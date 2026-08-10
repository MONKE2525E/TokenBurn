using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows.Shell;
using System.Windows.Interop;
using System.Windows.Threading;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Desktop;

public partial class App : System.Windows.Application
{
    private static readonly uint ActivationMessage = NativeMethods.RegisterWindowMessage("TokenBurn.Activate");
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private WindowsAppNotificationService? _appNotifications;
    private TaskbarOverlayController? _taskbar;
    private TauriPopupBridge? _tauriPopup;
    private UsageApiHost? _apiHost;
    private UserSettings _settings = UserSettings.Default;

    public static App CurrentApp => (App)Current;
    public UserSettings Settings => _settings;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Give Explorer a stable identity for the taskbar button and jump list. Without an explicit
        // AppUserModelID, self-contained builds can be grouped under a transient path identity and
        // the button may disappear or launch a second unassociated instance after an update.
        // Windows uses the AppUserModelID as the notification source label. A raw implementation
        // identifier made toasts say "TokenBurn.Windows" instead of the product's actual name.
        try { NativeMethods.SetCurrentProcessExplicitAppUserModelID("TokenBurn"); } catch { }

        _instanceMutex = new Mutex(true, "Global\\TokenBurn.Windows.SingleInstance", out var isNew);
        _ownsInstanceMutex = isNew;
        if (!isNew)
        {
            // A second launch is usually the user clicking the Start menu entry
            // while the login instance is minimized.  Exiting silently makes the
            // product feel dead, so restore and foreground the already-running
            // dashboard before this short-lived process exits.
            ActivateExistingInstance(e.Args);
            Shutdown(0);
            return;
        }

        NativeMethods.CloseStaleWindows("TokenBurn", Environment.ProcessId);
        NativeMethods.CloseStaleWindows("TokenBurn status strip", Environment.ProcessId);
        _settings = SettingsStore.Load();
        // The WPF window is now a headless integration host only. Keep the compact Tauri popup
        // as the sole dashboard even when an old settings file had opted into the legacy view.
        _settings.UseTauriPopup = true;
        // App notification activation is attached before the manager registers, as required by
        // Windows App SDK, so a notification click returns to this shell process.
        _appNotifications = new WindowsAppNotificationService(Dispatcher,
            () => _mainWindow?.ShowFromTray());
        _appNotifications.Register();
        StartupManager.SetEnabled(_settings.StartAtLogin);
        var launchedAtLogin = e.Args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        _mainWindow = new MainWindow();
        _mainWindow.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_mainWindow).Handle;
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetProp(hwnd, NativeMethods.ActivationHostMarker, new IntPtr(1));
            if (HwndSource.FromHwnd(hwnd) is { } source)
                source.AddHook(ActivationHook);
        };
        _mainWindow.Closed += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_mainWindow).Handle;
            if (hwnd != IntPtr.Zero)
                NativeMethods.RemoveProp(hwnd, NativeMethods.ActivationHostMarker);
        };
        _tauriPopup = new TauriPopupBridge(() =>
            _mainWindow.Dispatcher.BeginInvoke(new Action(_mainWindow.ShowSettings),
                System.Windows.Threading.DispatcherPriority.Normal),
            () => _mainWindow.Dispatcher.BeginInvoke(new Action(_mainWindow.ShowCustomize),
                System.Windows.Threading.DispatcherPriority.Normal),
            () => _mainWindow.GetEnabledProviderIds(),
            () => new TauriPopupBridge.RefreshStatus(_mainWindow.NextRefreshAt, _mainWindow.IsRefreshInFlight),
            () => _mainWindow.Dispatcher.InvokeAsync(() => _mainWindow.RefreshDataAsync(false, "popup-refresh")).Task.Unwrap(),
            () => _mainWindow.Dispatcher.Invoke(() => _mainWindow.GetSettingsPageDataJson()),
            json => _mainWindow.Dispatcher.Invoke(() => _mainWindow.ApplySettingsPageDataJson(json)),
            () => _mainWindow.Dispatcher.Invoke(() => _mainWindow.BuildDiagnosticsBundleJson()),
            metric => _mainWindow.Dispatcher.Invoke(() => _mainWindow.ApplySpendMetric(metric)),
            visible => _taskbar?.SetPopupVisible(visible));
        _tray = new TrayIconService(this, _mainWindow, _appNotifications);
        _taskbar = new TaskbarOverlayController(_mainWindow);
        _tray.AttachTaskbar(_taskbar);

        _mainWindow.Initialize(_settings, _taskbar, _tray, _tauriPopup);
        // Keep the popup and WebView2 warm. Taskbar clicks are the primary interaction and must
        // never pay a cold-start penalty after the app has been idle for a while.
        _tauriPopup.StartHosted();
        if (_mainWindow.SnapshotCatalog is { } catalog)
        {
            try
            {
                // The embedded Tauri webview can recover directly from the loopback API when its
                // command bridge is still starting.  The service remains loopback-only and only
                // exposes the same non-secret usage snapshot already shown in the taskbar.
                _apiHost = UsageApiHost.Create(catalog, _mainWindow.SnapshotCache,
                    new UsageApiOptions { EnableCors = true });
                _ = StartApiAsync(_apiHost);
            }
            catch
            {
                // A port collision must not prevent the desktop status surfaces from starting.
            }
        }
        _tray.Initialize();
        // The taskbar text strip is the primary, macOS-menu-bar-style surface: it shows live
        // quota values directly in the taskbar, which the tray icon alone cannot do. If Explorer
        // ever rejects embedding (or a shell restart knocks it loose mid-session), TryAttach's own
        // failure path hides the widget cleanly rather than showing a disconnected floating box,
        // and the watchdog keeps retrying so it recovers on its own once Explorer is stable. The
        // tray icon is demoted to a plain mark whenever the widget is actually attached, so the
        // two surfaces never show quota data at the same time.
        _settings.StatusSurface = StatusSurfaceMode.TaskbarWidget;
        _taskbar.TryAttach(_settings.SelectedMonitor);

        ConfigureTaskbarJumpList();

        var openedFromTaskbarTask = e.Args.Any(arg => string.Equals(arg, "--open", StringComparison.OrdinalIgnoreCase));
        var openedSettingsTask = e.Args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase));
        // Keep WPF alive as the shell integration host, but never let it become a second
        // dashboard. Taskbar and jump-list entry points are forwarded into the Tauri popup.
        _mainWindow.Show();
        _mainWindow.Hide();
        if (openedSettingsTask)
            CurrentApp.Dispatcher.BeginInvoke(() => _mainWindow.ShowSettingsPage(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        else if (openedFromTaskbarTask || !launchedAtLogin)
            CurrentApp.Dispatcher.BeginInvoke(() => _mainWindow.ShowFromTray(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public void SaveSettings(UserSettings settings)
    {
        settings.UseTauriPopup = true;
        var monitorChanged = !string.Equals(_settings.SelectedMonitor, settings.SelectedMonitor, StringComparison.OrdinalIgnoreCase);
        settings.StatusSurface = StatusSurfaceMode.TaskbarWidget;
        _settings = settings;
        SettingsStore.Save(settings);
        StartupManager.SetEnabled(settings.StartAtLogin);
        _mainWindow?.ApplySettings(settings);
        if (monitorChanged || _taskbar?.IsAttached != true)
            _taskbar?.TryAttach(settings.SelectedMonitor);
    }

    /// <summary>
    /// Persists a placement fallback without re-entering the taskbar overlay attach path.
    /// A disconnected display is expected state for a laptop dock, so this is deliberately
    /// a quiet settings migration rather than an error path.
    /// </summary>
    internal void PersistMonitorFallback(string monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId) ||
            _settings.SelectedMonitor.Equals(monitorId, StringComparison.OrdinalIgnoreCase))
            return;

        _settings.SelectedMonitor = monitorId;
        SettingsStore.Save(_settings);
        _mainWindow?.ApplySettings(_settings);
        _tray?.RefreshMonitorMenu();
    }

    internal void PersistMonitorSelection(string monitorId)
        => PersistMonitorFallback(monitorId);

    internal void PersistWidgetPlacement(string monitorId, double edgeOffsetDip)
    {
        _settings.SetWidgetPlacement(monitorId, edgeOffsetDip);
        SettingsStore.Save(_settings);
        _mainWindow?.ApplySettings(_settings);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _taskbar?.Dispose();
        _tray?.Dispose();
        _appNotifications?.Dispose();
        _tauriPopup?.Dispose();
        if (_apiHost is not null)
        {
            try { _apiHost.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        if (_ownsInstanceMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static async Task StartApiAsync(UsageApiHost host)
    {
        try { await host.StartAsync().ConfigureAwait(true); }
        catch { }
    }

    private static void ConfigureTaskbarJumpList()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            var jumpList = new JumpList
            {
                ShowRecentCategory = false,
                ShowFrequentCategory = false
            };
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = "Open dashboard",
                Description = "Show TokenBurn",
                ApplicationPath = executable,
                Arguments = "--open"
            });
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = "Settings",
                Description = "Choose taskbar surface and providers",
                ApplicationPath = executable,
                Arguments = "--settings"
            });
            JumpList.SetJumpList(CurrentApp, jumpList);
        }
        catch
        {
            // Jump lists are optional Explorer chrome. The taskbar button and tray must still work
            // if policy or an older shell declines the registration.
        }
    }

    private static IntPtr ActivationHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message != ActivationMessage) return IntPtr.Zero;
        var action = wParam.ToInt32();
        CurrentApp.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (action == 2)
                CurrentApp._mainWindow?.ShowSettings();
            else
                CurrentApp._mainWindow?.ShowFromTray();
        }), DispatcherPriority.Normal);
        handled = true;
        return IntPtr.Zero;
    }

    private static void ActivateExistingInstance(IReadOnlyList<string> args)
    {
        try
        {
            var currentId = Environment.ProcessId;
            var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(processName)) processName = "TokenBurn";

            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.Id == currentId) continue;
                    var hwnd = NativeMethods.FindTopLevelWindowForProcess(process.Id);
                    if (hwnd == IntPtr.Zero) continue;
                    var action = args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
                    NativeMethods.PostMessage(hwnd, ActivationMessage, new IntPtr(action), IntPtr.Zero);
                    return;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // The original instance may be between startup and HWND creation.
            // The mutex still prevents duplicate monitoring, and the tray remains
            // available once Explorer has finished creating its shell surfaces.
        }
    }
}
