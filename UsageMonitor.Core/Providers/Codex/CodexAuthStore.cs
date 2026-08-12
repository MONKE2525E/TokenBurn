using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageMonitor.Core.Providers.Codex;

public sealed record CodexTokens
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("id_token")] public string? IdToken { get; init; }
    [JsonPropertyName("account_id")] public string? AccountId { get; init; }
}

public sealed record CodexAuth
{
    [JsonPropertyName("tokens")] public CodexTokens? Tokens { get; init; }
    [JsonPropertyName("last_refresh")] public string? LastRefresh { get; init; }
    [JsonPropertyName("OPENAI_API_KEY")] public string? ApiKey { get; init; }
}

public sealed record CodexAuthState(string Path, CodexAuth Auth)
{
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(Auth.Tokens?.AccessToken);
}

/// <summary>Reads Codex's Windows-native file login without ever logging token contents.</summary>
public sealed class CodexAuthStore
{
    private readonly Func<string, string?> _environment;
    private readonly IProviderFileSystem _files;
    private readonly Func<string> _userProfile;

    public CodexAuthStore(
        IProviderFileSystem? files = null,
        Func<string, string?>? environment = null,
        Func<string>? userProfile = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public IReadOnlyList<string> AuthPaths()
    {
        var candidates = new List<string>();
        var configured = _environment("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(Path.Combine(configured!, "auth.json"));

        var profile = _userProfile();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            candidates.Add(Path.Combine(profile, ".codex", "auth.json"));
            candidates.Add(Path.Combine(profile, ".config", "codex", "auth.json"));
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<CodexAuthState> LoadCandidates() => AuthPaths()
        .Select(path => TryLoad(path))
        .OfType<CodexAuthState>()
        .ToArray();

    public CodexAuthState? TryLoad(string path)
    {
        if (!_files.FileExists(path)) return null;
        var text = _files.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var auth = JsonSerializer.Deserialize<CodexAuth>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (auth is null || (!HasValue(auth.ApiKey) && !HasValue(auth.Tokens?.AccessToken) && !HasValue(auth.Tokens?.RefreshToken))) return null;
            return new CodexAuthState(path, auth);
        }
        catch (JsonException) { return null; }
    }

    public static DateTimeOffset? AccessTokenExpiresAt(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var segments = token.Split('.');
        if (segments.Length < 2) return null;
        try
        {
            var payload = Convert.FromBase64String(segments[1].Replace('-', '+').Replace('_', '/') + new string('=', (4 - segments[1].Length % 4) % 4));
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (FormatException) { }
        catch (JsonException) { }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }

    public static bool NeedsRefresh(CodexAuth auth, DateTimeOffset now)
    {
        var expiresAt = AccessTokenExpiresAt(auth.Tokens?.AccessToken);
        if (expiresAt is not null) return expiresAt <= now.AddMinutes(5);
        if (DateTimeOffset.TryParse(auth.LastRefresh, out var refreshed)) return now - refreshed.ToUniversalTime() > TimeSpan.FromDays(8);
        return false;
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
