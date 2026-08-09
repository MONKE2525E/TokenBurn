using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsageMonitor.Desktop;

internal enum TokenBurnIconRole
{
    AppIdentity,
    Tray,
    Notification
}

/// <summary>
/// Loads the role-specific TokenBurn icons from the desktop assembly. Shell surfaces must not
/// depend on the current directory or on copied files beside a single-file publish.
/// </summary>
internal static class TokenBurnIconResources
{
    private const string AppPng = "TokenBurn.Icons.App.png";
    private const string AppIco = "TokenBurn.Icons.App.ico";
    private const string TrayPng = "TokenBurn.Icons.Tray.png";
    private const string TrayIco = "TokenBurn.Icons.Tray.ico";
    private static readonly Assembly ResourceAssembly = typeof(TokenBurnIconResources).Assembly;

    public static ImageSource LoadWpfAppIcon() => LoadWpfBitmap(TokenBurnIconRole.AppIdentity);

    public static Icon LoadAppIcon() => LoadIcon(TokenBurnIconRole.AppIdentity);

    public static Icon LoadTrayIcon() => LoadIcon(TokenBurnIconRole.Tray);

    public static BitmapSource LoadTrayMenuIcon() => LoadWpfBitmap(TokenBurnIconRole.Tray);

    public static BitmapSource LoadNotificationIcon() => LoadWpfBitmap(TokenBurnIconRole.Notification);

    private static BitmapSource LoadWpfBitmap(TokenBurnIconRole role)
    {
        using var stream = OpenResource(role, bitmap: true);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Icon LoadIcon(TokenBurnIconRole role)
    {
        using var stream = OpenResource(role, bitmap: false);
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    private static Stream OpenResource(TokenBurnIconRole role, bool bitmap)
    {
        var resourceName = role switch
        {
            TokenBurnIconRole.AppIdentity when bitmap => AppPng,
            TokenBurnIconRole.AppIdentity => AppIco,
            TokenBurnIconRole.Notification => TrayPng,
            TokenBurnIconRole.Tray when bitmap => TrayPng,
            TokenBurnIconRole.Tray => TrayIco,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown TokenBurn icon role.")
        };

        var stream = ResourceAssembly.GetManifestResourceStream(resourceName);
        if (stream is not null) return stream;

        var available = string.Join(", ", ResourceAssembly.GetManifestResourceNames());
        throw new InvalidOperationException(
            $"TokenBurn icon resource is missing for role '{role}'. Expected '{resourceName}'. " +
            $"Embedded resources: {available}");
    }
}
