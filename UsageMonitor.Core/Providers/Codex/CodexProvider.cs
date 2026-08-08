using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageMonitor.Core.Providers.Codex;

public sealed class CodexProvider : IUsageProvider
{
    public static readonly ProviderDescriptor Provider = new("codex", "Codex", "codex", new[] { new ProviderLink("Codex", "https://chatgpt.com") });
    public ProviderDescriptor Descriptor => Provider;

    private readonly CodexAuthStore _auth;
    private readonly IProviderHttpClient _http;
    private readonly IProviderFileSystem _files;

    public CodexProvider(CodexAuthStore? auth = null, IProviderHttpClient? http = null, IProviderFileSystem? files = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _auth = auth ?? new CodexAuthStore(_files);
        _http = http ?? new ProviderHttpClient();
    }

    public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        var state = _auth.LoadCandidates().FirstOrDefault(x => x.HasAccessToken) ?? _auth.LoadCandidates().FirstOrDefault();
        if (state is null) return ProviderSnapshot.Error(Provider, "Not configured. Run `codex` to authenticate.", ProviderErrorCategory.NotConfigured);
        if (!state.HasAccessToken) return ProviderSnapshot.Error(Provider, "API-key login cannot provide Codex subscription usage.", ProviderErrorCategory.Unsupported);

        try
        {
            var accessToken = state.Auth.Tokens!.AccessToken!;
            if (CodexAuthStore.NeedsRefresh(state.Auth, context.Now) && !string.IsNullOrWhiteSpace(state.Auth.Tokens.RefreshToken))
            {
                var refreshed = await RefreshTokenAsync(state.Auth.Tokens.RefreshToken!, context, cancellationToken).ConfigureAwait(false);
                accessToken = refreshed.AccessToken;
            }
            var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {accessToken}", ["Accept"] = "application/json", ["User-Agent"] = "UsageMonitor/1.0" };
            if (!string.IsNullOrWhiteSpace(state.Auth.Tokens.AccountId)) headers["ChatGPT-Account-Id"] = state.Auth.Tokens.AccountId!;
            var response = await _http.SendAsync(HttpMethod.Get, new Uri("https://chatgpt.com/backend-api/wham/usage"), headers, proxy: context.Proxy, cancellationToken: cancellationToken).ConfigureAwait(false);
            var mapped = CodexUsageMapper.Map(response, context.Now);
            var lines = mapped.Lines.ToList();
            var home = Path.GetDirectoryName(state.Path) ?? string.Empty;
            var history = new CodexLogUsageScanner(_files).Scan(home, context.Now.AddDays(-30));
            return ProviderSnapshot.Success(Provider, lines, mapped.Plan, context.Now, history);
        }
        catch (CodexAuthenticationException ex) { return ErrorWithHistory(ex.Message, ProviderErrorCategory.Authentication, state.Path, context.Now); }
        catch (CodexRequestException ex) { return ErrorWithHistory(ex.Message, ex.StatusCode == 429 ? ProviderErrorCategory.RateLimited : ProviderErrorCategory.Network, state.Path, context.Now); }
        catch (CodexParseException ex) { return ErrorWithHistory(ex.Message, ProviderErrorCategory.Parse, state.Path, context.Now); }
        catch (HttpRequestException) { return ErrorWithHistory("Codex connection failed.", ProviderErrorCategory.Network, state.Path, context.Now); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ErrorWithHistory("Codex request timed out.", ProviderErrorCategory.Network, state.Path, context.Now); }
    }

    private ProviderSnapshot ErrorWithHistory(string message, ProviderErrorCategory category, string path, DateTimeOffset now)
    {
        var home = Path.GetDirectoryName(path) ?? string.Empty;
        return ProviderSnapshot.Error(Provider, message, category) with
        {
            UsageHistory = new CodexLogUsageScanner(_files).Scan(home, now.AddDays(-30))
        };
    }

    private async Task<CodexRefreshTokenResult> RefreshTokenAsync(string refreshToken, ProviderContext context, CancellationToken cancellationToken)
    {
        var form = $"grant_type=refresh_token&client_id=app_EMoamEEZ73f0CkXaXp7hrann&refresh_token={Uri.EscapeDataString(refreshToken)}";
        var response = await _http.SendAsync(HttpMethod.Post, new Uri("https://auth.openai.com/oauth/token"), new Dictionary<string, string> { ["Accept"] = "application/json" }, form, "application/x-www-form-urlencoded", context.Proxy, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is 400 or 401) throw new CodexAuthenticationException("Codex session expired. Run `codex` to log in again.");
        if (response.StatusCode < 200 || response.StatusCode >= 300) throw new CodexRequestException(response.StatusCode);
        using var doc = ProviderJson.Parse(response.Body) ?? throw new CodexParseException("Codex token response was invalid.");
        var token = ProviderJson.String(ProviderJson.Property(doc.RootElement, "access_token"));
        if (string.IsNullOrWhiteSpace(token)) throw new CodexAuthenticationException("Codex session expired. Run `codex` to log in again.");
        return new CodexRefreshTokenResult(token!);
    }

    private sealed record CodexRefreshTokenResult(string AccessToken);
}
