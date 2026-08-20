using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace UsageMonitor.Core.Providers.Antigravity;

/// <summary>
/// Windows-native Antigravity quota provider. It follows the established strategy order but uses the
/// Windows Credential Manager and Gemini CLI OAuth file instead of macOS Keychain.
/// </summary>
public sealed class AntigravityProvider : IUsageProvider
{
    public static readonly ProviderDescriptor Provider = new(
        ProviderIds.Antigravity,
        "Antigravity",
        "antigravity",
        new[] { new ProviderLink("Antigravity", "https://antigravity.google/") });

    private static readonly Uri[] CloudCodeBases =
    [
        new("https://daily-cloudcode-pa.googleapis.com"),
        new("https://cloudcode-pa.googleapis.com")
    ];

    private const string FetchModelsPath = "/v1internal:fetchAvailableModels";
    private const string LoadAssistPath = "/v1internal:loadCodeAssist";
    private const string RetrieveQuotaPath = "/v1internal:retrieveUserQuota";
    private const string QuotaSummaryPath = "/v1internal:retrieveUserQuotaSummary";
    private const string GoogleTokenUri = "https://oauth2.googleapis.com/token";
    private const string GoogleClientIdEnvironmentVariable = "TOKENBURN_GOOGLE_CLIENT_ID";
    private const string GoogleClientSecretEnvironmentVariable = "TOKENBURN_GOOGLE_CLIENT_SECRET";

    public ProviderDescriptor Descriptor => Provider;
    private readonly AntigravityAuthStore _auth;
    private readonly IProviderHttpClient _http;
    private readonly AntigravityCliUsageScanner _history;
    // The Antigravity app keeps its refresh token in the credential store but rotates the short-lived
    // access token in memory. Keep the same safe, process-local cache so a 5-minute refresh does not
    // repeatedly hit Google's OAuth endpoint, while never writing a derived token back to disk.
    private string? _cachedAccessToken;
    private DateTimeOffset _cachedAccessTokenExpiresAt;
    private string? _cachedRefreshFingerprint;

    public AntigravityProvider(AntigravityAuthStore? auth = null, IProviderHttpClient? http = null,
        AntigravityCliUsageScanner? history = null)
    {
        _auth = auth ?? new AntigravityAuthStore();
        _http = http ?? new ProviderHttpClient();
        _history = history ?? new AntigravityCliUsageScanner();
    }

    public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var candidates = _auth.LoadCandidates();
        if (candidates.Count == 0)
            return ProviderSnapshot.Error(Provider, "Not configured. Sign in to Antigravity or Gemini CLI first.", ProviderErrorCategory.NotConfigured);

        var sawAuthFailure = false;
        foreach (var candidate in candidates)
        {
            try
            {
                var token = candidate.AccessToken?.Trim();
                var refreshAttempted = false;
                if (!AntigravityAuthStore.IsUsable(candidate, context.Now) && candidate.HasRefreshToken)
                {
                    // Try a still-present access token first. Some Antigravity builds leave the
                    // expiry field stale even though Cloud Code continues accepting the token.
                    token = GetCachedToken(candidate.RefreshToken!, context.Now) ?? token;
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        refreshAttempted = true;
                        var outcome = await RefreshGoogleTokenAsync(candidate, context, cancellationToken).ConfigureAwait(false);
                        if (outcome.Result is { } refreshed)
                        {
                            token = refreshed.AccessToken;
                            CacheToken(refreshed, candidate.RefreshToken!);
                        }
                        else if (outcome.RefreshTokenRejected)
                        {
                            sawAuthFailure = true;
                            continue;
                        }
                    }
                }
                if (!refreshAttempted && string.IsNullOrWhiteSpace(token) && candidate.HasRefreshToken)
                {
                    var outcome = await RefreshGoogleTokenAsync(candidate, context, cancellationToken).ConfigureAwait(false);
                    if (outcome.Result is { } refreshed)
                    {
                        token = refreshed.AccessToken;
                        CacheToken(refreshed, candidate.RefreshToken!);
                    }
                    else if (outcome.RefreshTokenRejected)
                    {
                        sawAuthFailure = true;
                        continue;
                    }
                }
                if (string.IsNullOrWhiteSpace(token)) continue;
                var tokenMs = stopwatch.ElapsedMilliseconds;

                var result = await FetchUsageAsync(token!, context, cancellationToken).ConfigureAwait(false);
                var apiMs = stopwatch.ElapsedMilliseconds;
                var history = _history.Scan(context.Now, context.ModelCatalog, cancellationToken);
                context.Logger?.Info("Antigravity usage refreshed",
                    new Dictionary<string, object?>
                    {
                        ["metricCount"] = result.Lines.Count,
                        ["historyPoints"] = history.Points.Count,
                        ["historyCostUsd"] = history.TotalCostUsd,
                        ["tokenMs"] = tokenMs,
                        ["apiMs"] = apiMs,
                        ["historyMs"] = stopwatch.ElapsedMilliseconds - apiMs
                    });
                return ProviderSnapshot.Success(Provider, result.Lines, result.Plan, context.Now,
                    history: history);
            }
            catch (AntigravityAuthenticationException)
            {
                // Access tokens can be revoked before their local expiry metadata catches up. A
                // refresh-token grant is the same recovery path the Antigravity client uses after a
                // 401, so retry exactly once before surfacing an expired-login card.
                if (candidate.HasRefreshToken)
                {
                    try
                    {
                        var outcome = await RefreshGoogleTokenAsync(candidate, context, cancellationToken).ConfigureAwait(false);
                        if (outcome.Result is { } refreshed)
                        {
                            CacheToken(refreshed, candidate.RefreshToken!);
                            var recovered = await FetchUsageAsync(refreshed.AccessToken, context, cancellationToken).ConfigureAwait(false);
                            var recoveredHistory = _history.Scan(context.Now, context.ModelCatalog, cancellationToken);
                            context.Logger?.Info("Antigravity usage refreshed",
                                new Dictionary<string, object?>
                                {
                                    ["metricCount"] = recovered.Lines.Count,
                                    ["historyPoints"] = recoveredHistory.Points.Count,
                                    ["recoveredFromAuthFailure"] = true,
                                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds
                                });
                            return ProviderSnapshot.Success(Provider, recovered.Lines, recovered.Plan, context.Now,
                                history: recoveredHistory);
                        }
                        // A refresh token that is genuinely rejected (invalid_grant) means the
                        // session ended; only then is "sign-in expired" truthful. Otherwise the
                        // access token failed but the session is intact, which is a transient
                        // condition rather than a signed-out state.
                        sawAuthFailure |= outcome.RefreshTokenRejected;
                    }
                    catch (AntigravityAuthenticationException) { }
                }
                else
                {
                    // No refresh token to recover with, so a rejected access token is a real
                    // sign-out even though the credential envelope still exists.
                    sawAuthFailure = true;
                }
            }
            catch (AntigravityRequestException ex)
            {
                return ErrorWithHistory(ex.Message,
                    ex.StatusCode == 429 ? ProviderErrorCategory.RateLimited : ProviderErrorCategory.Network,
                    context, cancellationToken, stopwatch.ElapsedMilliseconds);
            }
            catch (AntigravityParseException ex) { return ErrorWithHistory(ex.Message, ProviderErrorCategory.Parse, context, cancellationToken, stopwatch.ElapsedMilliseconds); }
            catch (HttpRequestException) { return ErrorWithHistory("Antigravity connection failed.", ProviderErrorCategory.Network, context, cancellationToken, stopwatch.ElapsedMilliseconds); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ErrorWithHistory("Antigravity request timed out.", ProviderErrorCategory.Network, context, cancellationToken, stopwatch.ElapsedMilliseconds); }
        }

        return ErrorWithHistory(
            sawAuthFailure ? "Antigravity sign-in expired. Open Antigravity or Gemini CLI and sign in again." : "Antigravity usage is temporarily unavailable.",
            sawAuthFailure ? ProviderErrorCategory.Authentication : ProviderErrorCategory.Network,
            context, cancellationToken, stopwatch.ElapsedMilliseconds);
    }

    private async Task<(string? Plan, IReadOnlyList<MetricLine> Lines)> FetchUsageAsync(string token, ProviderContext context, CancellationToken cancellationToken)
    {
        var summary = await CallCloudCodeAsync(QuotaSummaryPath, token, context, cancellationToken).ConfigureAwait(false);
        if (summary.StatusCode is 401 or 403) throw new AntigravityAuthenticationException();
        if (summary.StatusCode is >= 200 and < 300)
        {
            var lines = AntigravityUsageMapper.ParseQuotaSummary(summary.Body);
            if (lines is not null)
            {
                var planResponse = await CallCloudCodeAsync(LoadAssistPath, token, context, cancellationToken).ConfigureAwait(false);
                return (planResponse.StatusCode is >= 200 and < 300 ? AntigravityUsageMapper.ParsePlan(planResponse.Body) : null, lines);
            }
        }
        if (summary.StatusCode != 404 && (summary.StatusCode < 200 || summary.StatusCode > 299))
            ThrowForStatus(summary);

        var models = await CallCloudCodeAsync(FetchModelsPath, token, context, cancellationToken).ConfigureAwait(false);
        if (models.StatusCode is 401 or 403) throw new AntigravityAuthenticationException();
        if (models.StatusCode is >= 200 and < 300)
        {
            var lines = AntigravityUsageMapper.ParseAvailableModels(models.Body);
            if (lines.Count > 0) return (await LoadPlanAsync(token, context, cancellationToken).ConfigureAwait(false), lines);
        }

        var assist = await CallCloudCodeAsync(LoadAssistPath, token, context, cancellationToken,
            userAgent: "agy").ConfigureAwait(false);
        if (assist.StatusCode is 401 or 403) throw new AntigravityAuthenticationException();
        string? plan = assist.StatusCode is >= 200 and < 300 ? AntigravityUsageMapper.ParsePlan(assist.Body) : null;
        var project = assist.StatusCode is >= 200 and < 300 ? ParseProject(assist.Body) : null;
        var quota = await CallCloudCodeAsync(RetrieveQuotaPath, token, context, cancellationToken,
            project is null ? "{}" : $"{{\"project\":{JsonSerializer.Serialize(project)}}}", userAgent: "agy").ConfigureAwait(false);
        if (quota.StatusCode is 401 or 403) throw new AntigravityAuthenticationException();
        // Some older Cloud Code builds reject the project envelope even though the same token is
        // valid. Retry the documented empty body before giving up on the legacy path.
        if (project is not null && (quota.StatusCode is < 200 or >= 300))
        {
            quota = await CallCloudCodeAsync(RetrieveQuotaPath, token, context, cancellationToken,
                "{}", userAgent: "agy").ConfigureAwait(false);
            if (quota.StatusCode is 401 or 403) throw new AntigravityAuthenticationException();
        }
        if (quota.StatusCode is >= 200 and < 300)
        {
            var lines = AntigravityUsageMapper.ParseQuotaBuckets(quota.Body);
            if (lines.Count > 0) return (plan, lines);
        }
        if (summary.StatusCode >= 400 && models.StatusCode >= 400 && quota.StatusCode >= 400) ThrowForStatus(quota);
        return (plan, Array.Empty<MetricLine>());
    }

    private async Task<string?> LoadPlanAsync(string token, ProviderContext context, CancellationToken cancellationToken)
    {
        var response = await CallCloudCodeAsync(LoadAssistPath, token, context, cancellationToken).ConfigureAwait(false);
        return response.StatusCode is >= 200 and < 300 ? AntigravityUsageMapper.ParsePlan(response.Body) : null;
    }

    private async Task<ProviderHttpResponse> CallCloudCodeAsync(string path, string token, ProviderContext context,
        CancellationToken cancellationToken, string body = "{}", string userAgent = "antigravity")
    {
        ProviderHttpResponse? last = null;
        foreach (var baseUri in CloudCodeBases)
        {
            var response = await _http.SendAsync(HttpMethod.Post, new Uri(baseUri, path), new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["Authorization"] = $"Bearer {token}",
                ["User-Agent"] = userAgent,
                ["Content-Type"] = "application/json"
            }, body, "application/json", context.Proxy, cancellationToken).ConfigureAwait(false);
            last = response;
            if (response.StatusCode is 401 or 403 || response.StatusCode is >= 200 and < 300) return response;
        }
        return last ?? new ProviderHttpResponse(503, new Dictionary<string, string>(), string.Empty);
    }

    private async Task<GoogleRefreshOutcome> RefreshGoogleTokenAsync(AntigravityToken candidate, ProviderContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.RefreshToken))
            return new GoogleRefreshOutcome(null, RefreshTokenRejected: false);

        // The refresh token is bound to the OAuth client that issued it. Try the credential's own
        // client credentials, then the environment override. An invalid_grant response means the
        // refresh token itself was rejected (the session is genuinely over), which no other client
        // can fix, so that stops the loop.
        var candidates = new List<(string ClientId, string ClientSecret)>();
        var credentialClientId = candidate.ClientId;
        var credentialClientSecret = candidate.ClientSecret;
        if (!string.IsNullOrWhiteSpace(credentialClientId) && !string.IsNullOrWhiteSpace(credentialClientSecret))
            candidates.Add((credentialClientId.Trim(), credentialClientSecret.Trim()));
        var environmentClientId = Environment.GetEnvironmentVariable(GoogleClientIdEnvironmentVariable);
        var environmentClientSecret = Environment.GetEnvironmentVariable(GoogleClientSecretEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentClientId) && !string.IsNullOrWhiteSpace(environmentClientSecret) &&
            !candidates.Any(pair => pair.ClientId == environmentClientId.Trim() && pair.ClientSecret == environmentClientSecret.Trim()))
            candidates.Add((environmentClientId.Trim(), environmentClientSecret.Trim()));
        foreach (var client in candidates)
        {
            var form = $"client_id={Uri.EscapeDataString(client.ClientId)}&client_secret={Uri.EscapeDataString(client.ClientSecret)}&refresh_token={Uri.EscapeDataString(candidate.RefreshToken)}&grant_type=refresh_token";
            var response = await _http.SendAsync(HttpMethod.Post, new Uri(GoogleTokenUri),
                new Dictionary<string, string> { ["Accept"] = "application/json" }, form, "application/x-www-form-urlencoded", context.Proxy, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is >= 200 and < 300)
            {
                using var document = ProviderJson.Parse(response.Body);
                var access = ProviderJson.String(ProviderJson.Property(document?.RootElement ?? default, "access_token", "accessToken"));
                if (!string.IsNullOrWhiteSpace(access))
                {
                    var expiresIn = ProviderJson.Number(ProviderJson.Property(document?.RootElement ?? default, "expires_in", "expiresIn")) ?? 3600;
                    return new GoogleRefreshOutcome(
                        new GoogleRefreshResult(access.Trim(), context.Now.AddSeconds(Math.Max(60, expiresIn))),
                        RefreshTokenRejected: false);
                }
            }
            if (response.StatusCode is 400 or 401 && IsInvalidGrant(response.Body))
                return new GoogleRefreshOutcome(null, RefreshTokenRejected: true);
            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                if (response.StatusCode is 400 or 401) continue;
                throw new AntigravityRequestException(response.StatusCode);
            }
        }

        return new GoogleRefreshOutcome(null, RefreshTokenRejected: false);
    }

    private static bool IsInvalidGrant(string body)
    {
        try
        {
            using var document = ProviderJson.Parse(body);
            var error = ProviderJson.String(ProviderJson.Property(document?.RootElement ?? default, "error"));
            return error is not null &&
                   error.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
        }
        catch (AntigravityParseException) { return false; }
    }

    private string? GetCachedToken(string refreshToken, DateTimeOffset now)
    {
        if (_cachedAccessToken is null || _cachedAccessTokenExpiresAt <= now ||
            !string.Equals(_cachedRefreshFingerprint, Fingerprint(refreshToken), StringComparison.Ordinal))
            return null;
        return _cachedAccessToken;
    }

    private void CacheToken(GoogleRefreshResult refreshed, string refreshToken)
    {
        _cachedAccessToken = refreshed.AccessToken;
        _cachedAccessTokenExpiresAt = refreshed.ExpiresAt;
        _cachedRefreshFingerprint = Fingerprint(refreshToken);
    }

    private static string Fingerprint(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];

    private sealed record GoogleRefreshResult(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record GoogleRefreshOutcome(GoogleRefreshResult? Result, bool RefreshTokenRejected);

    private static string? ParseProject(string body)
    {
        using var document = ProviderJson.Parse(body);
        return ProviderJson.String(ProviderJson.Property(document?.RootElement ?? default, "cloudaicompanionProject", "project"));
    }

    private ProviderSnapshot ErrorWithHistory(string message, ProviderErrorCategory category,
        ProviderContext context, CancellationToken cancellationToken, long elapsedMs = 0)
    {
        var history = _history.Scan(context.Now, context.ModelCatalog, cancellationToken);
        if (elapsedMs > 0)
        {
            context.Logger?.Info("Antigravity refresh failed",
                new Dictionary<string, object?>
                {
                    ["errorCategory"] = category,
                    ["elapsedMs"] = elapsedMs,
                    ["historyPoints"] = history.Points.Count
                });
        }
        return ProviderSnapshot.Error(Provider, message, category) with { UsageHistory = history };
    }

    private static void ThrowForStatus(ProviderHttpResponse response)
    {
        if (response.StatusCode is 401 or 403) throw new AntigravityAuthenticationException();
        throw new AntigravityRequestException(response.StatusCode);
    }
}

public sealed class AntigravityAuthenticationException : Exception
{
    public AntigravityAuthenticationException() : base("Antigravity sign-in expired.") { }
}

public sealed class AntigravityParseException(string message) : Exception(message);
public sealed class AntigravityRequestException(int statusCode) : Exception($"Antigravity usage request failed ({statusCode}).")
{
    public int StatusCode { get; } = statusCode;
}
