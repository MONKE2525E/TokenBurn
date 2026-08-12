using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

/// <summary>
/// Regression tests for the loopback security boundary shared by the 6736 usage API, the 6737
/// popup control channel, and the 6738 desktop control server. The threat model is documented on
/// <see cref="LoopbackRequestGate"/>: webpages in the user's browser are the only actor that can
/// be repelled, via exact Origin allowlisting (fetch/WebSocket/form attacks) and Host validation
/// (DNS rebinding). Same-user native processes stay trusted.
/// </summary>
public sealed class LoopbackSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:6736", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST:6736", true)]
    [InlineData("[::1]", true)]
    [InlineData("[::1]:6736", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("evil.example", false)]
    [InlineData("127.0.0.1.evil.example", false)]
    [InlineData("10.0.0.8", false)]
    [InlineData("tauri.localhost", false)]
    [InlineData("127.0.0.1:6736:extra", false)]
    [InlineData("127.0.0.1:6736abc", false)]
    [InlineData("[::1]evil.example", false)]
    [InlineData("[::1]evil.example:6736", false)]
    [InlineData("[::1]:6736:extra", false)]
    public void HostGateAcceptsOnlyExactLoopbackHostNames(string? host, bool allowed)
    {
        Assert.Equal(allowed, LoopbackRequestGate.IsAllowedHost(host));
    }

    [Theory]
    [InlineData("tauri://localhost", true)]
    [InlineData("http://tauri.localhost", true)]
    [InlineData("https://tauri.localhost", true)]
    [InlineData("http://localhost:1420", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://127.0.0.1:6736", false)]
    [InlineData("tauri://localhost.evil.example", false)]
    [InlineData("http://tauri.localhost:1420", false)]
    public void OriginGateUsesExactAllowlistedOrigins(string? origin, bool allowed)
    {
        Assert.Equal(allowed, LoopbackRequestGate.IsAllowedOrigin(origin));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("2", false)]
    public void NativeMarkerGateAcceptsOnlyTheFixedValue(string? marker, bool allowed)
    {
        Assert.Equal(allowed, LoopbackRequestGate.HasNativeClientMarker(marker));
    }

    [Theory]
    // Side-effectful routes regardless of verb: the handlers run them for any method.
    [InlineData("GET", "/refresh", true)]
    [InlineData("POST", "/refresh", true)]
    [InlineData("GET", "/settings", true)]
    [InlineData("GET", "/customize", true)]
    [InlineData("GET", "/popup-shown", true)]
    [InlineData("GET", "/popup-hidden", true)]
    [InlineData("GET", "/refresh?x=1", true)]
    // POST-only side effects: writes persist state.
    [InlineData("POST", "/settings-data", true)]
    [InlineData("POST", "/spend-metric", true)]
    [InlineData("POST", "/settings-data?x=1", true)]
    // Read-only routes must stay marker-free for genuine native readers.
    [InlineData("GET", "/settings-data", false)]
    [InlineData("GET", "/spend-metric", false)]
    [InlineData("GET", "/providers", false)]
    [InlineData("GET", "/refresh-status", false)]
    [InlineData("GET", "/diagnostics-bundle", false)]
    [InlineData("POST", "/providers", false)]
    [InlineData("PUT", "/settings-data", false)]
    [InlineData("GET", null, false)]
    [InlineData("GET", "", false)]
    public void SideEffectClassificationCoversEveryRawControlRoute(string method, string? path, bool sideEffect)
    {
        Assert.Equal(sideEffect, LoopbackRequestGate.IsSideEffectRequest(method, path));
    }

    [Theory]
    // Native client with the marker: any route works.
    [InlineData("POST", "/refresh", "127.0.0.1", null, "1", true)]
    [InlineData("POST", "/settings-data", "localhost", null, "1", true)]
    [InlineData("POST", "/spend-metric", "127.0.0.1:6738", null, "1", true)]
    // Allowlisted browser origin: side effects allowed without a marker.
    [InlineData("GET", "/refresh", "127.0.0.1", "tauri://localhost", null, true)]
    [InlineData("POST", "/settings-data", "localhost", "http://tauri.localhost", null, true)]
    // Read-only routes: no marker required from no-Origin native readers.
    [InlineData("GET", "/providers", "127.0.0.1", null, null, true)]
    [InlineData("GET", "/refresh-status", "127.0.0.1", null, null, true)]
    [InlineData("GET", "/settings-data", "127.0.0.1", null, null, true)]
    [InlineData("GET", "/diagnostics-bundle", "127.0.0.1", null, null, true)]
    // <img>/<script> GET from a hostile webpage: no Origin, no marker -> side effects denied.
    [InlineData("GET", "/refresh", "127.0.0.1", null, null, false)]
    [InlineData("GET", "/settings", "127.0.0.1", null, null, false)]
    [InlineData("GET", "/customize", "127.0.0.1", null, null, false)]
    [InlineData("GET", "/popup-shown", "127.0.0.1", null, null, false)]
    [InlineData("POST", "/settings-data", "127.0.0.1", null, null, false)]
    [InlineData("POST", "/spend-metric", "127.0.0.1", null, null, false)]
    // Wrong marker value is not a native client.
    [InlineData("GET", "/refresh", "127.0.0.1", null, "0", false)]
    [InlineData("GET", "/refresh", "127.0.0.1", null, "true", false)]
    // Foreign origin rejected regardless of marker or route.
    [InlineData("GET", "/providers", "127.0.0.1", "https://evil.example", "1", false)]
    [InlineData("POST", "/refresh", "127.0.0.1", "https://evil.example", "1", false)]
    // DNS rebinding: foreign Host rejected even with an allowed origin and marker.
    [InlineData("GET", "/providers", "evil.example", "tauri://localhost", "1", false)]
    [InlineData("GET", "/refresh", "evil.example", null, "1", false)]
    // Missing Host (HTTP/1.0 native callers) is accepted, not treated as an attack.
    [InlineData("POST", "/refresh", null, null, "1", true)]
    public void DesktopControlGateDecidesEveryBoundaryCombination(
        string method, string path, string? host, string? origin, string? marker, bool allowed)
    {
        Assert.Equal(allowed, TauriPopupBridge.ControlGateAllows(method, path, host, origin, marker));
    }
}
