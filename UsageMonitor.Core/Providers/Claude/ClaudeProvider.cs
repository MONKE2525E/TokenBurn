using System.Net.Http;

namespace UsageMonitor.Core.Providers.Claude;

public sealed class ClaudeProvider : IUsageProvider
{
    public static readonly ProviderDescriptor Provider = new(ProviderIds.ClaudeCode, "Claude Code", "claude", new[] { new ProviderLink("Claude", "https://claude.ai") });
    public ProviderDescriptor Descriptor => Provider;

    private readonly ClaudeAuthStore _auth;
    private readonly IProviderHttpClient _http;
    private readonly IProviderFileSystem _files;
    private ClaudeMappedUsage? _lastGoodUsage;
    private DateTimeOffset? _rateLimitedUntil;
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);

    public ClaudeProvider(ClaudeAuthStore? auth = null, IProviderHttpClient? http = null, IProviderFileSystem? files = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _auth = auth ?? new ClaudeAuthStore(_files);
        _http = http ?? new ProviderHttpClient();
    }

    public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        var candidates = _auth.LoadCandidates().Where(x => x.HasAccessToken).ToArray();
        if (candidates.Length == 0)
            return ProviderSnapshot.Error(Provider, "Not configured. Run `claude` to authenticate.", ProviderErrorCategory.NotConfigured);

        ClaudeAuthenticationException? lastAuthentication = null;
        foreach (var state in candidates)
        {
            try
            {
                if (_rateLimitedUntil is { } limitedUntil && limitedUntil > context.Now)
                {
                    var cachedHistory = ScanHistory(state.Path, context.Now);
                    if (_lastGoodUsage is { } cachedUsage)
                        return ProviderSnapshot.Success(Provider, cachedUsage.Lines, cachedUsage.Plan, context.Now, cachedHistory,
                            $"Claude updates are rate limited. Showing the last good limits until {limitedUntil.LocalDateTime:t}.");

                    return ErrorWithHistory(
                        $"Claude updates are rate limited until {limitedUntil.LocalDateTime:t}. Local spend history is still available.",
                        ProviderErrorCategory.RateLimited,
                        candidates,
                        context.Now);
                }

                var token = state.OAuth.AccessToken!.Trim();
                var refreshWasRateLimited = false;
                var refreshWasRejected = false;
                if (!state.InferenceOnly && ClaudeAuthStore.NeedsRefresh(state.OAuth, context.Now))
                {
                    // Do not reject solely from the cached refreshTokenExpiresAt metadata. Claude
                    // has changed the meaning/units of that field across CLI releases, and an
                    // apparently expired local value can mask a live HTTP 429 from the OAuth service.
                    // Let the token endpoint decide: invalid_grant is a real auth failure, while a
                    // throttle must remain a rate-limit state with local history visible.
                    if (string.IsNullOrWhiteSpace(state.OAuth.RefreshToken))
                        throw new ClaudeAuthenticationException("Claude session expired. Run `claude` to log in again.");
                    try
                    {
                        var refreshed = await RefreshTokenAsync(state.OAuth.RefreshToken!, context, cancellationToken).ConfigureAwait(false);
                        token = refreshed.AccessToken;
                        _auth.TryPersistTokens(state.Path, refreshed.AccessToken, refreshed.RefreshToken, refreshed.ExpiresAt);
                    }
                    catch (ClaudeRequestException ex) when (ex.StatusCode == 429)
                    {
                        // Anthropic can rate-limit the refresh endpoint while the current access
                        // token is still accepted by the usage endpoint. Try that token once
                        // before declaring Claude unavailable, then keep the stale warning explicit.
                        ExtendRateLimit(context.Now, ex.RetryAfterSeconds);
                        refreshWasRateLimited = true;
                    }
                    catch (ClaudeAuthenticationException ex)
                    {
                        // A refresh token can be revoked or rotated while the access token still
                        // has server-side grace time left. Probe that access token once before
                        // declaring the account unavailable. The usage endpoint remains the source
                        // of truth, so a real 401/403 still falls through to the re-login message.
                        refreshWasRejected = true;
                        lastAuthentication = ex;
                    }
                }

                // setup-token and other inference-only credentials are valid for local spend
                // accounting but cannot access Anthropic's subscription usage endpoint.
                if (state.InferenceOnly || !state.HasProfileScope)
                {
                    var historyOnly = ScanHistory(state.Path, context.Now);
                    return ProviderSnapshot.Success(Provider, Array.Empty<MetricLine>(), state.OAuth.SubscriptionType, context.Now, historyOnly,
                        "Re-login with `claude` to restore live Session and Weekly limits. Local spend history is still available.");
                }

                var headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {token}",
                    ["Accept"] = "application/json",
                    ["Content-Type"] = "application/json",
                    ["anthropic-beta"] = "oauth-2025-04-20",
                    ["User-Agent"] = "claude-code/2.1.220"
                };
                var endpoints = ResolveOAuthEndpoints();
                var response = await _http.SendAsync(HttpMethod.Get, endpoints.UsageUri, headers, proxy: context.Proxy, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (response is null) throw new ClaudeRequestException(429);
                var retryAfterSeconds = response.StatusCode == 429
                    ? ClaudeUsageMapper.ParseRetryAfterSeconds(response.Header("retry-after"), context.Now)
                    : null;

                // If Anthropic rate-limited the refresh endpoint, an expired local access
                // token can make the follow-up usage probe return 401/403 as well. That does
                // not prove the refresh token is invalid. Classify the pair as a rate limit so
                // the UI keeps history visible and does not falsely ask the user to sign in.
                if (refreshWasRateLimited && (response.StatusCode is 401 or 403))
                {
                    ExtendRateLimit(context.Now, retryAfterSeconds);
                    var rateLimitedHistory = ScanHistory(state.Path, context.Now);
                    if (_lastGoodUsage is { } lastGoodAfterRateLimit)
                        return ProviderSnapshot.Success(Provider, lastGoodAfterRateLimit.Lines, lastGoodAfterRateLimit.Plan, context.Now,
                            rateLimitedHistory,
                            "Claude live limits are temporarily rate limited. Showing the last good limits and local history.");

                    return ErrorWithHistory(
                        "Claude live limits are temporarily unavailable because Anthropic is rate limiting OAuth requests. Local spend history is still available.",
                        ProviderErrorCategory.RateLimited,
                        candidates,
                        context.Now);
                }

                var mapped = ClaudeUsageMapper.Map(response, state.OAuth, context.Now);
                var history = ScanHistory(state.Path, context.Now);
                var warning = mapped.Warning ?? (refreshWasRateLimited
                    ? "Claude token refresh is rate limited. Showing the current session token while it remains valid."
                    : refreshWasRejected
                        ? "Claude refresh was rejected, but the current access token is still accepted. Re-login before it expires."
                        : null);
                if (mapped.Warning is not null)
                {
                    ExtendRateLimit(context.Now, retryAfterSeconds);
                    if (_lastGoodUsage is { } lastGood)
                        return ProviderSnapshot.Success(Provider, lastGood.Lines, lastGood.Plan, context.Now, history, warning);

                    // Do not turn a rate-limited response into a successful two-line snapshot. That
                    // would overwrite the disk cache and erase the last real Session/Weekly bars.
                    // CoreUsageSnapshotSource can now retain a prior successful envelope and attach
                    // this warning without pretending that the badge is live usage.
                    return ProviderSnapshot.Error(Provider,
                        warning ?? "Claude usage is temporarily rate limited. Cached limits remain visible.",
                        ProviderErrorCategory.RateLimited) with
                    {
                        Plan = mapped.Plan,
                        UsageHistory = history,
                        Warning = warning
                    };
                }
                else if (mapped.Lines.Any(line => line.Type == MetricLineType.Progress))
                {
                    _lastGoodUsage = mapped;
                    if (!refreshWasRateLimited) _rateLimitedUntil = null;
                }
                return ProviderSnapshot.Success(Provider, mapped.Lines, mapped.Plan, context.Now, history, warning);
            }
            catch (ClaudeAuthenticationException ex)
            {
                // A stale file can coexist with a fresh login. Try the next local candidate before
                // surfacing an auth error, matching upstream OpenUsage's source fallback behavior.
                lastAuthentication ??= ex;
                continue;
            }
            catch (ClaudeRequestException ex)
            {
                if (ex.StatusCode is 401 or 403)
                {
                    lastAuthentication ??= new ClaudeAuthenticationException("Claude session expired. Run `claude` to log in again.");
                    continue;
                }
                if (ex.StatusCode == 429)
                {
                    ExtendRateLimit(context.Now, ex.RetryAfterSeconds);
                    if (_lastGoodUsage is { } lastGood)
                    {
                        var history = ScanHistory(state.Path, context.Now);
                        return ProviderSnapshot.Success(Provider, lastGood.Lines, lastGood.Plan, context.Now, history,
                            "Claude token refresh is rate limited. Showing the last good limits for five minutes.");
                    }
                    return ErrorWithHistory("Claude token refresh is rate limited (HTTP 429). Try again in a few minutes.", ProviderErrorCategory.RateLimited, candidates, context.Now);
                }
                return ErrorWithHistory(ex.Message, ex.StatusCode == 429 ? ProviderErrorCategory.RateLimited : ProviderErrorCategory.Network, candidates, context.Now);
            }
            catch (ClaudeParseException ex) { return ErrorWithHistory(ex.Message, ProviderErrorCategory.Parse, candidates, context.Now); }
            catch (HttpRequestException) { return ErrorWithHistory("Claude connection failed.", ProviderErrorCategory.Network, candidates, context.Now); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ErrorWithHistory("Claude request timed out.", ProviderErrorCategory.Network, candidates, context.Now); }
        }

        return ErrorWithHistory(
            lastAuthentication?.Message ?? "Claude live limits are unavailable. Run `claude` to sign in again. Local spend history is still available.",
            ProviderErrorCategory.Authentication,
            candidates,
            context.Now);
    }

    private static readonly string ProductionClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly string NonProductionClientId = "22422756-60c9-4084-8eb7-27705fd5cf9a";

    private sealed record OAuthEndpoints(Uri UsageUri, Uri RefreshUri, string ClientId);

    /// <summary>
    /// Resolve the same OAuth endpoint branches Claude Code uses. This matters on Windows because
    /// enterprise and local Claude installations commonly set a custom OAuth base or staging mode.
    /// Falling back to production for a malformed override would be dangerous, so an invalid
    /// override is ignored only when it cannot form an absolute URI at all.
    /// </summary>
    private static OAuthEndpoints ResolveOAuthEndpoints()
    {
        var baseApi = "https://api.anthropic.com";
        var refreshApi = "https://platform.claude.com";
        var clientId = ProductionClientId;
        var userType = Environment.GetEnvironmentVariable("USER_TYPE");
        var isAntUser = string.Equals(userType?.Trim(), "ant", StringComparison.OrdinalIgnoreCase);
        var useLocal = IsTruthy(Environment.GetEnvironmentVariable("USE_LOCAL_OAUTH"));
        var useStaging = IsTruthy(Environment.GetEnvironmentVariable("USE_STAGING_OAUTH"));

        if (isAntUser && useLocal)
        {
            baseApi = Environment.GetEnvironmentVariable("CLAUDE_LOCAL_OAUTH_API_BASE")?.Trim().TrimEnd('/')
                ?? "http://localhost:8000";
            refreshApi = baseApi;
            clientId = NonProductionClientId;
        }
        else if (isAntUser && useStaging)
        {
            baseApi = "https://api-staging.anthropic.com";
            refreshApi = "https://platform.staging.ant.dev";
            clientId = NonProductionClientId;
        }

        var custom = Environment.GetEnvironmentVariable("CLAUDE_CODE_CUSTOM_OAUTH_URL")?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(custom))
        {
            baseApi = custom;
            refreshApi = custom;
        }

        var clientOverride = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_CLIENT_ID")?.Trim();
        if (!string.IsNullOrWhiteSpace(clientOverride)) clientId = clientOverride;

        if (!Uri.TryCreate(baseApi + "/api/oauth/usage", UriKind.Absolute, out var usageUri) ||
            !Uri.TryCreate(refreshApi + "/v1/oauth/token", UriKind.Absolute, out var refreshUri))
        {
            usageUri = new Uri("https://api.anthropic.com/api/oauth/usage");
            refreshUri = new Uri("https://platform.claude.com/v1/oauth/token");
            clientId = ProductionClientId;
        }

        return new OAuthEndpoints(usageUri, refreshUri, clientId);
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return !value.Trim().Equals("0", StringComparison.OrdinalIgnoreCase) &&
               !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) &&
               !value.Trim().Equals("no", StringComparison.OrdinalIgnoreCase) &&
               !value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private ProviderUsageHistory ScanHistory(string path, DateTimeOffset now)
    {
        var home = string.Equals(path, "environment", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Path.GetDirectoryName(path) ?? string.Empty;
        return new ClaudeLogUsageScanner(_files).Scan(home, now.AddDays(-30));
    }

    private ProviderSnapshot ErrorWithHistory(string message, ProviderErrorCategory category, IReadOnlyList<ClaudeAuthState> candidates, DateTimeOffset now)
    {
        var path = candidates.FirstOrDefault(x => !string.Equals(x.Path, "environment", StringComparison.OrdinalIgnoreCase))?.Path ?? "environment";
        var oauth = candidates.FirstOrDefault()?.OAuth;
        var plan = oauth is null ? null : ClaudeUsageMapper.FormatPlan(oauth.SubscriptionType, oauth.RateLimitTier);
        return ProviderSnapshot.Error(Provider, message, category) with
        {
            Plan = plan,
            UsageHistory = ScanHistory(path, now)
        };
    }

    private void ExtendRateLimit(DateTimeOffset now, int? retryAfterSeconds)
    {
        var candidate = now.AddSeconds(retryAfterSeconds ?? (int)RateLimitCooldown.TotalSeconds);
        if (_rateLimitedUntil is null || candidate > _rateLimitedUntil)
            _rateLimitedUntil = candidate;
    }

    private async Task<ClaudeRefreshResult> RefreshTokenAsync(string refreshToken, ProviderContext context, CancellationToken cancellationToken)
    {
        // Build the OAuth payload as JSON instead of interpolating a string. Apart from being
        // safer for unusual refresh-token characters, this avoids String.Format treating a
        // token or a JSON brace as a formatting hole. The previous implementation could throw
        // before the request was sent, making an otherwise valid Claude login look expired.
        var endpoints = ResolveOAuthEndpoints();
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            refresh_token = refreshToken,
            client_id = endpoints.ClientId,
            scope = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload"
        });
        var response = await _http.SendAsync(HttpMethod.Post, endpoints.RefreshUri,
            new Dictionary<string, string> { ["Accept"] = "application/json" }, body, "application/json", context.Proxy, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is 400 or 401)
        {
            var invalidGrant = response.Body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
            throw new ClaudeAuthenticationException(invalidGrant
                ? "Claude login expired. Run `claude auth login` to renew it."
                : "Claude session expired. Run `claude auth login` to renew it.");
        }
        if (response.StatusCode < 200 || response.StatusCode >= 300)
            throw new ClaudeRequestException(response.StatusCode,
                response.StatusCode == 429
                    ? ClaudeUsageMapper.ParseRetryAfterSeconds(response.Header("retry-after"), context.Now)
                    : null);
        using var doc = ProviderJson.Parse(response.Body) ?? throw new ClaudeParseException("Claude token response was invalid.");
        var token = ProviderJson.String(ProviderJson.Property(doc.RootElement, "access_token", "accessToken"));
        if (string.IsNullOrWhiteSpace(token)) throw new ClaudeAuthenticationException("Claude session expired. Run `claude` to log in again.");
        var rotatedRefresh = ProviderJson.String(ProviderJson.Property(doc.RootElement, "refresh_token", "refreshToken"));
        var expiresAt = ProviderJson.Number(ProviderJson.Property(doc.RootElement, "expires_at", "expiresAt"));
        if (expiresAt is null && ProviderJson.Number(ProviderJson.Property(doc.RootElement, "expires_in")) is { } expiresIn)
            expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)Math.Max(0, expiresIn);
        var expiry = expiresAt is { } raw
            ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Abs(raw) > 100_000_000_000 ? (long)raw : (long)(raw * 1000))
            : (DateTimeOffset?)null;
        return new ClaudeRefreshResult(token!.Trim(), rotatedRefresh?.Trim(), expiry);
    }

    private sealed record ClaudeRefreshResult(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);
}
