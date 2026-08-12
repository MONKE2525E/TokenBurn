using System.Windows;
using System.Windows.Threading;
using UsageMonitor.Core;
using Forms = System.Windows.Forms;

namespace UsageMonitor.Desktop;

public sealed class TrayIconService : IDisposable
{
    private readonly App _app;
    private readonly MainWindow _dashboard;
    private readonly WindowsAppNotificationService _appNotifications;
    private readonly MonitorPlacementService _placement = new();
    private Forms.NotifyIcon? _notifyIcon;
    private TaskbarOverlayController? _taskbar;
    private IReadOnlyList<MetricDisplay> _metrics = Array.Empty<MetricDisplay>();
    private DispatcherTimer? _countdownTimer;
    private string? _lastTaskbarNotice;
    private bool _widgetAttached;
    private bool _disposed;
    private TrayMenuWindow? _menu;
    private Forms.ContextMenuStrip? _fallbackMenu;
    private long _menuGeneration;

    internal TrayIconService(App app, MainWindow dashboard, WindowsAppNotificationService appNotifications)
    {
        _app = app;
        _dashboard = dashboard;
        _appNotifications = appNotifications;
    }

    public void Initialize()
    {
        if (_notifyIcon is not null) return;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "TokenBurn | no provider data yet",
            Icon = TokenBurnIconResources.LoadTrayIcon(),
            Visible = true
        };
        // MouseDown fires before Windows moves focus away from the popup. That lets the
        // Tauri bridge perform a true tray toggle instead of focus-loss hiding followed by
        // MouseClick reopening the popup immediately.
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleDashboard();
            else if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu();
        };
        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _countdownTimer.Tick += (_, _) => RefreshTooltip();
        _countdownTimer.Start();
    }

    public void AttachTaskbar(TaskbarOverlayController taskbar)
    {
        _taskbar = taskbar;
        _taskbar.StateChanged += OnTaskbarStateChanged;
    }

    internal void RefreshMonitorMenu() { }

    public void UpdateMetrics(IEnumerable<MetricDisplay> metrics)
    {
        _metrics = (metrics ?? Array.Empty<MetricDisplay>()).ToArray();
        RefreshTooltip();
        _taskbar?.UpdateMetrics(_metrics);
    }

    private void RefreshTooltip()
    {
        if (_notifyIcon is null) return;
        _notifyIcon.Text = BuildTooltip(_metrics, _app.Settings.ResetTimeDisplay);
    }

    /// <summary>Pure tray-tooltip text builder: provider values joined with their reset windows,
    /// truncated to the 63-char tray tooltip limit. Kept internal so the format is testable
    /// without a notification icon.</summary>
    internal static string BuildTooltip(IReadOnlyList<MetricDisplay> values, string resetDisplay)
    {
        var tooltip = values.Count == 0
            ? "TokenBurn | no provider data yet"
            : "TokenBurn | " + string.Join(" | ", values.Select(v =>
            {
                var reset = v.ResetAt is { } resetAt
                    ? $" ({ResetTimeFormatter.FormatSurface(resetAt, resetDisplay)})"
                    : string.Empty;
                return $"{v.Provider} {v.Value}{reset}";
            }));
        return tooltip.Length > 63 ? tooltip[..60] + "..." : tooltip;
    }

    public void ShowFallbackNotification(string message)
    {
        // User-initiated actions (popup start, settings, refresh) must not fail silently. The old
        // WinForms balloon was an unbranded second notification system, so the detail goes through
        // the same branded Windows App SDK pipeline as the quota/auth alerts instead. The
        // diagnostic line is kept for the log so the same message can be correlated with the
        // failing operation.
        FileDiagnosticsLogger.Default.Info("Shell fallback notification raised", new Dictionary<string, object?>
        {
            ["message"] = message
        });
        _appNotifications.ShowFallbackAlert(message);
    }

    public void ShowQuotaNotification(string message)
    {
        if (_disposed) return;
        _appNotifications.ShowQuotaAlert(message);
    }

    public void ShowAuthNotification(string message)
    {
        if (_disposed) return;
        _appNotifications.ShowAuthAlert(message);
    }

    private void OnTaskbarStateChanged(object? sender, TaskbarStateChangedEventArgs e)
    {
        // Embedded Explorer placement is unsupported. If it falls back to the visible taskbar-edge
        // window, tell the user once so they understand the mode they are seeing. De-duplicate shell
        // watchdog chatter, while still surfacing a fresh failure after a later reconnect. These
        // transitions are log-only: the watchdog re-attaches on its own, so a toast here would
        // fire on every Explorer shell restart without giving the user anything to do.
        var shouldNotify = _app.Settings.StatusSurface == StatusSurfaceMode.TaskbarWidget && !e.Attached;
        if (shouldNotify && !string.Equals(_lastTaskbarNotice, e.Message, StringComparison.Ordinal))
        {
            _lastTaskbarNotice = e.Message;
            FileDiagnosticsLogger.Default.Info("Taskbar strip state changed",
                new Dictionary<string, object?> { ["message"] = e.Message });
        }
        else if (e.Attached)
        {
            _lastTaskbarNotice = null;
        }

        if (_widgetAttached != e.Attached)
        {
            _widgetAttached = e.Attached;
            RefreshTooltip();
        }
    }

    private void ToggleDashboard()
    {
        _dashboard.Dispatcher.BeginInvoke(new Action(() =>
        {
            var anchor = NativeMethods.GetCursorPos(out var cursor)
                ? new System.Drawing.Point(cursor.X, cursor.Y)
                : Forms.Cursor.Position;
            _ = ToggleDashboardAsync(anchor);
        }));
    }

    private async Task ToggleDashboardAsync(System.Drawing.Point anchor)
    {
        try
        {
            if (await _dashboard.TryToggleTauriPopupAsync(anchor, useWidgetAvoidRect: false).ConfigureAwait(true))
                return;
            if (_dashboard.IsVisible && _dashboard.WindowState == WindowState.Normal)
                _dashboard.WindowState = WindowState.Minimized;
            else
                await _dashboard.ShowFromTrayAsync(anchor, useWidgetAvoidRect: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            FileDiagnosticsLogger.Default.Warning("The tray toggle could not open the popup", exception: exception);
        }
    }

    private void ShowTrayMenu()
    {
        if (_notifyIcon is null || _disposed) return;
        var generation = Interlocked.Increment(ref _menuGeneration);
        try
        {
            _dashboard.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || generation != Interlocked.Read(ref _menuGeneration)) return;
                try
                {
                    _menu?.CloseSafely();
                    var cursor = Forms.Cursor.Position;
                    var actions = new TrayMenuActions(
                        OpenDashboard: () => _dashboard.ShowFromTray(cursor, useWidgetAvoidRect: false),
                        Refresh: () => _dashboard.RefreshData(),
                        Settings: () => _dashboard.ShowSettingsPage(cursor, useWidgetAvoidRect: false),
                        Customize: () => _dashboard.ShowCustomizePage(cursor, useWidgetAvoidRect: false),
                        CheckForUpdates: _dashboard.ShowUpdateStatus,
                        Quit: _app.Shutdown,
                        Monitors: _placement.GetMonitors(),
                        SelectedMonitor: _app.Settings.SelectedMonitor,
                        SelectMonitor: monitor =>
                        {
                            var updated = _app.Settings.Clone();
                            updated.SelectedMonitor = monitor.Id;
                            updated.StatusSurface = StatusSurfaceMode.TaskbarWidget;
                            _app.SaveSettings(updated);
                        });
                    var menu = new TrayMenuWindow(actions, cursor);
                    if (_disposed || generation != Interlocked.Read(ref _menuGeneration))
                    {
                        menu.CloseSafely();
                        return;
                    }
                    _menu = menu;
                    menu.Closed += (_, _) =>
                    {
                        if (ReferenceEquals(_menu, menu)) _menu = null;
                    };
                    menu.Show();
                }
                catch (Exception ex)
                {
                    FileDiagnosticsLogger.Default.Warning("Tray menu could not be opened", exception: ex);
                    ShowBasicFallbackMenu(Forms.Cursor.Position);
                }
            }), DispatcherPriority.Input);
        }
        catch (InvalidOperationException)
        {
            // The desktop dispatcher can be shutting down while NotifyIcon delivers its final
            // mouse message. Do not turn a late tray click into an application crash.
        }
    }

    private void ShowBasicFallbackMenu(System.Drawing.Point cursor)
    {
        try
        {
            _fallbackMenu?.Dispose();
            var menu = new Forms.ContextMenuStrip();
            _fallbackMenu = menu;
            menu.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_fallbackMenu, menu)) return;
                _fallbackMenu = null;
                menu.Dispose();
            };
            menu.Items.Add("Open dashboard", null, (_, _) => _dashboard.ShowFromTray(cursor, useWidgetAvoidRect: false));
            menu.Items.Add("Refresh now", null, (_, _) => _dashboard.RefreshData());
            menu.Items.Add("Settings", null, (_, _) => _dashboard.ShowSettingsPage(cursor, useWidgetAvoidRect: false));
            menu.Items.Add("Customize", null, (_, _) => _dashboard.ShowCustomizePage(cursor, useWidgetAvoidRect: false));
            menu.Items.Add("Check for updates", null, (_, _) => _dashboard.ShowUpdateStatus());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => _app.Shutdown());
            menu.Show(cursor);
        }
        catch (Exception fallbackException)
        {
            FileDiagnosticsLogger.Default.Warning("Tray fallback menu could not be opened", exception: fallbackException);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _menuGeneration);
        _countdownTimer?.Stop();
        _countdownTimer = null;
        if (_taskbar is not null) _taskbar.StateChanged -= OnTaskbarStateChanged;
        try { _menu?.CloseSafely(); } catch (InvalidOperationException) { }
        try { _fallbackMenu?.Close(); _fallbackMenu?.Dispose(); } catch (InvalidOperationException) { }
        if (_notifyIcon is not null)
        {
            var icon = _notifyIcon.Icon;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            icon?.Dispose();
        }
    }
}
