using System.Text.Json;
using UsageMonitor.Core.Providers;

namespace UsageMonitor.Core.Providers.Grok;

/// <summary>
/// Monitors Grok Build — xAI's terminal coding agent (the `grok` CLI). It uses the CLI's own
/// login state (`~/.grok/auth.json`, written by `grok login`) and reports the Grok Build coding
/// quota from the billing endpoint. No xAI API key is required.
/// </summary>
public sealed class GrokProvider : IUsageProvider
{
    /// <summary>Default OAuth2 client scope key in auth.json (issuer + client id).</summary>
    internal const string DefaultAuthScope = "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828";
    /// <summary>Pre-OIDC scope key kept for existing auth files.</summary>
    internal const string LegacyAuthScope = "https://accounts.x.ai/sign-in";
    /// <summary>Default CLI chat proxy base URL (matches the Grok installer and auth docs).</summary>
    internal const string DefaultProxyBaseUrl = "https://cli-chat-proxy.grok.com/v1";
    /// <summary>Value of the X-XAI-Token-Auth header the Grok CLI sends on proxy calls.</summary>
    internal const string TokenHeader = "xai-grok-cli";
    /// <summary>Informational client version header. Not a credential; the proxy ignores unknown values.</summary>
    internal const string ClientVersion = "0.1.42";

    private static readonly ProviderDescriptor Provider = new(
        ProviderIds.Grok, "Grok", "grok", [new ProviderLink("Grok Build", "https://x.ai/cli")]);

    private readonly IProviderHttpClient _http;
    private readonly string? _grokHome;

    public GrokProvider(IProviderHttpClient? http = null, string? grokHome = null)
    {
        _http = http ?? new ProviderHttpClient();
        _grokHome = grokHome;
    }

    public ProviderDescriptor Descriptor => Provider;

    public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        var auth = ReadGrokAuth(_grokHome ?? ResolveDefaultGrokHome());
        if (auth is null)
            return ProviderSnapshot.Error(Provider,
                "Not configured. Grok Build is not logged in. Run `grok login` first.",
                ProviderErrorCategory.NotConfigured);

        var endpoint = BuildBillingEndpoint();

        try
        {
            var response = await _http.SendAsync(HttpMethod.Get, endpoint,
                new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {auth.Key}",
                    ["X-XAI-Token-Auth"] = TokenHeader,
                    ["x-userid"] = auth.UserId,
                    ["x-grok-client-version"] = ClientVersion,
                    ["Accept"] = "application/json"
                },
                proxy: context.Proxy, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is 401 or 403)
                return ProviderSnapshot.Error(Provider,
                    "Your Grok Build session expired. Run `grok login` to sign in again.",
                    ProviderErrorCategory.Authentication);
            if (response.StatusCode == 429)
                return ProviderSnapshot.Error(Provider, "The Grok quota service is rate limited.", ProviderErrorCategory.RateLimited);
            if (response.StatusCode is < 200 or >= 300)
                return ProviderSnapshot.Error(Provider, "The Grok Build quota request failed.", ProviderErrorCategory.Network);

            var parsed = GrokBillingMapper.Map(response.Body);
            if (parsed is null)
                return ProviderSnapshot.Error(Provider, "Grok Build returned invalid quota data.", ProviderErrorCategory.Parse);
            return ProviderSnapshot.Success(Provider, parsed.Lines, parsed.Plan, context.Now);
        }
        catch (HttpRequestException)
        {
            return ProviderSnapshot.Error(Provider, "Grok Build quota connection failed.", ProviderErrorCategory.Network);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderSnapshot.Error(Provider, "The Grok Build quota request timed out.", ProviderErrorCategory.Network);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ProviderSnapshot.Error(Provider, "Grok Build returned invalid quota data.", ProviderErrorCategory.Parse);
        }
    }

    /// <summary>
    /// Builds the billing endpoint from the configured proxy base URL. A malformed
    /// GROK_CLI_CHAT_PROXY_BASE_URL (unparseable, non-http scheme, embedded whitespace) falls back
    /// to the documented default instead of turning the whole refresh into a generic parse error.
    /// </summary>
    internal static Uri BuildBillingEndpoint()
    {
        var proxyBase = Environment.GetEnvironmentVariable("GROK_CLI_CHAT_PROXY_BASE_URL");
        if (!string.IsNullOrWhiteSpace(proxyBase) &&
            Uri.TryCreate(proxyBase.Trim().TrimEnd('/') + "/billing?format=credits", UriKind.Absolute, out var candidate) &&
            (candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            return candidate;
        return new Uri(DefaultProxyBaseUrl + "/billing?format=credits");
    }

    internal static string ResolveDefaultGrokHome()
    {
        var env = Environment.GetEnvironmentVariable("GROK_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? ".grok" : Path.Combine(home, ".grok");
    }

    /// <summary>Reads a usable session/API-key credential from `~/.grok/auth.json`. Mirrors the CLI's
    /// lookup: prefer the default OAuth2 scope, fall back to the legacy scope, then any entry that
    /// is not a deprecated WebLogin token.</summary>
    internal static GrokAuthInfo? ReadGrokAuth(string grokHome)
    {
        var path = Path.Combine(grokHome, "auth.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = ProviderJson.Parse(File.ReadAllText(path));
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = document.RootElement;
            var found = ReadScope(root, DefaultAuthScope) ?? ReadScope(root, LegacyAuthScope);
            if (found is not null) return found;
            foreach (var property in root.EnumerateObject())
            {
                found = ReadEntry(property.Value);
                if (found is not null) return found;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static GrokAuthInfo? ReadScope(JsonElement root, string scope)
    {
        var entry = ProviderJson.Property(root, scope);
        return entry is null ? null : ReadEntry(entry.Value);
    }

    private static GrokAuthInfo? ReadEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var key = ProviderJson.String(ProviderJson.Property(entry, "key"));
        if (string.IsNullOrWhiteSpace(key)) return null;
        var mode = ProviderJson.String(ProviderJson.Property(entry, "auth_mode"));
        if (string.Equals(mode, "web_login", StringComparison.OrdinalIgnoreCase)) return null;
        var userId = ProviderJson.String(ProviderJson.Property(entry, "user_id")) ?? string.Empty;
        return new GrokAuthInfo(key, userId);
    }
}

internal sealed record GrokAuthInfo(string Key, string UserId);
