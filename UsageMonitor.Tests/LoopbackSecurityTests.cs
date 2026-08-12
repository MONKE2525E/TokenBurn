using System.Net;
using System.Text.Json;
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

    [Fact]
    public async Task HostLevelRequestsFromForeignHostsAreRejectedWithJson()
    {
        await using var host = StartHost(false);
        using var client = new HttpClient();

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, host.BaseAddress + "/v1/limits");
        rebinding.Headers.Host = "evil.example";
        using var response = await client.SendAsync(rebinding);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"error\":\"forbidden_host\"}", body);
    }

    [Fact]
    public async Task AllowedWebViewOriginsMayReadCrossOrigin()
    {
        await using var host = StartHost(true);
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, host.BaseAddress + "/v1/limits");
        request.Headers.Add("Origin", "http://tauri.localhost");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://tauri.localhost", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(response.Headers.GetValues("Vary"), value => value.Contains("Origin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ForeignWebOriginsAreRejectedBeforeProcessing()
    {
        await using var host = StartHost(true);
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, host.BaseAddress + "/v1/limits?force=true");
        request.Headers.Add("Origin", "https://evil.example");
        using var response = await client.SendAsync(request);

        // A foreign webpage must neither read the loopback data nor trigger side effects such as
        // a forced provider refresh, so the request is refused outright with the JSON envelope.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"forbidden_origin\"}", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"),
            "a foreign origin must never receive CORS headers for the loopback data");
    }

    [Fact]
    public async Task PreflightFromAllowedOriginSucceedsAndForeignPreflightIsDenied()
    {
        await using var host = StartHost(true);
        using var client = new HttpClient();

        using var allowed = new HttpRequestMessage(HttpMethod.Options, host.BaseAddress + "/v1/limits");
        allowed.Headers.Add("Origin", "http://tauri.localhost");
        allowed.Headers.Add("Access-Control-Request-Method", "GET");
        using var allowedResponse = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
        Assert.Equal("http://tauri.localhost", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(allowedResponse.Headers.GetValues("Access-Control-Allow-Methods"),
            value => value.Contains("GET", StringComparison.OrdinalIgnoreCase));

        using var foreign = new HttpRequestMessage(HttpMethod.Options, host.BaseAddress + "/v1/limits");
        foreign.Headers.Add("Origin", "https://evil.example");
        using var foreignResponse = await client.SendAsync(foreign);
        Assert.False(foreignResponse.Headers.Contains("Access-Control-Allow-Origin"),
            "a foreign preflight must not be granted");
    }

    [Fact]
    public async Task OversizedRequestBodyIsRejectedBeforeProcessing()
    {
        await using var host = StartHost(false);
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, host.BaseAddress + "/v1/limits")
        {
            Content = new StringContent(new string('x', 10_000))
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("{\"error\":\"payload_too_large\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EphemeralPortReportsTheRealBoundAddress()
    {
        var options = new UsageApiOptions { Port = 0, Host = "127.0.0.1" };
        await using var host = UsageApiHost.Create(new EmptyUsageSnapshotSource(), options);
        // Before binding, the ephemeral placeholder is advertised.
        Assert.Contains(":0", host.BaseAddress);

        await host.StartAsync();
        Assert.True(host.IsStarted);
        // After binding, the reported address must be the actually bound one, i.e. requests to it
        // succeed and it carries a real port number.
        Assert.DoesNotContain(":0", host.BaseAddress);
        using var client = new HttpClient();
        using var response = await client.GetAsync(host.BaseAddress + "/v1/limits");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PortCollisionDoesNotThrowAndKeepsTheHostUnstarted()
    {
        await using var first = StartHost(false, port: 0);
        Assert.True(first.IsStarted);

        var options = new UsageApiOptions { Port = ExtractPort(first.BaseAddress), Host = "127.0.0.1" };
        await using var second = UsageApiHost.Create(new EmptyUsageSnapshotSource(), options);
        await second.StartAsync();

        Assert.False(second.IsStarted);
        Assert.Contains($":{options.Port}", second.BaseAddress);
    }

    [Fact]
    public async Task ForceRefreshWithoutOriginOrMarkerIsRejectedBeforeProcessing()
    {
        await using var host = StartHost(true);
        using var client = new HttpClient();

        // A hostile webpage's <img>/<script> GET carries no Origin, so the origin gate alone
        // cannot see it. Forced refreshes hit provider APIs with real credentials, so they must
        // additionally require the native-client marker header that browsers cannot attach.
        using var response = await client.GetAsync(host.BaseAddress + "/v1/limits?force=true");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"forbidden_client\"}", await response.Content.ReadAsStringAsync());

        // Same-actor attack on the /usage route.
        using var usageResponse = await client.GetAsync(host.BaseAddress + "/v1/usage?force=true");
        Assert.Equal(HttpStatusCode.Forbidden, usageResponse.StatusCode);
    }

    [Fact]
    public async Task ForceRefreshFromAllowedWebviewOriginIsStillServed()
    {
        await using var host = StartHost(true);
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, host.BaseAddress + "/v1/limits?force=true");
        request.Headers.Add("Origin", "http://tauri.localhost");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ForceRefreshFromNativeClientWithMarkerIsStillServed()
    {
        await using var host = StartHost(false);
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, host.BaseAddress + "/v1/usage?force=true");
        request.Headers.Add(LoopbackRequestGate.NativeClientMarkerHeader, LoopbackRequestGate.NativeClientMarkerValue);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    [Fact]
    public async Task NonFiniteMetricsSerializeAsNullNotAsJsonStringsOrZeros()
    {
        var snapshot = new UsageSnapshotData("codex", "Codex", null,
            [new ValuesMetricData("Balance", [new ScalarValueData(double.NaN, "usd")])],
            DateTimeOffset.UtcNow)
        {
            UsageHistory = new UsageHistoryData([
                new UsageHistoryPointData(DateOnly.FromDateTime(DateTime.UtcNow), double.PositiveInfinity, double.NaN)])
        };
        var response = await new UsageApiService(new FakeSource(snapshot)).HandleAsync("GET", "/v1/usage/codex");

        Assert.Equal(200, response.StatusCode);
        Assert.DoesNotContain("NaN", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", response.Body, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(response.Body);
        var provider = doc.RootElement[0];
        var value = provider.GetProperty("lines")[0].GetProperty("value").GetString();
        Assert.Equal(string.Empty, value);
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("usageHistory").GetProperty("totalCostUsd").ValueKind);
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("usageHistory").GetProperty("points")[0].GetProperty("tokens").ValueKind);
    }

    [Fact]
    public async Task MalformedPercentEscapesNeverBecomeServerErrors()
    {
        var service = new UsageApiService(new FakeSource());
        // Modern Uri.UnescapeDataString is lenient and returns malformed escapes verbatim, so the
        // contract is: every hostile path yields the JSON error envelope, never an exception or a
        // 500. The host layer additionally catches any decode-time exception as a 400.
        var hostilePaths = new[] { "/v1/limits/%zz", "/v1/limits/%", "/v1/limits/%2", "/v1/limits/a%2Fb", "/v1/limits/%25", "/v1/limits/\u0000" };
        foreach (var path in hostilePaths)
        {
            var response = await service.HandleAsync("GET", path);
            Assert.InRange(response.StatusCode, 400, 599);
            using var doc = JsonDocument.Parse(response.Body);
            Assert.True(doc.RootElement.TryGetProperty("error", out _), $"path {path} must use the JSON error envelope");
        }
    }

    [Fact]
    public async Task UnknownVerbsAndRoutesKeepTheJsonErrorEnvelope()
    {
        var service = new UsageApiService(new FakeSource());
        Assert.Equal("{\"error\":\"method_not_allowed\"}", (await service.HandleAsync("DELETE", "/v1/limits")).Body);
        Assert.Equal("{\"error\":\"not_found\"}", (await service.HandleAsync("GET", "/v1/settings")).Body);
        Assert.Equal("{\"error\":\"provider_not_found\"}", (await service.HandleAsync("GET", "/v1/limits/nope")).Body);
    }

    private static int ExtractPort(string baseAddress) =>
        new Uri(baseAddress).Port;

    private static UsageApiHost StartHost(bool enableCors, int port = 0)
    {
        var options = new UsageApiOptions { Port = port, EnableCors = enableCors };
        var host = UsageApiHost.Create(new FakeSource(), options);
        host.StartAsync().GetAwaiter().GetResult();
        return host;
    }

    private sealed class FakeSource(params UsageSnapshotData[] snapshots) : IUsageSnapshotSource
    {
        private readonly IReadOnlyList<UsageSnapshotData> _snapshots = snapshots;
        public IReadOnlySet<string> KnownProviderIds { get; } = new HashSet<string>(["codex"], StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyList<UsageSnapshotData>> GetSnapshotsAsync(string? providerId, bool force,
            CancellationToken cancellationToken = default, string? refreshId = null) =>
            Task.FromResult(providerId is null
                ? _snapshots
                : _snapshots.Where(x => string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)).ToArray());
    }
}
