using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsageMonitor.Desktop;

public sealed record MonitorOption(string Id, string DisplayName, bool IsPrimary)
{
    public override string ToString() => DisplayName;
}

public sealed class MonitorPlacementService
{
    public const string PrimaryMonitorId = "PRIMARY";

    public IReadOnlyList<MonitorOption> GetMonitors()
    {
        var screens = Screen.AllScreens;
        var result = new List<MonitorOption>(screens.Length + 1);
        foreach (var screen in screens)
        {
            var id = screen.Primary ? PrimaryMonitorId : screen.DeviceName;
            var label = screen.Primary
                ? $"Primary display ({screen.Bounds.Width} x {screen.Bounds.Height})"
                : $"{screen.DeviceName.TrimStart('\\', '.') } ({screen.Bounds.Width} x {screen.Bounds.Height})";
            result.Add(new MonitorOption(id, label, screen.Primary));
        }
        return result;
    }

    public string GetMonitorId(Screen screen) => screen.Primary ? PrimaryMonitorId : screen.DeviceName;

    public Screen ResolveScreen(string? id)
    {
        var screens = Screen.AllScreens;
        if (string.IsNullOrWhiteSpace(id) || id.Equals(PrimaryMonitorId, StringComparison.OrdinalIgnoreCase))
            return Screen.PrimaryScreen ?? screens.First();
        return screens.FirstOrDefault(s => s.DeviceName.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? Screen.PrimaryScreen
            ?? screens.First();
    }

    public IntPtr GetTaskbarHandle(Screen screen)
    {
        var primary = screen.Primary;
        var hwnd = primary
            ? NativeMethods.FindWindow("Shell_TrayWnd", null)
            : FindSecondaryTaskbar(screen);
        if (hwnd != IntPtr.Zero) return hwnd;

        // Explorer can briefly expose the secondary taskbar under the primary class name.
        var candidate = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        while (candidate != IntPtr.Zero)
        {
            if (IsOnScreen(candidate, screen)) return candidate;
            candidate = NativeMethods.FindWindowEx(IntPtr.Zero, candidate, "Shell_TrayWnd", null);
        }
        return IntPtr.Zero;
    }

    public bool IsOnScreen(IntPtr hwnd, Screen screen)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        var center = new System.Drawing.Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        return screen.Bounds.Contains(center);
    }

    public System.Drawing.Rectangle GetTaskbarBounds(IntPtr hwnd)
    {
        return NativeMethods.GetWindowRect(hwnd, out var rect)
            ? new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : System.Drawing.Rectangle.Empty;
    }

    public Screen? GetMonitorAtTaskbarPoint(System.Drawing.Point point)
    {
        foreach (var screen in Screen.AllScreens)
        {
            var taskbar = GetTaskbarHandle(screen);
            if (taskbar == IntPtr.Zero || !NativeMethods.GetWindowRect(taskbar, out var rect)) continue;
            if (new System.Drawing.Rectangle(rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top).Contains(point))
                return screen;
        }

        return null;
    }

    private static IntPtr FindSecondaryTaskbar(Screen screen)
    {
        var hwnd = NativeMethods.FindWindow("Shell_SecondaryTrayWnd", null);
        while (hwnd != IntPtr.Zero)
        {
            if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                var center = new System.Drawing.Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                if (screen.Bounds.Contains(center)) return hwnd;
            }
            hwnd = NativeMethods.FindWindowEx(IntPtr.Zero, hwnd, "Shell_SecondaryTrayWnd", null);
        }
        return IntPtr.Zero;
    }
}
