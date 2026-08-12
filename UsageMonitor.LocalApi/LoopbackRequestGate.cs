namespace UsageMonitor.LocalApi;

/// <summary>
/// Shared loopback-only request gate for every local surface (the 6736 usage API, the 6737 popup
/// control channel, and the 6738 desktop settings/refresh server).
///
/// Threat model: these services are reachable from (a) any webpage in the user's regular browser
/// and (b) any process running as the same user. Arbitrary same-user processes can already read
/// the user's settings, logs, and DPAPI-protected files, so repelling them with a token adds no
/// real security. Webpages are the only actor worth defending against, and two cheap, exact
/// checks defeat their attack classes:
///
/// * <see cref="IsAllowedOrigin"/> rejects browser-originated requests (fetch, WebSocket, form
///   posts all carry an Origin) unless the origin is one of the app's own embedded surfaces.
/// * <see cref="IsAllowedHost"/> rejects any Host header that is not a loopback host name, which
///   blocks DNS-rebinding reads of the loopback data (the attacker's domain resolves to
///   127.0.0.1, so the Host header is the only field the server can distinguish them by).
///
/// The origin check alone does not stop side-effectful GETs: a hostile webpage can issue them
/// with &lt;img&gt;/&lt;script&gt;/&lt;link&gt; tags, which send no Origin. Side-effectful endpoints
/// therefore require either an allowlisted Origin or <see cref="NativeClientMarkerHeader"/>, a
/// header browsers cannot attach without a preflight the origin gate rejects.
///
/// Native clients (the Rust host, the CLI, HttpClient consumers) send no Origin and a loopback
/// Host, so they are unaffected. All checks are exact matches only - no prefixes, no wildcards.
/// </summary>
public static class LoopbackRequestGate
{
    /// <summary>
    /// Origins that may interact with the loopback surfaces from a browser context: the embedded
    /// Tauri/WebView2 dashboard (the modern and legacy custom-protocol schemes) and the local Vite
    /// dev origin used for frontend work. Keep in sync with the Rust control server's allowlist.
    /// </summary>
    public static readonly string[] AllowedOrigins =
    [
        "tauri://localhost",
        "http://tauri.localhost",
        "https://tauri.localhost",
        "http://localhost:1420"
    ];

    private static readonly string[] AllowedHostNames =
    [
        "127.0.0.1",
        "localhost",
        "[::1]",
        "::1"
    ];

    /// <summary>Exact-match check against <see cref="AllowedOrigins"/>. Absent or empty values are
    /// accepted because they mean a native client, not a browser context.</summary>
    public static bool IsAllowedOrigin(string? origin) =>
        string.IsNullOrWhiteSpace(origin) ||
        AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Header that same-user native clients (the Tauri host, the desktop shell) send on
    /// side-effectful requests. A webpage cannot set it: browsers refuse custom headers without a
    /// CORS preflight, and the origin gate rejects preflights from foreign origins. The value is
    /// not a secret - arbitrary same-user processes are already trusted - it only separates
    /// browser-context requests (which cannot attach the header) from native clients.
    /// </summary>
    public const string NativeClientMarkerHeader = "X-TokenBurn-Client";
    public const string NativeClientMarkerValue = "1";

    /// <summary>True when a request carries the native-client marker at its fixed value.</summary>
    public static bool HasNativeClientMarker(string? markerHeader) =>
        string.Equals(markerHeader?.Trim(), NativeClientMarkerValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when handling <paramref name="method"/> against <paramref name="path"/> on a raw
    /// control surface performs a side effect that must be gated behind an allowlisted Origin or
    /// the native-client marker: opening the settings/customize windows, popup visibility state
    /// changes, forcing a provider refresh (which hits provider APIs with real credentials), and
    /// persisting settings or spend-metric changes. Read-only routes such as /providers,
    /// /refresh-status, /settings-data GET, and /diagnostics-bundle are deliberately not side
    /// effects. The 6737 popup control server keeps a mirrored classification; keep the sets in
    /// sync.
    /// </summary>
    public static bool IsSideEffectRequest(string? method, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var target = path.Split('?', 2)[0];
        if (target.Equals("/settings", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("/customize", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("/popup-shown", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("/popup-hidden", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("/refresh", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        return target.Equals("/settings-data", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("/spend-metric", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates a Host header. Missing values (HTTP/1.0 clients) are accepted so native callers
    /// keep working; present values must be a loopback host name with an optional port. Anything
    /// else (including a DNS-rebinding attacker's domain) is rejected.
    /// </summary>
    public static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var candidate = host.Trim();
        string? portSuffix = null;
        if (candidate.StartsWith("[", StringComparison.Ordinal))
        {
            var end = candidate.IndexOf(']');
            if (end < 0) return false;
            // A port may follow the bracket (e.g. "[::1]:6737"), but anything else appended to
            // the bracket host (e.g. "[::1]evil.example") is not a loopback host and must be
            // rejected rather than trimmed into one.
            var remainder = candidate[(end + 1)..];
            if (remainder.Length > 0 && !remainder.StartsWith(":", StringComparison.Ordinal)) return false;
            portSuffix = remainder;
            candidate = candidate[..(end + 1)];
        }
        else if (candidate.Count(character => character == ':') > 1)
        {
            // A non-bracketed multi-colon value is only acceptable if it is exactly a known
            // loopback host (bare ::1); it can never be split-and-prefix matched.
            return AllowedHostNames.Contains(candidate, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var colon = candidate.IndexOf(':');
            if (colon >= 0)
            {
                portSuffix = candidate[colon..];
                candidate = candidate[..colon];
            }
        }
        // A port suffix, when present, must be a colon followed only by digits: anything else is
        // a malformed Host header, not a loopback host with a port.
        if (portSuffix is not null &&
            (portSuffix.Length < 2 || !portSuffix[1..].All(char.IsAsciiDigit))) return false;
        return AllowedHostNames.Contains(candidate, StringComparer.OrdinalIgnoreCase);
    }
}
