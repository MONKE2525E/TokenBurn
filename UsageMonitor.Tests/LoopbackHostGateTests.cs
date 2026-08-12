using System.Net;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

/// <summary>
/// Host-level loopback gate tests: these exercise the 6736 usage API host implementation
/// (Host validation, Origin allowlisting, the native-client marker on forced refreshes, bounded
/// request bodies, CORS reflection, and the ephemeral-port address contract). They live with the
/// runtime-refresh stack that owns that host integration; the pure gate unit tests live in
/// LoopbackSecurityTests with the security branch.
/// </summary>
public sealed class LoopbackHostGateTests
{
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
