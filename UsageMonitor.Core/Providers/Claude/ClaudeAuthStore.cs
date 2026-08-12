using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageMonitor.Core.Providers.Claude;

public sealed record ClaudeOAuth
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; init; }
    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; init; }
    [JsonPropertyName("expiresAt")] public double? ExpiresAt { get; init; }
    // Claude Code also records a separate refresh-token lifetime. Keep it in the normalized
    // credential shape so Windows can fail fast with a useful re-login action instead of
    // repeatedly posting a known-dead refresh token on every five-minute poll.
    [JsonPropertyName("refreshTokenExpiresAt")] public double? RefreshTokenExpiresAt { get; init; }
    [JsonPropertyName("subscriptionType")] public string? SubscriptionType { get; init; }
    [JsonPropertyName("rateLimitTier")] public string? RateLimitTier { get; init; }
    [JsonPropertyName("scopes")] public IReadOnlyList<string>? Scopes { get; init; }
}

public sealed record ClaudeCredentialsFile
{
    [JsonPropertyName("claudeAiOauth")] public ClaudeOAuth? ClaudeAiOauth { get; init; }
}

public sealed record ClaudeAuthState(string Path, ClaudeOAuth OAuth, bool InferenceOnly = false)
{
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(OAuth.AccessToken);
    public bool HasProfileScope => OAuth.Scopes is null || OAuth.Scopes.Count == 0 || OAuth.Scopes.Any(x => string.Equals(x, "user:profile", StringComparison.OrdinalIgnoreCase));
}

public sealed class ClaudeAuthStore
{
    private readonly Func<string, string?> _environment;
    private readonly IProviderFileSystem _files;
    private readonly Func<string> _userProfile;

    public ClaudeAuthStore(IProviderFileSystem? files = null, Func<string, string?>? environment = null, Func<string>? userProfile = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public IReadOnlyList<string> CredentialPaths()
    {
        var configured = _environment("CLAUDE_CONFIG_DIR");
        var profile = _userProfile();
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Claude Code accepts a custom config home. On Windows it is common for this to be
            // supplied as a quoted, tilde-expanded, or semicolon-separated value by a shell profile.
            foreach (var root in configured!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = ExpandPath(root, profile);
                if (string.IsNullOrWhiteSpace(expanded)) continue;
                // A few Windows launchers expose the credential file itself as the config value.
                // Accept both forms so the desktop process sees the same login as the CLI.
                paths.Add(Path.GetFileName(expanded).Equals(".credentials.json", StringComparison.OrdinalIgnoreCase)
                    ? expanded
                    : Path.Combine(expanded, ".credentials.json"));
            }
        }
        if (!string.IsNullOrWhiteSpace(profile))
        {
            paths.Add(Path.Combine(profile, ".claude", ".credentials.json"));
            // Older Claude Code builds wrote the file directly under the config home.
            paths.Add(Path.Combine(profile, ".credentials.json"));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<ClaudeAuthState> LoadCandidates()
    {
        var candidates = CredentialPaths().Select(TryLoad).OfType<ClaudeAuthState>().ToList();
        var envToken = _environment("CLAUDE_CODE_OAUTH_TOKEN") ?? _environment("ANTHROPIC_AUTH_TOKEN");
        // Environment tokens are usually setup-token/inference-only credentials. Keep them as a
        // last-resort candidate so they cannot shadow the real Claude Code login that can read
        // subscription limits.
        if (!string.IsNullOrWhiteSpace(envToken))
            candidates.Add(new ClaudeAuthState("environment", new ClaudeOAuth { AccessToken = envToken.Trim() }, InferenceOnly: true));
        return candidates;
    }

    public ClaudeAuthState? TryLoad(string path)
    {
        if (!_files.FileExists(path)) return null;
        var text = _files.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var oauthElement = ProviderJson.Property(doc.RootElement, "claudeAiOauth", "claude_ai_oauth", "oauth");
            if (oauthElement is null) return null;
            var oauth = JsonSerializer.Deserialize<ClaudeOAuth>(oauthElement.Value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (oauth is null || string.IsNullOrWhiteSpace(oauth.AccessToken)) return null;
            oauth = oauth with { AccessToken = oauth.AccessToken.Trim(), RefreshToken = oauth.RefreshToken?.Trim() };
            return new ClaudeAuthState(path, oauth);
        }
        catch (JsonException) { return null; }
    }

    private static string ExpandPath(string raw, string profile)
    {
        var value = raw.Trim().Trim('"');
        if (value.Length == 0) return value;
        if (value == "~") return profile;
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            value = Path.Combine(profile, value[2..]);
        value = Environment.ExpandEnvironmentVariables(value);
        try { return Path.GetFullPath(value); }
        catch (ArgumentException) { return value; }
    }

}
