using System.Text;
using System.Text.Json;

namespace UsageMonitor.Core.Providers.Antigravity;

public sealed record AntigravityToken(
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string Source,
    string? ClientId = null,
    string? ClientSecret = null)
{
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(AccessToken);
    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);
    public bool IsUsable(DateTimeOffset now, TimeSpan buffer) => HasAccessToken && (ExpiresAt is null || ExpiresAt > now.Add(buffer));
}

/// <summary>
/// Reads the credentials already created by Antigravity, agy, or Gemini CLI. Windows builds use
/// Credential Manager for the Antigravity target, while Gemini CLI keeps the same OAuth envelope in
/// .gemini/oauth_creds.json. No credential is written back or copied into Usage Monitor settings.
/// </summary>
public sealed class AntigravityAuthStore
{
    public const string CredentialTarget = "gemini:antigravity";
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(1);
    private readonly IProviderFileSystem _files;
    private readonly Func<string> _profile;
    private readonly Func<string?> _credentialReader;

    public AntigravityAuthStore(IProviderFileSystem? files = null, Func<string>? profile = null,
        Func<string?>? credentialReader = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _profile = profile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _credentialReader = credentialReader ?? (() => WindowsCredentialReader.ReadGeneric(CredentialTarget));
    }

    public IReadOnlyList<AntigravityToken> LoadCandidates()
    {
        var candidates = new List<AntigravityToken>();
        var raw = _credentialReader();
        if (TryParse(raw, "windows-credential", out var credential)) candidates.Add(credential);

        var profile = _profile();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var path = Path.Combine(profile, ".gemini", "oauth_creds.json");
            if (_files.FileExists(path) && TryParse(_files.ReadAllText(path), "gemini-oauth-file", out var file)) candidates.Add(file);
        }

        return candidates.Where(x => x.HasAccessToken || x.HasRefreshToken)
            .GroupBy(x => $"{x.AccessToken}\u001f{x.RefreshToken}", StringComparer.Ordinal)
            .Select(x => x.First())
            .ToArray();
    }

    public static bool TryParse(string? raw, string source, out AntigravityToken token)
    {
        token = new AntigravityToken(null, null, null, source);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim().Trim('\0', '\uFEFF');
        if (text.StartsWith("go-keyring-base64:", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = text[(text.IndexOf(':') + 1)..];
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
            catch (FormatException) { return false; }
        }

        if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            text = text["Bearer ".Length..].Trim();

        if (text.StartsWith("{", StringComparison.Ordinal))
        {
            using var document = ProviderJson.Parse(text);
            if (document is null) return false;
            var parsed = ParseObject(document.RootElement, source);
            if (parsed is null) return false;
            token = parsed;
            return token.HasAccessToken || token.HasRefreshToken;
        }

        token = new AntigravityToken(text, null, null, source);
        return token.HasAccessToken;
    }

    private static AntigravityToken? ParseObject(JsonElement root, string source)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var nested = ProviderJson.Object(ProviderJson.Property(root, "token"));
        var value = nested ?? root;
        var access = ProviderJson.String(ProviderJson.Property(value, "access_token", "accessToken", "bearerToken", "id_token", "idToken"));
        var refresh = ProviderJson.String(ProviderJson.Property(value, "refresh_token", "refreshToken"));
        var expiry = ProviderJson.Date(ProviderJson.Property(value, "expiry", "expiry_date", "expires_at", "expiresAt"));
        var clientId = ProviderJson.String(ProviderJson.Property(value, "client_id", "clientId")) ??
            ProviderJson.String(ProviderJson.Property(root, "client_id", "clientId"));
        var clientSecret = ProviderJson.String(ProviderJson.Property(value, "client_secret", "clientSecret")) ??
            ProviderJson.String(ProviderJson.Property(root, "client_secret", "clientSecret"));
        if (!string.IsNullOrWhiteSpace(access) || !string.IsNullOrWhiteSpace(refresh))
            return new AntigravityToken(access?.Trim(), refresh?.Trim(), expiry, source, clientId?.Trim(), clientSecret?.Trim());

        foreach (var name in new[] { "oauth", "oauth2", "credentials", "auth", "tokens" })
        {
            var child = ProviderJson.Object(ProviderJson.Property(root, name));
            var parsed = child is { } element ? ParseObject(element, source) : null;
            if (parsed is not null) return parsed;
        }
        return null;
    }

    public static bool IsUsable(AntigravityToken token, DateTimeOffset now) => token.IsUsable(now, RefreshBuffer);
}
