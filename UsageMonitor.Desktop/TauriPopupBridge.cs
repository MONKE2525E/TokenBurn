using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UsageMonitor.Core;

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
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{ControlPort}"), Timeout = TimeSpan.FromMilliseconds(350) };
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
        Action<bool> setPopupVisibility)
    {
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

    private void EnsureDesktopControlServer()
    {
        lock (_gate)
        {
            if (_desktopControlListener is not null) return;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, DesktopControlPort);
                listener.Start();
                var cancellation = new CancellationTokenSource();
                _desktopControlListener = listener;
                _desktopControlCancellation = cancellation;
                _ = Task.Run(() => RunDesktopControlServerAsync(listener, cancellation.Token));
            }
            catch (SocketException)
            {
                // Another TokenBurn process may still be closing its listener during an
                // update. The Tauri surface remains usable, and the tray still exposes settings.
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

            _ = Task.Run(() => HandleDesktopControlClientAsync(client), cancellationToken);
        }
    }

    private async Task HandleDesktopControlClientAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var buffer = new byte[16384];
            var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
            var request = Encoding.UTF8.GetString(buffer, 0, count);
            var requestLine = request.Split('\n').FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var method = requestLine?.ElementAtOrDefault(0) ?? "GET";
            var path = requestLine?.ElementAtOrDefault(1);
            var headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var requestBody = headerEnd >= 0 ? request[(headerEnd + 4)..] : string.Empty;
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

    private static Task WriteControlResponseAsync(NetworkStream stream, string status)
    {
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        return stream.WriteAsync(response).AsTask();
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

    public bool TryShow(Point anchor) => TryShow(anchor, avoidRect: null, page: null);

    public bool TryShow(Point anchor, Rectangle? avoidRect, string? page = null)
    {
        if (_disposed) return false;
        var avoidQuery = BuildAvoidQuery(avoidRect);
        var path = page is null
            ? $"/show?x={anchor.X}&y={anchor.Y}{avoidQuery}"
            : $"/show?x={anchor.X}&y={anchor.Y}&page={page}{avoidQuery}";
        return TryShowPath(path);
    }

    /// <summary>
    /// Shows the popup, optionally landing directly on its in-page Settings/Customize view (see
    /// index.html's #settings-view/#customize-view). Lets tray-menu "Settings"/"Customize" reach
    /// the same page the Options button opens instead of a second native window that has to
    /// coordinate focus, ownership, and position with this popup.
    /// </summary>
    public bool TryShow(Point anchor, string? page)
        => TryShow(anchor, avoidRect: null, page);

    private bool TryShowPath(string path)
    {
        if (TrySend(path)) return true;
        if (!EnsureStarted()) return false;

        // WebView2 and the Rust control listener start independently. Poll briefly so a tray
        // click feels immediate without blocking the WPF fallback for more than two seconds.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (TrySend(path)) return true;
            Thread.Sleep(80);
        }
        return false;
    }

    public bool TryToggle(Point anchor)
        => TryToggle(anchor, avoidRect: null);

    public bool TryToggle(Point anchor, Rectangle? avoidRect)
    {
        if (_disposed) return false;
        var avoidQuery = BuildAvoidQuery(avoidRect);
        var path = $"/toggle?x={anchor.X}&y={anchor.Y}{avoidQuery}";
        if (TrySend(path)) return true;
        if (!EnsureStarted()) return false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (TrySend(path)) return true;
            Thread.Sleep(80);
        }
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
        TrySend("/hide");
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
                    new FileDiagnosticsLogger().Info("TokenBurn popup host started.");
                return _process is not null;
            }
            catch (Exception exception)
            {
                _process = null;
                _ownsProcess = false;
                new FileDiagnosticsLogger().Warning("TokenBurn popup host failed to start.", exception: exception);
                return false;
            }
        }
    }

    private bool TrySend(string path)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            return (int)response.StatusCode is >= 200 and < 300;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
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
