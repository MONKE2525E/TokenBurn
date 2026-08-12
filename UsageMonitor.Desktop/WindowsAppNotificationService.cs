using System.Security.Principal;
using System.IO;
using System.Windows.Threading;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using UsageMonitor.Core;

namespace UsageMonitor.Desktop;

/// <summary>
/// Owns Windows App SDK registration and sends quota alerts through the operating system's
/// notification pipeline. No WPF window or NotifyIcon balloon participates in this path.
/// </summary>
internal sealed class WindowsAppNotificationService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _openDashboard;
    private AppNotificationManager? _manager;
    private bool _registered;

    public WindowsAppNotificationService(Dispatcher dispatcher, Action openDashboard)
    {
        _dispatcher = dispatcher;
        _openDashboard = openDashboard;
    }

    public void Register()
    {
        if (IsElevated())
        {
            FileDiagnosticsLogger.Default.Warning("Windows app notifications are unavailable from an elevated process.");
            return;
        }

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                FileDiagnosticsLogger.Default.Warning(
                    "Windows app notifications are unavailable because the Windows App Runtime Singleton package is not registered.");
                return;
            }

            _manager = AppNotificationManager.Default;
            // This must happen before Register so a click routes to this running shell instance.
            _manager.NotificationInvoked += OnNotificationInvoked;
            _manager.Register();
            _registered = true;
            FileDiagnosticsLogger.Default.Info("Windows app notifications registered.");
        }
        catch (Exception exception)
        {
            _manager?.NotificationInvoked -= OnNotificationInvoked;
            _manager = null;
            FileDiagnosticsLogger.Default.Warning("Windows app notification registration failed.", exception: exception);
        }
    }

    public void ShowQuotaAlert(string message) => ShowAlert(message, "quota-alert", "quota");

    public void ShowAuthAlert(string message) => ShowAlert(message, "auth-alert", "auth");

    public void ShowFallbackAlert(string message) => ShowAlert(message, "fallback-alert", "fallback");

    private void ShowAlert(string message, string tag, string group)
    {
        if (!_registered || _manager is null) return;

        try
        {
            // Windows presents this as a large notification image on high-DPI displays. The 32px
            // tray asset turns into a blurred tile there, so keep the tray and notification assets
            // deliberately separate.
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "tokenburn-app-icon-256.png");
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open-dashboard")
                .AddText("TokenBurn")
                .AddText(message)
                .SetTag(tag)
                .SetGroup(group);
            if (File.Exists(logoPath))
                builder.SetAppLogoOverride(new Uri(logoPath, UriKind.Absolute));

            var notification = builder.BuildNotification();
            _manager.Show(notification);
        }
        catch (Exception exception)
        {
            FileDiagnosticsLogger.Default.Warning("Windows app notification could not be shown.", exception: exception);
        }
    }

    public void Dispose()
    {
        if (_manager is null) return;
        try
        {
            _manager.NotificationInvoked -= OnNotificationInvoked;
            if (_registered) _manager.Unregister();
        }
        catch (Exception exception)
        {
            FileDiagnosticsLogger.Default.Warning("Windows app notification cleanup failed.", exception: exception);
        }
        finally
        {
            _registered = false;
            _manager = null;
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("action", out var action) || action != "open-dashboard") return;
        _dispatcher.BeginInvoke(_openDashboard, DispatcherPriority.Input);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
