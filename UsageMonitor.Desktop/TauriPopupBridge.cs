using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UsageMonitor.Core;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Desktop;

/// <summary>
/// Keeps the WPF shell responsible for Explorer integration while routing the actual popover to
/// the Tauri presentation process. The control channel is loopback-only and carries coordinates,
/// never provider data or credentials.
/// </summary>
public sealed class TauriPopupBridge : IDisposable
{
    private const int ControlPort = 6737;
    private const int DesktopControlPort = 6738;
    private readonly int _desktopControlPort;
    /// <summary>Hard cap for any desktop control request (headers plus body).</summary>
    private const int MaxRequestBytes = 64 * 1024;
    /// <summary>Read/write bound so a stalled peer cannot pin a handler task indefinitely.</summary>
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Bounded like the Rust control server: a client flood must not grow handler tasks
    /// without limit. Each slot is held at most PeerTimeout by a stalled client.</summary>
    private static readonly SemaphoreSlim ControlClientSlots = new(16, 16);
    private readonly HttpClient _http = CreateControlClient();

    private static HttpClient CreateControlClient()
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ControlPort}"), Timeout = TimeSpan.FromMilliseconds(350) };
        // Native-client marker: the 6737/6738 control servers only run side-effectful commands
        // for an allowlisted browser origin or a request carrying this header. Browsers cannot
        // attach it without a CORS preflight, so hostile webpages are rejected either way.
        client.DefaultRequestHeaders.Add(LoopbackRequestGate.NativeClientMarkerHeader, LoopbackRequestGate.NativeClientMarkerValue);
        return client;
    }
    private readonly object _gate = new();
    private readonly Action _showSettings;
    private readonly Action _showCustomize;
    private readonly Func<IReadOnlyList<string>> _enabledProviderIds;
    private readonly Func<RefreshStatus> _refreshStatus;
    private readonly Func<Task> _forceRefresh;
    private readonly Func<string> _getSettingsPageData;
    private readonly Func<string, bool> _applySettingsPageData;
    private readonly Func<string> _getDiagnosticsBundle;
    private readonly Func<string, bool> _setSpendMetric;
    private readonly Action<bool> _setPopupVisibility;
    private Process? _process;
    private bool _ownsProcess;
    private TcpListener? _desktopControlListener;
    private CancellationTokenSource? _desktopControlCancellation;
    private bool _disposed;

    public TauriPopupBridge(
        Action showSettings,
        Action showCustomize,
        Func<IReadOnlyList<string>> enabledProviderIds,
        Func<RefreshStatus> refreshStatus,
        Func<Task> forceRefresh,
        Func<string> getSettingsPageData,
        Func<string, bool> applySettingsPageData,
        Func<string> getDiagnosticsBundle,
        Func<string, bool> setSpendMetric,
        Action<bool> setPopupVisibility,
        int? desktopControlPort = null)
    {
        _desktopControlPort = desktopControlPort ?? DesktopControlPort;
        _showSettings = showSettings ?? throw new ArgumentNullException(nameof(showSettings));
        _showCustomize = showCustomize ?? throw new ArgumentNullException(nameof(showCustomize));
        _enabledProviderIds = enabledProviderIds ?? throw new ArgumentNullException(nameof(enabledProviderIds));
        _refreshStatus = refreshStatus ?? throw new ArgumentNullException(nameof(refreshStatus));
        _forceRefresh = forceRefresh ?? throw new ArgumentNullException(nameof(forceRefresh));
        _getSettingsPageData = getSettingsPageData ?? throw new ArgumentNullException(nameof(getSettingsPageData));
        _applySettingsPageData = applySettingsPageData ?? throw new ArgumentNullException(nameof(applySettingsPageData));
        _getDiagnosticsBundle = getDiagnosticsBundle ?? throw new ArgumentNullException(nameof(getDiagnosticsBundle));
        _setSpendMetric = setSpendMetric ?? throw new ArgumentNullException(nameof(setSpendMetric));
        _setPopupVisibility = setPopupVisibility ?? throw new ArgumentNullException(nameof(setPopupVisibility));
    }

    public readonly record struct RefreshStatus(DateTimeOffset NextRefreshAt, bool Loading);

    public void StartHosted()
    {
        if (_disposed) return;
        EnsureDesktopControlServer();
        EnsureStarted();
    }

    internal void StopHostedProcess()
    {
        lock (_gate)
        {
            if (!_ownsProcess || _process is null) return;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch { }
            _process.Dispose();
            _process = null;
            _ownsProcess = false;
        }
    }

    /// <summary>Starts only the loopback control server, never the popup host process. Used by
    /// tests (with an ephemeral port) and by <see cref="StartHosted"/> before process startup.</summary>
    internal bool TryStartDesktopControlServer() => EnsureDesktopControlServer();

    /// <summary>The port the control server is actually bound to (the configured one, or the
    /// ephemeral port the OS assigned when the configured port was 0).</summary>
    internal int BoundDesktopControlPort
    {
        get
        {
            lock (_gate)
            {
                if (_desktopControlListener is { } listener &&
                    listener.LocalEndpoint is IPEndPoint endpoint)
                    return endpoint.Port;
                return _desktopControlPort;
            }
        }
    }

    private bool EnsureDesktopControlServer()
    {
        lock (_gate)
        {
            if (_desktopControlListener is not null) return true;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, _desktopControlPort);
                listener.Start();
                var cancellation = new CancellationTokenSource();
                _desktopControlListener = listener;
                _desktopControlCancellation = cancellation;
                _ = Task.Run(() => RunDesktopControlServerAsync(listener, cancellation.Token));
                return true;
            }
            catch (SocketException)
            {
                // Another TokenBurn process may still be closing its listener during an
                // update. The Tauri surface remains usable, and the tray still exposes settings.
                return false;
            }
        }
    }

    private async Task RunDesktopControlServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { continue; }

            // CancellationToken.None on purpose: a token canceled between Accept and Task.Run
            // would skip the delegate entirely and leak the accepted socket. The delegate checks
            // the token itself and disposes the client on every exit path.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    // A hostile or misbehaving local client flood is dropped at the admission
                    // gate, never queued: every slot is bounded by PeerTimeout anyway.
                    if (!await ControlClientSlots.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
                        return;
                    try
                    {
                        await HandleDesktopControlClientAsync(client).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation while handling a client is an orderly close, not a fault.
                    }
                    finally
                    {
                        ControlClientSlots.Release();
                    }
                }
                finally
                {
                    client.Dispose();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleDesktopControlClientAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            stream.ReadTimeout = (int)PeerTimeout.TotalMilliseconds;
            stream.WriteTimeout = (int)PeerTimeout.TotalMilliseconds;
            var requestBytes = await ReadRequestAsync(stream).ConfigureAwait(false);
            if (requestBytes is null) return;
            var request = Encoding.UTF8.GetString(requestBytes);
            var lines = request.Split('\n');
            var requestLine = lines.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var method = requestLine?.ElementAtOrDefault(0) ?? "GET";
            var path = requestLine?.ElementAtOrDefault(1);
            var headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var requestBody = headerEnd >= 0 ? request[(headerEnd + 4)..] : string.Empty;

            if (!ControlGateAllows(method, path,
                ParseHeader(request, "Host"),
                ParseHeader(request, "Origin"),
                ParseHeader(request, LoopbackRequestGate.NativeClientMarkerHeader)))
            {
                await WriteControlResponseAsync(stream, "403 Forbidden").ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/settings", StringComparison.OrdinalIgnoreCase))
            {
                await WriteControlResponseAsync(stream, "204 No Content").ConfigureAwait(false);
                _showSettings();
                return;
            }
            if (string.Equals(path, "/customize", StringComparison.OrdinalIgnoreCase))
            {
                await WriteControlResponseAsync(stream, "204 No Content").ConfigureAwait(false);
                _showCustomize();
                return;
            }
            if (string.Equals(path, "/popup-hidden", StringComparison.OrdinalIgnoreCase))
            {
                await WriteControlResponseAsync(stream, "204 No Content").ConfigureAwait(false);
                _setPopupVisibility(false);
                return;
            }
            if (string.Equals(path, "/popup-shown", StringComparison.OrdinalIgnoreCase))
            {
                await WriteControlResponseAsync(stream, "204 No Content").ConfigureAwait(false);
                _setPopupVisibility(true);
                return;
            }
            if (string.Equals(path, "/settings-data", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    var applied = _applySettingsPageData(requestBody);
                    await WriteControlResponseAsync(stream, applied ? "204 No Content" : "400 Bad Request").ConfigureAwait(false);
                    return;
                }
                var json = _getSettingsPageData();
                await WriteJsonResponseAsync(stream, "200 OK", Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
                return;
            }
            if (string.Equals(path, "/spend-metric", StringComparison.OrdinalIgnoreCase))
            {
                var applied = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                    _setSpendMetric(requestBody.Trim().Trim('"'));
                await WriteControlResponseAsync(stream, applied ? "204 No Content" : "400 Bad Request").ConfigureAwait(false);
                return;
            }
            if (string.Equals(path, "/providers", StringComparison.OrdinalIgnoreCase))
            {
                var ids = _enabledProviderIds();
                var body = JsonSerializer.SerializeToUtf8Bytes(ids);
                await WriteJsonResponseAsync(stream, "200 OK", body).ConfigureAwait(false);
                return;
            }
            if (string.Equals(path, "/diagnostics-bundle", StringComparison.OrdinalIgnoreCase))
            {
                var json = _getDiagnosticsBundle();
                await WriteJsonResponseAsync(stream, "200 OK", Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
                return;
            }
            if (string.Equals(path, "/refresh-status", StringComparison.OrdinalIgnoreCase))
            {
                var status = _refreshStatus();
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    nextRefreshAt = status.NextRefreshAt,
                    loading = status.Loading
                });
                await WriteJsonResponseAsync(stream, "200 OK", body).ConfigureAwait(false);
                return;
            }
            if (string.Equals(path, "/refresh", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _forceRefresh().ConfigureAwait(false);
                    await WriteControlResponseAsync(stream, "204 No Content").ConfigureAwait(false);
                }
                catch
                {
                    await WriteControlResponseAsync(stream, "503 Service Unavailable").ConfigureAwait(false);
                }
                return;
            }
            await WriteControlResponseAsync(stream, "404 Not Found").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The complete request gate for this loopback control server: exact Host validation (DNS
    /// rebinding), exact Origin allowlisting (foreign webpages), and the native-client marker for
    /// side-effectful requests that arrive without an Origin (<see cref="LoopbackRequestGate"/>
    /// for the threat model). A hostile webpage can issue Origin-less GETs with
    /// &lt;img&gt;/&lt;script&gt;/&lt;link&gt; tags, so side-effectful paths must also carry the
    /// marker, which browsers cannot attach without a CORS preflight the origin gate rejects.
    /// The marker is not a secret and not an authentication boundary between same-user processes.
    /// </summary>
    internal static bool ControlGateAllows(string? method, string? path, string? host,
        string? origin, string? marker)
    {
        if (!LoopbackRequestGate.IsAllowedHost(host)) return false;
        if (origin is not null && !LoopbackRequestGate.IsAllowedOrigin(origin)) return false;
        if (origin is null && LoopbackRequestGate.IsSideEffectRequest(method, path) &&
            !LoopbackRequestGate.HasNativeClientMarker(marker))
            return false;
        return true;
    }

    private static Task WriteControlResponseAsync(NetworkStream stream, string status)
    {
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        return stream.WriteAsync(response).AsTask();
    }

    /// <summary>
    /// Reads one complete HTTP request (headers plus the body declared by Content-Length) or fails
    /// the connection: a partial header/body read is dropped, a request with an invalid or
    /// oversized body declaration is dropped, an undeclared body or chunked framing is dropped,
    /// and a peer that sends nothing within the timeout is dropped. Callers only ever see a
    /// complete, well-framed request, so the route handlers can stop guessing about framing.
    /// </summary>
    private static async Task<byte[]?> ReadRequestAsync(NetworkStream stream)
    {
        // Socket read timeouts do not bound NetworkStream's async reads, so a peer that declares
        // a body and never sends it (or trickles headers forever) would otherwise hold its
        // connection and concurrency slot indefinitely. An explicit read deadline bounds both the
        // header and the body loops; the caller sees null and drops the connection.
        using var readDeadline = new CancellationTokenSource(PeerTimeout);
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        var headerEnd = -1;
        try
        {
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total), readDeadline.Token).ConfigureAwait(false);
                if (read <= 0) return null;
                total += read;
                if (total >= buffer.Length) return null;
                headerEnd = IndexOfHeaderEnd(buffer, total);
            }

            var contentLength = ParseContentLength(buffer, headerEnd);
            // Garbage/duplicate/negative Content-Length, chunked framing, or a body with no declared
            // length means we cannot frame the request; drop the connection rather than guess.
            if (contentLength < 0 || HasTransferEncoding(buffer, headerEnd)) return null;
            var expected = headerEnd + 4 + contentLength;
            if (expected > MaxRequestBytes) return null;
            if (total > expected) return null;
            while (total < expected)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total), readDeadline.Token).ConfigureAwait(false);
                if (read <= 0) return null;
                total += read;
            }
            return buffer.AsSpan(0, total).ToArray();
        }
        catch (OperationCanceledException)
        {
            // The peer stalled past the read deadline; drop the connection without answering.
            return null;
        }
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                return i;
        }
        return -1;
    }

    private static bool HasTransferEncoding(byte[] buffer, int headerEnd)
    {
        var header = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        return header.Split('\n').Any(line =>
            line.TrimStart().StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase));
    }

    private static int ParseContentLength(byte[] buffer, int headerEnd)
    {
        // Only a single Content-Length is honored. Duplicates (RFC 7230 §3.3.2 treats multiple
        // Content-Length fields as malformed, identical values included) and garbage yield -1,
        // which drops the connection via the caller's size/framing checks.
        var header = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var matches = header.Split('\n')
            .Where(line => line.TrimStart().StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        // No Content-Length at all means an empty body. More than one is malformed regardless of
        // whether the values agree, so the connection must be dropped rather than served.
        if (matches.Length > 1) return -1;
        if (matches.Length == 0) return 0;
        var value = matches[0].Trim().Substring("Content-Length:".Length).Trim();
        return int.TryParse(value, out var length) && length >= 0 ? length : -1;
    }

    private static string? ParseHeader(string request, string name)
    {
        foreach (var line in request.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            if (string.Equals(trimmed[..colon].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return trimmed[(colon + 1)..].Trim();
        }
        return null;
    }

    private static Task WriteJsonResponseAsync(NetworkStream stream, string status, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return WriteJsonResponseCoreAsync(stream, header, body);
    }

    private static async Task WriteJsonResponseCoreAsync(NetworkStream stream, byte[] header, byte[] body)
    {
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public Task<bool> TryShowAsync(Point anchor) => TryShowAsync(anchor, avoidRect: null, page: null);

    public async Task<bool> TryShowAsync(Point anchor, Rectangle? avoidRect, string? page = null)
    {
        if (_disposed) return false;
        var avoidQuery = BuildAvoidQuery(avoidRect);
        var path = page is null
            ? $"/show?x={anchor.X}&y={anchor.Y}{avoidQuery}"
            : $"/show?x={anchor.X}&y={anchor.Y}&page={page}{avoidQuery}";
        return await TryShowPathAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows the popup, optionally landing directly on its in-page Settings/Customize view (see
    /// index.html's #settings-view/#customize-view). Lets tray-menu "Settings"/"Customize" reach
    /// the same page the Options button opens instead of a second native window that has to
    /// coordinate focus, ownership, and position with this popup.
    /// </summary>
    public Task<bool> TryShowAsync(Point anchor, string? page)
        => TryShowAsync(anchor, avoidRect: null, page);

    private async Task<bool> TryShowPathAsync(string path)
    {
        if (await TrySendAsync(path).ConfigureAwait(false)) return true;
        if (!EnsureStarted()) return false;

        // WebView2 and the Rust control listener start independently. Poll briefly so a tray
        // click feels immediate. The wait happens off the UI thread: the overlay click that
        // reaches this path runs inside the WndProc, and a synchronous poll there stalls the
        // whole dispatcher (refresh heartbeat, drag handling, reset notifications).
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(80).ConfigureAwait(false);
            if (await TrySendAsync(path).ConfigureAwait(false)) return true;
        }
        // A tray click that visibly does nothing is a support case, not a silent no-op. The path
        // carries only anchor coordinates and page names, never provider data.
        FileDiagnosticsLogger.Default.Debug("Compact popup could not be reached",
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["attempts"] = 20,
                ["processRunning"] = _process is { HasExited: false }
            });
        return false;
    }

    public Task<bool> TryToggleAsync(Point anchor)
        => TryToggleAsync(anchor, avoidRect: null);

    public async Task<bool> TryToggleAsync(Point anchor, Rectangle? avoidRect)
    {
        if (_disposed) return false;
        var avoidQuery = BuildAvoidQuery(avoidRect);
        var path = $"/toggle?x={anchor.X}&y={anchor.Y}{avoidQuery}";
        if (await TrySendAsync(path).ConfigureAwait(false)) return true;
        if (!EnsureStarted()) return false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(80).ConfigureAwait(false);
            if (await TrySendAsync(path).ConfigureAwait(false)) return true;
        }
        FileDiagnosticsLogger.Default.Debug("Compact popup could not be toggled",
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["attempts"] = 20,
                ["processRunning"] = _process is { HasExited: false }
            });
        return false;
    }

    private static string BuildAvoidQuery(Rectangle? avoidRect)
    {
        if (avoidRect is not { } rect || rect.IsEmpty) return string.Empty;
        return $"&avoidX={rect.Left}&avoidY={rect.Top}&avoidWidth={rect.Width}&avoidHeight={rect.Height}";
    }

    public void TryHide()
    {
        if (_disposed) return;
        _ = TrySendAsync("/hide");
    }

    private bool EnsureStarted()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return true;
            EnsureDesktopControlServer();
            var executable = ResolveExecutable();
            if (executable is null) return false;
            try
            {
                _process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--hosted",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
                });
                _ownsProcess = _process is not null;
                if (_ownsProcess)
                    FileDiagnosticsLogger.Default.Info("TokenBurn popup host started.");
                return _process is not null;
            }
            catch (Exception exception)
            {
                _process = null;
                _ownsProcess = false;
                FileDiagnosticsLogger.Default.Warning("TokenBurn popup host failed to start.", exception: exception);
                return false;
            }
        }
    }

    private async Task<bool> TrySendAsync(string path)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            return (int)response.StatusCode is >= 200 and < 300;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string? ResolveExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("USAGE_MONITOR_TAURI_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        // The popup embeds its frontend assets at compile time. A stale bundled copy beside a
        // freshly rebuilt cargo binary (or vice versa) would silently keep serving old UI/colors,
        // so choose the newest available build instead of blindly preferring the sibling copy.
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "UsageMonitor.TauriPoc.exe"),
            Path.Combine(AppContext.BaseDirectory, "tokenburn-desktop.exe")
        };
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "UsageMonitor.TauriPoc", "src-tauri", "target", "debug", "tokenburn-desktop.exe"));
            candidates.Add(Path.Combine(current.FullName, "UsageMonitor.TauriPoc", "src-tauri", "target", "release", "tokenburn-desktop.exe"));
        }
        return candidates.Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { TryHide(); } catch { }
        _disposed = true;
        lock (_gate)
        {
            _desktopControlCancellation?.Cancel();
            _desktopControlCancellation = null;
            try { _desktopControlListener?.Stop(); } catch { }
            _desktopControlListener = null;
        }
        StopHostedProcess();
        _http.Dispose();
    }
}
