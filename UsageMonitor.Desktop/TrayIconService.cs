using System.Drawing;
using System.Drawing.Drawing2D;
using DrawingSize = System.Drawing.Size;
using System.Windows;
using System.Windows.Threading;
using UsageMonitor.Core;
using Forms = System.Windows.Forms;

namespace UsageMonitor.Desktop;

public sealed class TrayIconService : IDisposable
{
    private readonly App _app;
    private readonly MainWindow _dashboard;
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

    public TrayIconService(App app, MainWindow dashboard)
    {
        _app = app;
        _dashboard = dashboard;
    }

    public void Initialize()
    {
        if (_notifyIcon is not null) return;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Usage Monitor | no provider data yet",
            Icon = CreateIcon(Array.Empty<MetricDisplay>()),
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
        var values = _metrics.ToList();
        var tooltip = values.Count == 0
            ? "Usage Monitor | no provider data yet"
            : "Usage Monitor | " + string.Join(" | ", values.Select(v =>
            {
                var reset = v.ResetAt is { } resetAt
                    ? $" ({ResetTimeFormatter.Format(resetAt, _app.Settings.ResetTimeDisplay)})"
                    : string.Empty;
                return $"{v.Provider} {v.Value}{reset}";
            }));
        if (tooltip.Length > 63) tooltip = tooltip[..60] + "...";
        _notifyIcon.Text = tooltip;
        var old = _notifyIcon.Icon;
        // The taskbar widget is the live quota surface when it is actually embedded. Showing the
        // same progress glyph here too would put two quota counters on screen at once, so the tray
        // icon falls back to a plain mark while the widget owns the display.
        _notifyIcon.Icon = _widgetAttached ? PlainIcon() : CreateIcon(_metrics);
        old?.Dispose();
    }

    public void ShowFallbackNotification(string message)
    {
        _notifyIcon?.ShowBalloonTip(5000, "Usage Monitor", message, Forms.ToolTipIcon.Info);
    }

    public void ShowQuotaNotification(string message)
    {
        _notifyIcon?.ShowBalloonTip(6000, "Usage Monitor quota alert", message, Forms.ToolTipIcon.Warning);
    }

    private void OnTaskbarStateChanged(object? sender, TaskbarStateChangedEventArgs e)
    {
        // Embedded Explorer placement is unsupported. If it falls back to the visible taskbar-edge
        // window, tell the user once so they understand the mode they are seeing. De-duplicate shell
        // watchdog chatter, while still surfacing a fresh failure after a later reconnect.
        var shouldNotify = _app.Settings.StatusSurface == StatusSurfaceMode.TaskbarWidget && !e.Attached;
        if (shouldNotify && !string.Equals(_lastTaskbarNotice, e.Message, StringComparison.Ordinal))
        {
            _lastTaskbarNotice = e.Message;
            ShowFallbackNotification(e.Message);
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
            if (_dashboard.TryToggleTauriPopup(anchor))
            {
                return;
            }
            if (_dashboard.IsVisible && _dashboard.WindowState == WindowState.Normal)
                _dashboard.WindowState = WindowState.Minimized;
            else
                _dashboard.ShowFromTray(anchor);
        }));
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
                        OpenDashboard: ToggleDashboard,
                        Refresh: () => _dashboard.RefreshData(force: true),
                        Settings: () => _dashboard.ShowSettingsPage(),
                        Customize: () => _dashboard.ShowCustomizePage(),
                        CheckForUpdates: _dashboard.ShowUpdateStatus,
                        About: _dashboard.ShowAbout,
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
                    new FileDiagnosticsLogger().Warning("Tray menu could not be opened", exception: ex);
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
            menu.Items.Add("Open dashboard", null, (_, _) => ToggleDashboard());
            menu.Items.Add("Refresh now", null, (_, _) => _dashboard.RefreshData(force: true));
            menu.Items.Add("Settings", null, (_, _) => _dashboard.ShowSettingsPage());
            menu.Items.Add("Customize", null, (_, _) => _dashboard.ShowCustomizePage());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => _app.Shutdown());
            menu.Show(cursor);
        }
        catch (Exception fallbackException)
        {
            new FileDiagnosticsLogger().Warning("Tray fallback menu could not be opened", exception: fallbackException);
        }
    }

    private static Icon CreateIcon(IReadOnlyList<MetricDisplay> metrics)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        // Keep the tray glyph visually aligned with the native taskbar glyph. A tiny provider marker
        // prevents the old anonymous blue line from looking like a broken badge, while the tooltip
        // remains the exact-value surface for users who need labels and reset times.
        var items = metrics.Where(TaskbarGlyphRenderer.HasRenderableData).Where(metric => metric.IsMeter).ToArray();
        if (items.Length == 0)
        {
            DrawFallbackIcon(graphics);
            return Icon.FromHandle(bitmap.GetHicon());
        }

        const int markerX = 3;
        const int barLeft = 8;
        const int width = 21;
        const int height = 4;
        const int gap = 2;
        var top = (32 - (items.Length * height + (items.Length - 1) * gap)) / 2;
        using var track = new SolidBrush(Color.FromArgb(115, 165, 175, 186));
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var y = top + i * (height + gap);
            var color = AccentColor(item);
            using var progressFill = new SolidBrush(color);
            graphics.FillEllipse(progressFill, new Rectangle(markerX, y, height, height));
            graphics.FillRoundedRectangle(track, new Rectangle(barLeft, y, width, height), new DrawingSize(2, 2));
            var progress = Math.Clamp(item.Progress, 0, 1);
            if (progress > 0)
            {
                var visible = TaskbarGlyphRenderer.VisualFraction(progress);
                var fillWidth = Math.Max(2, Math.Min(width, (int)Math.Round(width * visible)));
                graphics.FillRoundedRectangle(progressFill, new Rectangle(barLeft, y, fillWidth, height), new DrawingSize(2, 2));
                if (visible < 1)
                {
                    using var remainder = new SolidBrush(Color.FromArgb(60, color));
                    graphics.FillRoundedRectangle(remainder,
                        new Rectangle(barLeft + fillWidth, y, Math.Max(1, width - fillWidth), height), new DrawingSize(1, 2));
                }
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static Icon PlainIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        DrawFallbackIcon(graphics);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static void DrawFallbackIcon(Graphics graphics)
    {
        // Use the same compact bar mark as the taskbar fallback. A gauge-and-needle reads like a
        // warning badge at 16px, while bars remain legible and consistent with the live icon.
        using var accent = new SolidBrush(Color.FromArgb(83, 210, 195));
        using var track = new SolidBrush(Color.FromArgb(120, 165, 175, 186));
        const int width = 5;
        const int gap = 3;
        var x = (32 - (width * 3 + gap * 2)) / 2;
        const int baseline = 24;
        foreach (var height in new[] { 10, 16, 22 })
        {
            graphics.FillRoundedRectangle(track, new Rectangle(x, baseline - 22, width, 22), new DrawingSize(2, 2));
            graphics.FillRoundedRectangle(accent, new Rectangle(x, baseline - height, width, height), new DrawingSize(2, 2));
            x += width + gap;
        }
    }

    private static Color AccentColor(MetricDisplay metric)
    {
        switch (metric.State?.ToLowerInvariant())
        {
            case "warn": return Color.FromArgb(255, 179, 64);
            case "danger": return Color.FromArgb(242, 121, 135);
            case "neutral": return Color.FromArgb(154, 160, 171);
        }

        return metric.Provider?.ToLowerInvariant() switch
        {
            "codex" => Color.FromArgb(61, 130, 246),
            "claude" or "claude code" => Color.FromArgb(218, 119, 86),
            "antigravity" => Color.FromArgb(52, 168, 83),
            "opencode" => Color.FromArgb(45, 212, 191),
            "cursor" => Color.FromArgb(108, 123, 255),
            "copilot" => Color.FromArgb(137, 87, 229),
            "devin" => Color.FromArgb(255, 180, 84),
            "grok" => Color.FromArgb(201, 206, 214),
            _ => Color.FromArgb(83, 210, 195)
        };
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
