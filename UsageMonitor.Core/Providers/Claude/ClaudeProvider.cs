using System.Net.Http;

namespace UsageMonitor.Core.Providers.Claude;

public sealed class ClaudeProvider : IUsageProvider
{
    public static readonly ProviderDescriptor Provider = new(ProviderIds.ClaudeCode, "Claude Code", "claude", new[] { new ProviderLink("Claude", "https://claude.ai") });
    public ProviderDescriptor Descriptor => Provider;

    private readonly ClaudeAuthStore _auth;
    private readonly IProviderHttpClient _http;
    private readonly IProviderFileSystem _files;
    private readonly IModelCatalog? _catalog;
    private ClaudeMappedUsage? _lastGoodUsage;
    private DateTimeOffset? _rateLimitedUntil;
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);

    public ClaudeProvider(ClaudeAuthStore? auth = null, IProviderHttpClient? http = null, IProviderFileSystem? files = null, IModelCatalog? catalog = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _auth = auth ?? new ClaudeAuthStore(_files);
        _http = http ?? new ProviderHttpClient();
        _catalog = catalog;
    }

    public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        var candidates = _auth.LoadCandidates().Where(x => x.HasAccessToken).ToArray();
        if (candidates.Length == 0)
            return ProviderSnapshot.Error(Provider,
                "Claude Code is signed out on this Windows account. Run `claude auth login`, then refresh.",
                ProviderErrorCategory.NotConfigured);

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

                // Claude Code owns OAuth refresh-token rotation. TokenBurn deliberately reads the
                // current access token but never calls the token endpoint or writes
                // .credentials.json. Claude Code can then refresh its own credential without a
                // second process racing it with a stale refresh token.
                var token = state.OAuth.AccessToken!.Trim();

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

                var mapped = ClaudeUsageMapper.Map(response, state.OAuth, context.Now);
                var history = ScanHistory(state.Path, context.Now);
                var warning = mapped.Warning;
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
                    _rateLimitedUntil = null;
                }
                return ProviderSnapshot.Success(Provider, mapped.Lines, mapped.Plan, context.Now, history, warning);
            }
            catch (ClaudeAuthenticationException ex)
            {
                // A stale file can coexist with a fresh login. Try the next local candidate before
                // surfacing an auth error, matching the source fallback behavior.
                lastAuthentication ??= ex;
                continue;
            }
            catch (ClaudeRequestException ex)
            {
                if (ex.StatusCode is 401 or 403)
                {
                    lastAuthentication ??= new ClaudeAuthenticationException("Claude session expired. Run `claude auth login`, then refresh.");
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

    private sealed record OAuthEndpoints(Uri UsageUri);

    /// <summary>
    /// Resolve the same OAuth endpoint branches Claude Code uses. This matters on Windows because
    /// enterprise and local Claude installations commonly set a custom OAuth base or staging mode.
    /// Falling back to production for a malformed override would be dangerous, so an invalid
    /// override is ignored only when it cannot form an absolute URI at all.
    /// </summary>
    private static OAuthEndpoints ResolveOAuthEndpoints()
    {
        var baseApi = "https://api.anthropic.com";
        var userType = Environment.GetEnvironmentVariable("USER_TYPE");
        var isAntUser = string.Equals(userType?.Trim(), "ant", StringComparison.OrdinalIgnoreCase);
        var useLocal = IsTruthy(Environment.GetEnvironmentVariable("USE_LOCAL_OAUTH"));
        var useStaging = IsTruthy(Environment.GetEnvironmentVariable("USE_STAGING_OAUTH"));

        if (isAntUser && useLocal)
        {
            baseApi = Environment.GetEnvironmentVariable("CLAUDE_LOCAL_OAUTH_API_BASE")?.Trim().TrimEnd('/')
                ?? "http://localhost:8000";
        }
        else if (isAntUser && useStaging)
        {
            baseApi = "https://api-staging.anthropic.com";
        }

        var custom = Environment.GetEnvironmentVariable("CLAUDE_CODE_CUSTOM_OAUTH_URL")?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(custom))
        {
            baseApi = custom;
        }

        if (!Uri.TryCreate(baseApi + "/api/oauth/usage", UriKind.Absolute, out var usageUri))
        {
            usageUri = new Uri("https://api.anthropic.com/api/oauth/usage");
        }

        return new OAuthEndpoints(usageUri);
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
        return new ClaudeLogUsageScanner(_files, _catalog).Scan(home, now.AddDays(-90));
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

}
