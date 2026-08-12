using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageMonitor.Core;

namespace UsageMonitor.Desktop;

/// <summary>
/// Owns the one native taskbar overlay. WPF is used only to render the existing strip visual into
/// a bitmap. The visible hit-test and z-order surface is a native layered tool window.
/// </summary>
public sealed class TaskbarOverlayController : IDisposable
{
    internal const string NativeOverlayMarker = "UsageMonitor.TaskbarOverlay.Native";
    private readonly MainWindow _dashboard;
    private readonly MonitorPlacementService _placement = new();
    private readonly DispatcherTimer _safetyTimer;
    private readonly DebouncedPlacementPersister _placementPersister;
    private readonly WidgetWindow _renderer = new();
    private readonly Window _rendererHost;
    private readonly NativeMethods.WinEventDelegate _winEventCallback;
    private readonly List<IntPtr> _eventHooks = [];
    private NativeTaskbarOverlay? _overlay;
    private IntPtr _taskbarHandle;
    private string _monitorId = MonitorPlacementService.PrimaryMonitorId;
    private bool _disposed;
    private bool _retryPending;
    private bool _monitorFallbackNotified;
    private bool _popupVisible;
    private double _edgeOffsetDip = TaskbarWidgetPlacement.Default.EdgeOffsetDip;
    private IReadOnlyList<MetricDisplay> _metrics = Array.Empty<MetricDisplay>();
    private int _syncQueued;
    private string _queuedReason = "queued";
    private readonly SyncPromoteState _promoteState = new();
    private bool _bitmapInvalidated = true;
    private double _renderedScale;
    private double _renderedWidthDip;
    private bool _dragActive;
    private bool _fullscreenActive;
    private string _dragMonitorId = string.Empty;
    private System.Drawing.Point _dragStartCursor;
    private System.Drawing.Rectangle _dragStartBounds;

    public TaskbarOverlayController(MainWindow dashboard)
    {
        _dashboard = dashboard;
        _rendererHost = new Window
        {
            Content = _renderer,
            Width = 320,
            Height = 80,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = System.Windows.Media.Brushes.Transparent
        };
        _rendererHost.Show();
        _winEventCallback = OnWinEvent;
        _safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _safetyTimer.Tick += (_, _) => QueueSynchronize("safety-timer");
        _safetyTimer.Start();
        _placementPersister = new DebouncedPlacementPersister(
            App.CurrentApp.PersistWidgetPlacement,
            TimeSpan.FromMilliseconds(250));
        RegisterShellEvents();
    }

    public bool IsAttached { get; private set; }
    public event EventHandler<TaskbarStateChangedEventArgs>? StateChanged;

#if DEBUG
    internal string DumpDiagnostics()
    {
        LogState("manual-dump", "diagnostics", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
        return GetLogPath();
    }
#endif

    public bool TryGetWidgetBounds(out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;
        if (_overlay is null || !NativeMethods.GetWindowRect(_overlay.Handle, out var rect)) return false;
        bounds = new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return !bounds.IsEmpty && _overlay.IsVisible;
    }

    public bool TryAttach(string? monitorId)
    {
        if (_disposed) return false;

        var requestedId = string.IsNullOrWhiteSpace(monitorId)
            ? MonitorPlacementService.PrimaryMonitorId
            : monitorId.Trim();
        if (!IsMonitorAvailable(requestedId))
        {
            _monitorId = MonitorPlacementService.PrimaryMonitorId;
            App.CurrentApp.PersistMonitorFallback(_monitorId);
            if (!_monitorFallbackNotified)
            {
                _monitorFallbackNotified = true;
                StateChanged?.Invoke(this, new TaskbarStateChangedEventArgs(false,
                    "The selected display is unavailable. TokenBurn switched the widget to the primary display."));
            }
        }
        else
        {
            _monitorId = requestedId;
            _monitorFallbackNotified = false;
        }

        _edgeOffsetDip = App.CurrentApp.Settings.GetWidgetPlacement(_monitorId).EdgeOffsetDip;
        LogState("attach-loaded-placement", "read-placement", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
        var screen = _placement.ResolveScreen(_monitorId);
        _taskbarHandle = _placement.GetTaskbarHandle(screen);
        if (_taskbarHandle == IntPtr.Zero)
        {
            SetFallback("Windows taskbar is unavailable. TokenBurn will keep running in the tray.");
            _retryPending = true;
            return false;
        }

        try
        {
            EnsureOverlay();
            _overlay!.SetPositionLocked(App.CurrentApp.Settings.TaskbarPositionLocked);
            _overlay.SetScreenShareExcluded(App.CurrentApp.Settings.HideFromScreenShare);
            _renderer.ResetTimeDisplay = App.CurrentApp.Settings.ResetTimeDisplay;
            _renderer.SetMetrics(_metrics);
            _bitmapInvalidated = true;
            IsAttached = true;
            ReconcileTaskbarStrip("attach");
            _retryPending = false;
            StateChanged?.Invoke(this, new TaskbarStateChangedEventArgs(true, "Taskbar status strip active."));
            return true;
        }
        catch (Exception ex)
        {
            FileDiagnosticsLogger.Default.Warning("Native taskbar overlay attach failed", exception: ex);
            SetFallback($"Taskbar status strip could not attach ({ex.Message}). Tray mode remains available.");
            _retryPending = true;
            return false;
        }
    }

    public void UpdateMetrics(IEnumerable<MetricDisplay> metrics)
    {
        _metrics = (metrics ?? Array.Empty<MetricDisplay>()).ToArray();
        _bitmapInvalidated = true;
        if (!IsAttached) return;
        _renderer.ResetTimeDisplay = App.CurrentApp.Settings.ResetTimeDisplay;
        _renderer.SetMetrics(_metrics);
        ReconcileTaskbarStrip("metrics-updated");
    }

    public void ApplyPositionLock(bool locked)
    {
        if (locked) _dragActive = false;
        _overlay?.SetPositionLocked(locked);
        LogState(locked ? "position-locked" : "position-unlocked", "settings", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
        // Re-assert the strip after the lock change so the layered surface repositions/re-paints
        // cleanly instead of lingering in whatever compositing state the previous drag left it in.
        ReconcileTaskbarStrip(locked ? "position-locked" : "position-unlocked");
    }

    public void ApplyScreenSharePrivacy(bool excluded)
    {
        _overlay?.SetScreenShareExcluded(excluded);
        LogState(excluded ? "screen-share-excluded" : "screen-share-visible", "settings", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
    }

    public void SetPopupVisible(bool visible)
    {
        if (_disposed) return;
        if (!_dashboard.Dispatcher.CheckAccess())
        {
            _dashboard.Dispatcher.BeginInvoke(() => SetPopupVisible(visible), DispatcherPriority.Normal);
            return;
        }

        _popupVisible = visible;
        LogState(visible ? "popup-shown" : "popup-hidden", "popup-visibility", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
        if (!IsAttached || _overlay is null) return;
        // The strip is independent of popup visibility. Reconcile it in place so a popup
        // transition can never blank or destroy the last good metric bitmap.
        ReconcileTaskbarStrip(visible ? "popup-shown" : "popup-hidden", promote: !visible);
    }

    public void RestoreAfterPopupDismissal()
    {
        if (_disposed || !IsAttached || _overlay is null) return;
        if (!_dashboard.Dispatcher.CheckAccess())
        {
            _dashboard.Dispatcher.BeginInvoke(RestoreAfterPopupDismissal);
            return;
        }

        _popupVisible = false;
        ReconcileTaskbarStrip("popup-dismissed", promote: true);
    }

    public void Detach()
    {
        LogState("detach", "destroy", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
        IsAttached = false;
        _overlay?.Destroy();
        _overlay = null;
        _taskbarHandle = IntPtr.Zero;
        StateChanged?.Invoke(this, new TaskbarStateChangedEventArgs(false, "Tray only mode is active."));
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null && NativeMethods.IsWindow(_overlay.Handle)) return;
        NativeMethods.CloseStaleOverlayWindows(
            NativeTaskbarOverlay.WindowTitle,
            NativeTaskbarOverlay.OverlayMarker,
            Environment.ProcessId);
        NativeMethods.CloseStaleOverlayWindows(
            NativeTaskbarOverlay.WindowTitle,
            NativeMethods.LegacyTaskbarOverlayMarker,
            Environment.ProcessId);
        _overlay?.Destroy();
        _overlay = new NativeTaskbarOverlay(
            click: point => _dashboard.ToggleFromTaskbarIndicator(point),
            dragStart: point => BeginDrag(point),
            drag: point => ApplyDragDelta(point),
            dragEnd: () => _placementPersister.Flush());
    }

    private void ReconcileTaskbarStrip(string reason, bool promote = false)
    {
        if (_disposed) return;
        if (!IsAttached)
        {
            if (_retryPending) TryAttach(_monitorId);
            return;
        }
        if (_overlay is null || _taskbarHandle == IntPtr.Zero || !NativeMethods.IsWindow(_taskbarHandle))
        {
            SetFallback("Explorer recreated the taskbar. Retrying the status strip shortly.");
            _retryPending = true;
            return;
        }

        var expected = _placement.GetTaskbarHandle(_placement.ResolveScreen(_monitorId));
        if (expected != _taskbarHandle)
        {
            _taskbarHandle = expected;
            if (_taskbarHandle == IntPtr.Zero)
            {
                SetFallback("Explorer recreated the taskbar. Retrying the status strip shortly.");
                _retryPending = true;
                return;
            }
        }

        // A fullscreen app covers the taskbar area of its monitor, but the strip is topmost and
        // would float over the video/game in the corner. Hide it while the foreground window is
        // fullscreen on the strip's monitor, and let the next reconcile show it again.
        if (IsStripCoveredByFullscreen())
        {
            if (!_fullscreenActive)
            {
                _fullscreenActive = true;
                LogState($"{reason}.fullscreen", "hide", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
            }
            _overlay.Hide();
            return;
        }
        if (_fullscreenActive)
        {
            _fullscreenActive = false;
            LogState($"{reason}.fullscreen-ended", "show", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
        }

        var bounds = _placement.GetTaskbarBounds(_taskbarHandle);
        if (bounds.IsEmpty) return;
        var dpi = NativeMethods.GetDpiForWindow(_taskbarHandle);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;
        var trayNotify = NativeMethods.TryGetTaskbarTrayNotifyBounds(_taskbarHandle, out var trayNotifyRect)
            ? new System.Drawing.Rectangle(
                trayNotifyRect.Left,
                trayNotifyRect.Top,
                trayNotifyRect.Right - trayNotifyRect.Left,
                trayNotifyRect.Bottom - trayNotifyRect.Top)
            : System.Drawing.Rectangle.Empty;
        var reservedEdgeDip = TaskbarStripPlacement.CalculateReservedEdgeDip(bounds, trayNotify, dpi);
        var mainAxisPixels = bounds.Width < bounds.Height ? bounds.Height : bounds.Width;
        var layout = TaskbarLayoutDecision.Calculate(
            mainAxisPixels / scale,
            reservedEdgeDip);
        _renderer.SetAvailableWidthDip(layout.AvailableWidthDip);
        _rendererHost.Width = Math.Max(40, _renderer.IdealWidthDip);
        // The native layered window covers the complete taskbar cross-axis. Render into that
        // exact size instead of handing UpdateLayeredWindow a 38px bitmap for a 60px window.
        // Passing mismatched source and destination heights lets GDI read beyond the DIB and was
        // the most plausible source of the intermittent disappear/reappear behavior.
        _rendererHost.Height = Math.Max(30, bounds.Height / scale);
        _rendererHost.UpdateLayout();
        var placement = TaskbarStripPlacement.Calculate(
            bounds,
            _renderer.IdealWidthDip,
            layout.AvailableWidthDip,
            _edgeOffsetDip,
            dpi,
            reservedEdgeDip);
        if (placement.ResetPersistedOffset)
        {
            _edgeOffsetDip = placement.EdgeOffsetDip;
            App.CurrentApp.PersistWidgetPlacement(_monitorId, _edgeOffsetDip);
            LogState($"{reason}.invalid-placement-reset", "reset-placement", placement.Bounds, false, false, dpi, _edgeOffsetDip);
        }
        else
        {
            _edgeOffsetDip = placement.EdgeOffsetDip;
        }

        if (!placement.IsSane(bounds))
        {
            LogState($"{reason}.invariant-failed", "reconcile-rejected", placement.Bounds, false, false, dpi, _edgeOffsetDip);
            SetFallback("The taskbar geometry was invalid. TokenBurn will retry shortly.");
            _retryPending = true;
            return;
        }

        var needsBitmap = _bitmapInvalidated || !_overlay.HasBitmap ||
            Math.Abs(_renderedScale - scale) > 0.001 ||
            Math.Abs(_renderedWidthDip - _renderer.IdealWidthDip) > 0.001;
        var shouldPromote = promote || (!_popupVisible && !NativeMethods.IsWindowAbove(_overlay.Handle, _taskbarHandle));
        var positionMatches = NativeMethods.GetWindowRect(_overlay.Handle, out var actualOverlayRect) &&
            actualOverlayRect.Left == placement.Bounds.Left && actualOverlayRect.Top == placement.Bounds.Top &&
            actualOverlayRect.Right == placement.Bounds.Right && actualOverlayRect.Bottom == placement.Bounds.Bottom;
        var needsPosition = needsBitmap || !positionMatches || !_overlay.IsVisible || shouldPromote;
        // The 750 ms safety reconcile reaches this point on every heartbeat even when nothing
        // moved or repainted. Writing a full state entry then was unbounded, always-hot logging:
        // keep the placement log for real transitions (attach, detach, fallback, fullscreen,
        // placement resets, moves, repaints) and stay silent for pure no-ops.
        var changed = needsBitmap || needsPosition;
        var operation = changed ? (needsBitmap ? "move/show/repaint" : "move/show") : "noop";
        if (changed)
            LogState($"{reason}.before", operation, placement.Bounds, false, false, dpi, _edgeOffsetDip);
        if (needsBitmap)
        {
            var image = _renderer.RenderToBitmap(scale);
            _overlay.SetBitmap(image, placement.Bounds.Width, placement.Bounds.Height);
            _bitmapInvalidated = false;
            _renderedScale = scale;
            _renderedWidthDip = _renderer.IdealWidthDip;
        }
        var layeredPresented = needsPosition
            ? _overlay.SetPosition(placement.Bounds, shouldPromote, needsBitmap)
            : _overlay.HasBitmap;
        if (changed || !layeredPresented)
            LogState($"{reason}.after", needsPosition
                    ? (layeredPresented ? operation : $"{operation}-failed")
                    : "noop",
                placement.Bounds, needsBitmap, layeredPresented, dpi, _edgeOffsetDip);
    }

    private void BeginDrag(System.Drawing.Point point)
    {
        _dragActive = false;
        if (_overlay is null || !NativeMethods.GetWindowRect(_overlay.Handle, out var bounds)) return;
        _dragActive = true;
        _dragStartCursor = point;
        _dragStartBounds = new System.Drawing.Rectangle(
            bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        _dragMonitorId = _monitorId;
    }

    private void ApplyDragDelta(WidgetDragDeltaEventArgs delta)
    {
        if (!IsAttached || _overlay is null || _taskbarHandle == IntPtr.Zero) return;
        if (TryMoveToCursorMonitor(delta.CurrentPoint))
        {
            _dragActive = false;
            _edgeOffsetDip = App.CurrentApp.Settings.GetWidgetPlacement(_monitorId).EdgeOffsetDip;
            ReconcileTaskbarStrip("drag-monitor-changed", promote: true);
            BeginDrag(delta.CurrentPoint);
            return;
        }
        if (!_dragActive || !_dragMonitorId.Equals(_monitorId, StringComparison.OrdinalIgnoreCase) ||
            !NativeMethods.GetWindowRect(_taskbarHandle, out var taskbarRect)) return;
        var dpi = NativeMethods.GetDpiForWindow(_taskbarHandle);
        if (dpi == 0) dpi = 96;
        var trayNotify = NativeMethods.TryGetTaskbarTrayNotifyBounds(_taskbarHandle, out var trayNotifyRect)
            ? new System.Drawing.Rectangle(trayNotifyRect.Left, trayNotifyRect.Top,
                trayNotifyRect.Right - trayNotifyRect.Left, trayNotifyRect.Bottom - trayNotifyRect.Top)
            : System.Drawing.Rectangle.Empty;
        var reservedEdgeDip = TaskbarStripPlacement.CalculateReservedEdgeDip(
            new System.Drawing.Rectangle(taskbarRect.Left, taskbarRect.Top,
                taskbarRect.Right - taskbarRect.Left, taskbarRect.Bottom - taskbarRect.Top),
            trayNotify, dpi);
        _edgeOffsetDip = TaskbarStripPlacement.CalculateDraggedEdgeOffset(
            new System.Drawing.Rectangle(taskbarRect.Left, taskbarRect.Top,
                taskbarRect.Right - taskbarRect.Left, taskbarRect.Bottom - taskbarRect.Top),
            _dragStartBounds,
            (delta.CurrentPoint.X - _dragStartCursor.X) / (dpi / 96.0),
            (delta.CurrentPoint.Y - _dragStartCursor.Y) / (dpi / 96.0),
            dpi,
            reservedEdgeDip);
        // The position itself still reconciles on every move so the strip tracks the cursor; only
        // the settings write is debounced, so a drag persists at most a few times instead of once
        // per mouse-move (each write is a synchronous settings.json rewrite on the UI thread).
        _placementPersister.Request(_monitorId, _edgeOffsetDip);
        ReconcileTaskbarStrip("drag", promote: true);
    }

    private bool TryMoveToCursorMonitor(System.Drawing.Point cursor)
    {
        var target = _placement.GetMonitorAtTaskbarPoint(cursor);
        if (target is null) return false;
        var targetId = _placement.GetMonitorId(target);
        if (targetId.Equals(_monitorId, StringComparison.OrdinalIgnoreCase)) return false;
        var targetTaskbar = _placement.GetTaskbarHandle(target);
        if (targetTaskbar == IntPtr.Zero) return false;
        _taskbarHandle = targetTaskbar;
        _monitorId = targetId;
        _edgeOffsetDip = App.CurrentApp.Settings.GetWidgetPlacement(_monitorId).EdgeOffsetDip;
        App.CurrentApp.PersistMonitorSelection(_monitorId);
        return true;
    }

    private bool IsMonitorAvailable(string monitorId)
        => monitorId.Equals(MonitorPlacementService.PrimaryMonitorId, StringComparison.OrdinalIgnoreCase) ||
           _placement.GetMonitors().Any(m => m.Id.Equals(monitorId, StringComparison.OrdinalIgnoreCase));

    private bool IsStripCoveredByFullscreen()
    {
        if (_overlay is null || _taskbarHandle == IntPtr.Zero) return false;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _overlay.Handle) return false;
        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        if (foregroundProcessId == (uint)Environment.ProcessId) return false;
        if (!NativeMethods.IsWindowVisible(foreground) || NativeMethods.IsIconic(foreground)) return false;
        // Shell/desktop windows (GetShellWindow, Progman, WorkerW) cover the whole monitor without
        // being a fullscreen app. Clicking the desktop or pressing Win+D makes one of them the
        // foreground window, which would otherwise hide the strip for no reason.
        if (foreground == NativeMethods.GetShellWindow()) return false;
        if (NativeMethods.GetWindowClassName(foreground) is "Progman" or "WorkerW") return false;
        if (!NativeMethods.GetWindowRect(foreground, out var windowRect)) return false;
        var window = new System.Drawing.Rectangle(
            windowRect.Left, windowRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);
        if (window.IsEmpty) return false;

        var stripMonitor = NativeMethods.MonitorFromWindow(_taskbarHandle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var foregroundMonitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (stripMonitor == IntPtr.Zero || foregroundMonitor != stripMonitor) return false;

        var info = new NativeMethods.MONITORINFO { Size = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(foregroundMonitor, ref info)) return false;
        var monitor = new System.Drawing.Rectangle(
            info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top);
        // Maximized windows stop at the working area; fullscreen windows cover the whole monitor
        // including the taskbar band, which is exactly when the strip would overlap the app.
        const int tolerance = 4;
        return Math.Abs(window.Left - monitor.Left) <= tolerance &&
               Math.Abs(window.Top - monitor.Top) <= tolerance &&
               Math.Abs(window.Right - monitor.Right) <= tolerance &&
               Math.Abs(window.Bottom - monitor.Bottom) <= tolerance;
    }

    private void SetFallback(string message)
    {
        var wasAttached = IsAttached;
        LogState("fallback", "hide", System.Drawing.Rectangle.Empty, false, false, 0, _edgeOffsetDip);
        IsAttached = false;
        _overlay?.Hide();
        _taskbarHandle = IntPtr.Zero;
        if (wasAttached || !_retryPending)
            StateChanged?.Invoke(this, new TaskbarStateChangedEventArgs(false, message));
    }

    private void LogState(
        string reason,
        string operation,
        System.Drawing.Rectangle intended,
        bool layeredContentUpdated,
        bool layeredPresented,
        uint dpi,
        double persistedEdgeOffsetDip)
    {
        try
        {
            var overlayHandle = _overlay?.Handle ?? IntPtr.Zero;
            var actual = overlayHandle != IntPtr.Zero && NativeMethods.GetWindowRect(overlayHandle, out var actualRect)
                ? FormatRect(actualRect)
                : null;
            var taskbarRect = default(NativeMethods.RECT);
            var hasTaskbarRect = _taskbarHandle != IntPtr.Zero && NativeMethods.GetWindowRect(_taskbarHandle, out taskbarRect);
            var taskbar = hasTaskbarRect
                ? FormatRect(taskbarRect)
                : null;
            var trayNotifyRect = default(NativeMethods.RECT);
            var hasTrayNotifyRect = _taskbarHandle != IntPtr.Zero &&
                NativeMethods.TryGetTaskbarTrayNotifyBounds(_taskbarHandle, out trayNotifyRect);
            var effectiveDpi = dpi != 0 ? dpi : (_taskbarHandle != IntPtr.Zero ? NativeMethods.GetDpiForWindow(_taskbarHandle) : 96);
            if (effectiveDpi == 0) effectiveDpi = 96;
            var taskbarBounds = hasTaskbarRect
                ? new System.Drawing.Rectangle(taskbarRect.Left, taskbarRect.Top,
                    taskbarRect.Right - taskbarRect.Left, taskbarRect.Bottom - taskbarRect.Top)
                : System.Drawing.Rectangle.Empty;
            var trayBounds = hasTrayNotifyRect
                ? new System.Drawing.Rectangle(trayNotifyRect.Left, trayNotifyRect.Top,
                    trayNotifyRect.Right - trayNotifyRect.Left, trayNotifyRect.Bottom - trayNotifyRect.Top)
                : System.Drawing.Rectangle.Empty;
            var taskbarRight = taskbar is null ? 0 : taskbarRect.Right;
            var screen = _taskbarHandle != IntPtr.Zero
                ? System.Windows.Forms.Screen.FromHandle(_taskbarHandle)
                : null;
            var foreground = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
            var foregroundProcess = string.Empty;
            try { foregroundProcess = Process.GetProcessById((int)foregroundProcessId).ProcessName; } catch { }
            var above = overlayHandle == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetWindow(overlayHandle, NativeMethods.GwHwndPrev);
            var below = overlayHandle == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetWindow(overlayHandle, NativeMethods.GwHwndNext);
            var entry = new
            {
                Timestamp = DateTimeOffset.Now,
                Reason = reason,
                Operation = operation,
                OverlayHwnd = FormatHandle(overlayHandle),
                OverlayIsWindow = overlayHandle != IntPtr.Zero && NativeMethods.IsWindow(overlayHandle),
                OverlayVisible = overlayHandle != IntPtr.Zero && NativeMethods.IsWindowVisible(overlayHandle),
                ActualRect = actual,
                IntendedRect = FormatRect(intended),
                TaskbarHwnd = FormatHandle(_taskbarHandle),
                TaskbarRect = taskbar,
                Monitor = screen?.DeviceName ?? _monitorId,
                WorkArea = screen is null ? null : $"{screen.WorkingArea.Left},{screen.WorkingArea.Top},{screen.WorkingArea.Right},{screen.WorkingArea.Bottom}",
                Dpi = dpi,
                TrayNotifyRect = hasTrayNotifyRect ? FormatRect(trayNotifyRect) : null,
                ReservedTaskbarEdgeDip = TaskbarStripPlacement.CalculateReservedEdgeDip(taskbarBounds, trayBounds, effectiveDpi),
                PersistedEdgeOffsetDip = persistedEdgeOffsetDip,
                PersistedOffsetUnit = "DIP-from-trailing-taskbar-edge",
                CalculatedPhysicalX = intended.Left,
                CalculatedPhysicalY = intended.Top,
                CalculatedWidth = intended.Width,
                CalculatedHeight = intended.Height,
                CalculatedPhysicalEdgeOffset = taskbarRight - intended.Right,
                ForegroundHwnd = FormatHandle(foreground),
                ForegroundProcess = foregroundProcess,
                FullscreenActive = _fullscreenActive,
                ZNeighborAbove = FormatHandle(above),
                ZNeighborBelow = FormatHandle(below),
                LayeredBitmapAlphaPixels = _overlay?.BitmapAlphaPixels ?? 0,
                LayeredBitmapMaxAlpha = _overlay?.BitmapMaxAlpha ?? 0,
                LayeredBitmapNonZeroBytes = _overlay?.BitmapNonZeroBytes ?? 0,
                LayeredBitmapFirstBytes = _overlay?.BitmapFirstBytes,
                LayeredBitmapWidth = _overlay?.BitmapWidth ?? 0,
                LayeredBitmapHeight = _overlay?.BitmapHeight ?? 0,
                LayeredContentUpdated = layeredContentUpdated,
                LayeredPresented = layeredPresented
            };
            var directory = Path.GetDirectoryName(GetLogPath())!;
            Directory.CreateDirectory(directory);
            RotateStripLogIfNeeded();
            File.AppendAllText(GetLogPath(), JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch { }
    }

    private const long StripLogMaxBytes = 1_000_000;

    /// <summary>Bounded like the diagnostics log: once the strip log reaches its cap the old
    /// contents rotate to a single sibling backup instead of growing forever.</summary>
    private void RotateStripLogIfNeeded()
    {
        try
        {
            var path = GetLogPath();
            if (!File.Exists(path) || new FileInfo(path).Length < StripLogMaxBytes) return;
            var rotated = path + ".1";
            if (File.Exists(rotated)) File.Delete(rotated);
            File.Move(path, rotated);
        }
        catch { }
    }

    private static string? FormatRect(NativeMethods.RECT rect)
        => $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";

    private static string? FormatRect(System.Drawing.Rectangle rect)
        => rect.IsEmpty ? null : $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";

    private static string? FormatHandle(IntPtr handle)
        => handle == IntPtr.Zero ? null : $"0x{handle.ToInt64():X}";

    private static string GetLogPath()
        => UsageMonitorPaths.Current.TaskbarStripLogFile;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // A placement requested during the last drag moments must survive shutdown.
        _placementPersister.Flush();
        _safetyTimer.Stop();
        foreach (var hook in _eventHooks)
            NativeMethods.UnhookWinEvent(hook);
        _eventHooks.Clear();
        Detach();
        try { _rendererHost.Close(); } catch (InvalidOperationException) { }
    }

    private void RegisterShellEvents()
    {
        foreach (var (min, max) in new[]
        {
            (NativeMethods.EventSystemForeground, NativeMethods.EventSystemForeground),
            (NativeMethods.EventSystemMoveSizeEnd, NativeMethods.EventSystemMoveSizeEnd),
            (NativeMethods.EventSystemDesktopSwitch, NativeMethods.EventSystemDesktopSwitch),
            (NativeMethods.EventObjectShow, NativeMethods.EventObjectLocationChange)
        })
        {
            var hook = NativeMethods.SetWinEventHook(min, max, IntPtr.Zero, _winEventCallback, 0, 0,
                NativeMethods.WinEventOutOfContext);
            if (hook != IntPtr.Zero) _eventHooks.Add(hook);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (_disposed) return;
        var isSystemEvent = eventType < NativeMethods.EventObjectShow;
        if (!isSystemEvent)
        {
            var currentShellTaskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero || (hwnd != _taskbarHandle && hwnd != currentShellTaskbar)) return;
        }
        QueueSynchronize($"win-event-{eventType:X}", promote: eventType is NativeMethods.EventObjectShow or NativeMethods.EventObjectLocationChange);
    }

    private void QueueSynchronize(string reason, bool promote = false)
    {
        _queuedReason = reason;
        // A promote request that arrives while a sync is already queued must survive the
        // coalescing. The winning call used to capture its own promote flag, so a SHOW/
        // LOCATIONCHANGE promote that arrived behind an already-queued non-promote sync was
        // silently lost. The sticky counter is consumed when the sync actually runs.
        if (promote) _promoteState.MarkPending();
        if (_disposed || Interlocked.Exchange(ref _syncQueued, 1) != 0) return;
        try
        {
            _dashboard.Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _syncQueued, 0);
                ReconcileTaskbarStrip(_queuedReason, _promoteState.Consume());
            }), DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _syncQueued, 0);
        }
    }
}

/// <summary>
/// Tracks whether any coalesced sync request carried a promote intent. <see cref="TaskbarOverlayController.QueueSynchronize"/>
/// coalesces requests with a single queued flag; a later SHOW/LOCATIONCHANGE promote must not be
/// dropped just because an earlier non-promote sync won the queue slot. A monotonic pending count
/// makes the promote intent sticky until the sync actually runs, and later requests re-mark it.
/// </summary>
internal sealed class SyncPromoteState
{
    private int _pending;
    private int _applied;

    public void MarkPending() => Interlocked.Increment(ref _pending);

    /// <summary>Consumes the pending promote intent. Returns true when at least one promote was
    /// requested since the previous call. Safe to call repeatedly: a promote marked between the
    /// read and the write is never lost and is returned by the next call.</summary>
    public bool Consume()
    {
        var pending = Volatile.Read(ref _pending);
        var shouldPromote = pending > Volatile.Read(ref _applied);
        Volatile.Write(ref _applied, pending);
        return shouldPromote;
    }
}

/// <summary>
/// Bounds how often a high-frequency source (drag mouse moves) writes settings. The drag path
/// used to call SettingsStore.Save on every WndProc mouse-move — one synchronous full JSON rewrite
/// of settings.json per message on the UI thread. Writes now coalesce behind a short timer and
/// flush on drag release, so a drag writes at most a few times instead of once per mouse move.
/// </summary>
internal sealed class DebouncedPlacementPersister
{
    private readonly Action<string, double> _persist;
    private readonly DispatcherTimer _timer;
    private string _monitorId = string.Empty;
    private double _edgeOffsetDip;

    public DebouncedPlacementPersister(Action<string, double> persist, TimeSpan interval)
    {
        _persist = persist;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => Flush();
    }

    public void Request(string monitorId, double edgeOffsetDip)
    {
        _monitorId = monitorId;
        _edgeOffsetDip = edgeOffsetDip;
        _timer.Stop();
        _timer.Start();
    }

    public void Flush()
    {
        _timer.Stop();
        if (_monitorId.Length == 0) return;
        var monitorId = _monitorId;
        var edgeOffsetDip = _edgeOffsetDip;
        _monitorId = string.Empty;
        _persist(monitorId, edgeOffsetDip);
    }
}

public sealed class TaskbarStateChangedEventArgs(bool attached, string message) : EventArgs
{
    public bool Attached { get; } = attached;
    public string Message { get; } = message;
}

internal sealed class NativeTaskbarOverlay : IDisposable
{
    internal const string WindowTitle = "TokenBurn status strip";
    internal const string OverlayMarker = "UsageMonitor.TaskbarOverlay.Native";
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmSetCursor = 0x0020;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseMove = 0x0200;
    private const uint WmCaptureChanged = 0x0215;
    private const uint WmDestroy = 0x0002;
    private const uint HtClient = 1;
    private const uint MaNoActivate = 3;
    private const int IccArrow = 32512;
    private const byte HitTestAlpha = 1;
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoSendChanging = 0x0400;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const int BmiRgb = 0;
    private const int DragThreshold = 4;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly object ClassGate = new();
    private static readonly WndProcDelegate WndProc = WindowProc;
    private static string? _className;
    private static bool _classRegistered;
    [ThreadStatic] private static NativeTaskbarOverlay? Creating;

    private readonly Action<System.Drawing.Point> _click;
    private readonly Action<System.Drawing.Point> _dragStart;
    private readonly Action<WidgetDragDeltaEventArgs> _drag;
    private readonly Action _dragEnd;
    private NativeMethods.POINT _down;
    private NativeMethods.POINT _last;
    private bool _capture;
    private bool _dragging;
    private bool _locked = true;
    private bool _visible;
    private bool _suppressNextClick;
    private IntPtr _bitmap;
    private IntPtr _bitmapDc;
    private IntPtr _oldBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _bitmapAlphaPixels;
    private byte _bitmapMaxAlpha;
    private int _bitmapNonZeroBytes;
    private string? _bitmapFirstBytes;
    private GCHandle _selfHandle;

    public NativeTaskbarOverlay(
        Action<System.Drawing.Point> click,
        Action<System.Drawing.Point> dragStart,
        Action<WidgetDragDeltaEventArgs> drag,
        Action dragEnd)
    {
        _click = click;
        _dragStart = dragStart;
        _drag = drag;
        _dragEnd = dragEnd;
        Handle = Create();
        _selfHandle = GCHandle.Alloc(this);
        SetWindowLongPtr(Handle, -21, GCHandle.ToIntPtr(_selfHandle));
        SetWindowText(Handle, WindowTitle);
        NativeMethods.SetProp(Handle, OverlayMarker, new IntPtr(1));
    }

    public IntPtr Handle { get; }
    public bool IsVisible => _visible;
    public bool HasBitmap => _bitmap != IntPtr.Zero;
    public int BitmapAlphaPixels => _bitmapAlphaPixels;
    public byte BitmapMaxAlpha => _bitmapMaxAlpha;
    public int BitmapNonZeroBytes => _bitmapNonZeroBytes;
    public string? BitmapFirstBytes => _bitmapFirstBytes;
    public int BitmapWidth => _bitmapWidth;
    public int BitmapHeight => _bitmapHeight;

    public void SetPositionLocked(bool locked)
    {
        _locked = locked;
        if (locked && _capture)
        {
            _capture = false;
            if (_dragging)
            {
                _dragging = false;
                // The last dragged position must not be lost when the lock interrupts the drag.
                _dragEnd();
            }
            // The user may still be holding the button down right now (lock toggled mid-drag).
            // Swallow the following mouse-up so releasing does not read as a click that toggles
            // the popup and leaves the strip looking "bugged out".
            _suppressNextClick = true;
            ReleaseCapture();
        }
    }

    public void SetScreenShareExcluded(bool excluded)
    {
        if (Handle == IntPtr.Zero) return;
        _ = SetWindowDisplayAffinity(Handle, excluded ? WdaExcludeFromCapture : WdaNone);
    }

    public void SetBitmap(BitmapSource source, int targetWidth, int targetHeight)
    {
        var converted = source.Format == System.Windows.Media.PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        var sourceWidth = converted.PixelWidth;
        var sourceHeight = converted.PixelHeight;
        var width = Math.Max(sourceWidth, targetWidth);
        var height = Math.Max(sourceHeight, targetHeight);
        var sourcePixels = new byte[sourceWidth * sourceHeight * 4];
        converted.CopyPixels(sourcePixels, sourceWidth * 4, 0);
        var pixels = new byte[width * height * 4];
        var contentTop = Math.Max(0, (height - sourceHeight) / 2);
        for (var row = 0; row < sourceHeight && row + contentTop < height; row++)
        {
            var sourceOffset = row * sourceWidth * 4;
            var targetOffset = (row + contentTop) * width * 4;
            Buffer.BlockCopy(sourcePixels, sourceOffset, pixels, targetOffset,
                Math.Min(sourceWidth, width) * 4);
        }
        // Layered windows pass fully transparent pixels through to the window underneath before
        // WM_NCHITTEST runs. Keep the strip visually transparent, but give every pixel a tiny
        // alpha value so the whole padded rectangle is a dependable hitbox.
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] == 0) pixels[index] = HitTestAlpha;
        }
        _bitmapAlphaPixels = 0;
        _bitmapMaxAlpha = 0;
        _bitmapNonZeroBytes = pixels.Count(value => value > 0);
        _bitmapFirstBytes = string.Join(",", pixels.Take(8));
        for (var index = 3; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index];
            if (alpha > 0) _bitmapAlphaPixels++;
            if (alpha > _bitmapMaxAlpha) _bitmapMaxAlpha = alpha;
        }
        ClearBitmap();
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BmiRgb
            }
        };
        _bitmapDc = CreateCompatibleDC(IntPtr.Zero);
        _bitmap = CreateDibSection(_bitmapDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        _oldBitmap = SelectObject(_bitmapDc, _bitmap);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        _bitmapWidth = width;
        _bitmapHeight = height;
    }

    public bool SetPosition(System.Drawing.Rectangle bounds, bool promote, bool repaint)
    {
        if (Handle == IntPtr.Zero) return false;
        // The composited window must never be narrower/shorter than the bitmap actually painted
        // into it (SetBitmap already grows the DIB to fit its source content). Sizing the window to
        // a stale/undershot placement while blitting from (0,0) silently crops the trailing content
        // (e.g. the last provider's value) instead of showing it.
        var width = Math.Max(bounds.Width, _bitmapWidth);
        var height = Math.Max(bounds.Height, _bitmapHeight);
        var x = bounds.Right - width;
        var y = bounds.Top;
        var presented = false;
        if (repaint && _bitmapWidth > 0 && _bitmapHeight > 0)
        {
            var destination = new NativePoint { X = x, Y = y };
            var size = new NativeSize { Width = width, Height = height };
            var source = new NativePoint();
            var blend = new BlendFunction { Operation = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            presented = UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, _bitmapDc, ref source, 0, ref blend, UlwAlpha);
        }
        var insertAfter = promote ? HwndTopmost : IntPtr.Zero;
        var flags = SwpNoActivate | SwpNoSendChanging | SwpShowWindow;
        if (!promote) flags |= SwpNoZOrder;
        _ = SetWindowPos(Handle, insertAfter, x, y, width, height, flags);
        if (promote)
        {
            // HWND_TOPMOST establishes the topmost band. HWND_TOP then places this window at the
            // front of that band. Explorer can otherwise leave a previously-created taskbar
            // surface ahead of us even though the first SetWindowPos call succeeds.
            _ = SetWindowPos(Handle, HwndTop, 0, 0, 0, 0,
                SwpNoActivate | SwpNoSendChanging | SwpNoMove | SwpNoSize);
        }
        _ = ShowWindow(Handle, SwShownNoActivate);
        _visible = NativeMethods.IsWindowVisible(Handle) && (presented || _bitmap != IntPtr.Zero);
        return _visible;
    }

    public void Hide()
    {
        if (Handle != IntPtr.Zero) ShowWindow(Handle, 0);
        _visible = false;
    }

    public void Destroy()
    {
        if (Handle == IntPtr.Zero) return;
        _visible = false;
        RemoveProp(Handle, OverlayMarker);
        DestroyWindow(Handle);
        ClearBitmap();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private IntPtr Create()
    {
        lock (ClassGate)
        {
            _className ??= $"UsageMonitor.NativeOverlay.{Environment.ProcessId}";
            if (!_classRegistered)
            {
                var windowClass = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(),
                    WindowProc = WndProc,
                    Instance = GetModuleHandle(null),
                    Cursor = LoadCursor(IntPtr.Zero, new IntPtr(IccArrow)),
                    ClassName = _className
                };
                if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
                    throw new Win32Exception();
                _classRegistered = true;
            }
        }
        Creating = this;
        var handle = CreateWindowEx(
            WsExToolWindow | WsExLayered | WsExNoActivate,
            _className!, WindowTitle, WsPopup, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        Creating = null;
        if (handle == IntPtr.Zero) throw new Win32Exception();
        return handle;
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmNcHitTest)
        {
            // The bitmap is transparent around the glyphs, but the native overlay is still a
            // control. Claim the complete padded rectangle so users do not have to aim at a logo
            // or text pixel to click the strip.
            return new IntPtr((long)HtClient);
        }
        if (message == WmSetCursor)
        {
            SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IccArrow)));
            return new IntPtr(1);
        }
        if (message == WmMouseActivate) return new IntPtr((long)MaNoActivate);
        if (message == WmLButtonDown)
        {
            _down = GetCursor();
            _last = _down;
            _dragging = false;
            _suppressNextClick = false;
            if (!_locked)
            {
                _dragStart(new System.Drawing.Point(_down.X, _down.Y));
                SetCapture(Handle);
                _capture = true;
            }
            return IntPtr.Zero;
        }
        if (message == WmMouseMove && _capture && !_locked)
        {
            var point = GetCursor();
            if (!_dragging)
            {
                if (Math.Abs(point.X - _down.X) < DragThreshold && Math.Abs(point.Y - _down.Y) < DragThreshold)
                    return IntPtr.Zero;
                _dragging = true;
            }
            var dpi = GetDpiForWindow(Handle);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            _drag(new WidgetDragDeltaEventArgs(
                (point.X - _last.X) / scale,
                (point.Y - _last.Y) / scale,
                new System.Drawing.Point(point.X, point.Y)));
            _last = point;
            return IntPtr.Zero;
        }
        if (message == WmLButtonUp)
        {
            if (_capture)
            {
                _capture = false;
                ReleaseCapture();
            }
            if (_suppressNextClick)
            {
                _suppressNextClick = false;
                return IntPtr.Zero;
            }
            if (_dragging)
            {
                _dragging = false;
                _dragEnd();
            }
            else
            {
                var point = GetCursor();
                _click(new System.Drawing.Point(point.X, point.Y));
            }
            return IntPtr.Zero;
        }
        if (message == WmCaptureChanged)
        {
            _capture = false;
            if (_dragging)
            {
                _dragging = false;
                _dragEnd();
            }
            return IntPtr.Zero;
        }
        if (message == WmDestroy) _visible = false;
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static NativeMethods.POINT GetCursor()
    {
        NativeMethods.GetCursorPos(out var point);
        return point;
    }

    private void ClearBitmap()
    {
        if (_bitmapDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) SelectObject(_bitmapDc, _oldBitmap);
            DeleteDC(_bitmapDc);
        }
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        _bitmap = IntPtr.Zero;
        _bitmapDc = IntPtr.Zero;
        _oldBitmap = IntPtr.Zero;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var target = GetTarget(hwnd);
        return target?.OnMessage(hwnd, message, wParam, lParam) ?? DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static NativeTaskbarOverlay? GetTarget(IntPtr hwnd)
    {
        var value = GetWindowLongPtr(hwnd, -21);
        if (value != IntPtr.Zero)
        {
            try { return (NativeTaskbarOverlay)GCHandle.FromIntPtr(value).Target!; } catch { }
        }
        if (Creating is not null) return Creating;
        return null;
    }

    public void Dispose() => Destroy();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WndProcDelegate WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        // Keep the WNDCLASSEX layout intact, but leave both icon handles zero. The quota strip
        // is a tool window and must not create a second TokenBurn shell identity.
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte Operation, Flags, SourceConstantAlpha, AlphaFormat; }
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW", SetLastError = true)] private static extern bool SetWindowText(IntPtr hwnd, string text);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, int colorKey, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)] private static extern IntPtr CreateDibSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr objectHandle);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetProp(IntPtr hwnd, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr hwnd, string name);

    private const int SwShownNoActivate = 4;
    private const uint SwpNoZOrder = 0x0004;
}
