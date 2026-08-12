using System.Net;
using System.Net.Sockets;
using System.Text;
using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

/// <summary>
/// Adversarial socket matrix for the 6738 desktop control server, mirroring the Rust 6737
/// control-channel tests: real TCP requests against the real accept loop, gate, and route
/// dispatch. Verifies that side-effectful routes (forced refresh, settings writes, popup state,
/// settings/customize windows) are denied for Origin-less browser-primitive requests unless the
/// native-client marker is present, that read-only routes stay marker-free, and that framing
/// abuse (oversized, duplicated Content-Length, chunked, stalled) never reaches a handler.
/// </summary>
public sealed class DesktopControlServerSecurityTests
{
    [Fact]
    public async Task SideEffectfulGetWithoutMarkerOrOriginIsRejected()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "GET /refresh HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Equal(0, harness.Recorders.ForceRefreshCalls);
    }

    [Fact]
    public async Task SideEffectfulGetWithMarkerIsServed()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "GET /refresh HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 204", response);
        Assert.Equal(1, harness.Recorders.ForceRefreshCalls);
    }

    [Fact]
    public async Task SideEffectfulPostWithMarkerIsServed()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "POST /refresh HTTP/1.1\r\nHost: localhost:6738\r\nX-TokenBurn-Client: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 204", response);
        Assert.Equal(1, harness.Recorders.ForceRefreshCalls);
    }

    [Fact]
    public async Task WrongMarkerValueIsNotANativeClient()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "GET /refresh HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 0\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Equal(0, harness.Recorders.ForceRefreshCalls);
    }

    [Fact]
    public async Task ForeignOriginIsRejectedOnTheWire()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "GET /providers HTTP/1.1\r\nHost: 127.0.0.1\r\nOrigin: https://evil.example\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Equal(0, harness.Recorders.ProvidersReads);
    }

    [Fact]
    public async Task AllowlistedOriginIsServedWithoutMarker()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "GET /refresh HTTP/1.1\r\nHost: 127.0.0.1\r\nOrigin: tauri://localhost\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 204", response);
        Assert.Equal(1, harness.Recorders.ForceRefreshCalls);
    }

    [Fact]
    public async Task RebindingHostIsRejectedEvenWithMarker()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "GET /providers HTTP/1.1\r\nHost: evil.example\r\nX-TokenBurn-Client: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Equal(0, harness.Recorders.ProvidersReads);
    }

    [Fact]
    public async Task SettingsWriteRequiresMarker()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "POST /settings-data HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 7\r\n\r\n{\"a\":1}");
        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Equal(0, harness.Recorders.ApplySettingsCalls);
    }

    [Fact]
    public async Task SettingsWriteWithMarkerAppliesTheBody()
    {
        using var harness = StartBridge();
        var body = "{\"a\":1}";
        var response = await SendRawAsync(harness.Port,
            $"POST /settings-data HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        Assert.StartsWith("HTTP/1.1 204", response);
        Assert.Equal(1, harness.Recorders.ApplySettingsCalls);
        Assert.Equal(body, harness.Recorders.LastAppliedBody);
    }

    [Fact]
    public async Task SpendMetricWriteRequiresMarker()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "POST /spend-metric HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 4\r\n\r\ncost");
        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Equal(0, harness.Recorders.SpendMetricCalls);
    }

    [Fact]
    public async Task ReadOnlyRoutesStayMarkerFree()
    {
        using var harness = StartBridge();
        var providers = await SendRawAsync(harness.Port,
            "GET /providers HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", providers);
        var settings = await SendRawAsync(harness.Port,
            "GET /settings-data HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", settings);
        var status = await SendRawAsync(harness.Port,
            "GET /refresh-status HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", status);
        var diagnostics = await SendRawAsync(harness.Port,
            "GET /diagnostics-bundle HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", diagnostics);
        // A GET to a write-only route is a 400 no-op, never a side effect.
        var spendMetricGet = await SendRawAsync(harness.Port,
            "GET /spend-metric HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", spendMetricGet);
        Assert.Equal(0, harness.Recorders.ApplySettingsCalls);
        Assert.Equal(0, harness.Recorders.SpendMetricCalls);
        Assert.Equal(0, harness.Recorders.ForceRefreshCalls);
    }

    [Fact]
    public async Task PopupVisibilityNotificationsRequireMarker()
    {
        using var harness = StartBridge();
        var denied = await SendRawAsync(harness.Port,
            "GET /popup-shown HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", denied);
        Assert.Equal(0, harness.Recorders.PopupVisibilityCalls);

        var shown = await SendRawAsync(harness.Port,
            "GET /popup-shown HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 204", shown);
        var hidden = await SendRawAsync(harness.Port,
            "GET /popup-hidden HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 204", hidden);
        Assert.Equal(2, harness.Recorders.PopupVisibilityCalls);
        Assert.False(harness.Recorders.LastPopupVisibility);
    }

    [Fact]
    public async Task SettingsAndCustomizeRoutesRequireMarker()
    {
        using var harness = StartBridge();
        var denied = await SendRawAsync(harness.Port,
            "GET /settings HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", denied);
        Assert.Equal(0, harness.Recorders.ShowSettingsCalls);

        var allowed = await SendRawAsync(harness.Port,
            "GET /settings HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 204", allowed);
        var customize = await SendRawAsync(harness.Port,
            "GET /customize HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 204", customize);
        Assert.Equal(1, harness.Recorders.ShowSettingsCalls);
        Assert.Equal(1, harness.Recorders.ShowCustomizeCalls);
    }

    [Fact]
    public async Task UnknownRoutesReturn404EvenWithMarker()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "GET /no-such-route HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", response);
    }

    [Fact]
    public async Task MalformedRequestLineIsRejected()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "PING\r\nHost: localhost\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 4", response);
        Assert.Equal(0, harness.Recorders.ForceRefreshCalls);
    }

    [Fact]
    public async Task OversizedBodyDropsTheConnectionWithoutSideEffects()
    {
        using var harness = StartBridge();
        var body = new string('x', 70 * 1024);
        var response = await SendRawAsync(harness.Port,
            $"POST /settings-data HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        Assert.Equal(string.Empty, response);
        Assert.Equal(0, harness.Recorders.ApplySettingsCalls);
    }

    [Fact]
    public async Task ConflictingContentLengthDropsTheConnection()
    {
        using var harness = StartBridge();
        // Request-smuggling-style framing ambiguity: two Content-Length headers that disagree.
        // The server cannot know which one frames the request, so it drops the connection.
        var response = await SendRawAsync(harness.Port,
            "POST /settings-data HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\nContent-Length: 0\r\nContent-Length: 99\r\n\r\n");
        Assert.Equal(string.Empty, response);
        Assert.Equal(0, harness.Recorders.ApplySettingsCalls);
    }

    [Fact]
    public async Task DuplicateContentLengthDropsTheConnection()
    {
        using var harness = StartBridge();
        // RFC 7230 §3.3.2 treats any repeated Content-Length as malformed, identical values
        // included, so the connection must be dropped rather than served by last-header-wins.
        var response = await SendRawAsync(harness.Port,
            "GET /providers HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n");
        Assert.Equal(string.Empty, response);
        Assert.Equal(0, harness.Recorders.ProvidersReads);
    }

    [Fact]
    public async Task DeclaredBodyThatNeverArrivesIsBounded()
    {
        using var harness = StartBridge();
        // A client that declares a body and then stalls must not hold the connection (and its
        // concurrency slot) forever: the read deadline drops it without answering or dispatching.
        var started = DateTime.UtcNow;
        var response = await SendRawAsync(harness.Port,
            "POST /settings-data HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\nContent-Length: 999\r\n\r\n");
        Assert.Equal(string.Empty, response);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10),
            "a stalled body must be dropped by the read deadline");
        Assert.Equal(0, harness.Recorders.ApplySettingsCalls);
    }

    [Fact]
    public async Task ChunkedFramingDropsTheConnection()
    {
        using var harness = StartBridge();
        var response = await SendRawAsync(harness.Port,
            "POST /settings-data HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
        Assert.Equal(string.Empty, response);
        Assert.Equal(0, harness.Recorders.ApplySettingsCalls);
    }

    [Fact]
    public async Task StalledClientDoesNotBlockLaterRequests()
    {
        using var harness = StartBridge();
        using (var stalled = new TcpClient())
        {
            await stalled.ConnectAsync(IPAddress.Loopback, harness.Port);
            var started = DateTime.UtcNow;
            var response = await SendRawAsync(harness.Port,
                "GET /refresh HTTP/1.1\r\nHost: 127.0.0.1\r\nX-TokenBurn-Client: 1\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 204", response);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1),
                "a stalled peer must not delay a legitimate request");
        }
    }

    private static async Task<string> SendRawAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(request);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
        client.Client.ReceiveTimeout = 5000;
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        // Socket receive timeouts do not bound StreamReader async reads, so an explicit deadline
        // keeps a stalling server from hanging the whole test run.
        using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await reader.ReadToEndAsync(readDeadline.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A connection dropped by the server is a valid outcome for framing abuse.
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static BridgeHarness StartBridge()
    {
        var recorders = new Recorders();
        var bridge = new TauriPopupBridge(
            showSettings: () => recorders.ShowSettingsCalls++,
            showCustomize: () => recorders.ShowCustomizeCalls++,
            enabledProviderIds: () => { recorders.ProvidersReads++; return recorders.Providers; },
            refreshStatus: () => new TauriPopupBridge.RefreshStatus(DateTimeOffset.UtcNow.AddMinutes(5), false),
            forceRefresh: () => { recorders.ForceRefreshCalls++; return Task.CompletedTask; },
            getSettingsPageData: () => "{\"settings\":{}}",
            applySettingsPageData: body => { recorders.ApplySettingsCalls++; recorders.LastAppliedBody = body; return true; },
            getDiagnosticsBundle: () => "{\"diagnostics\":[]}",
            setSpendMetric: metric => { recorders.SpendMetricCalls++; recorders.LastSpendMetric = metric; return true; },
            setPopupVisibility: visible => { recorders.PopupVisibilityCalls++; recorders.LastPopupVisibility = visible; },
            desktopControlPort: 0);
        Assert.True(bridge.TryStartDesktopControlServer(),
            "the control server must bind the ephemeral test port");
        return new BridgeHarness(bridge, bridge.BoundDesktopControlPort, recorders);
    }

    private sealed record BridgeHarness(TauriPopupBridge Bridge, int Port, Recorders Recorders) : IDisposable
    {
        public void Dispose() => Bridge.Dispose();
    }

    private sealed class Recorders
    {
        public int ShowSettingsCalls;
        public int ShowCustomizeCalls;
        public int ProvidersReads;
        public int ForceRefreshCalls;
        public int ApplySettingsCalls;
        public string? LastAppliedBody;
        public int SpendMetricCalls;
        public string? LastSpendMetric;
        public int PopupVisibilityCalls;
        public bool LastPopupVisibility;
        public IReadOnlyList<string> Providers { get; } = ["codex", "grok"];
    }
}
