using System.Runtime.InteropServices;

namespace UsageMonitor.Desktop;

internal static class NativeMethods
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    internal const string ActivationHostMarker = "UsageMonitor.ActivationHost";
    internal const string LegacyTaskbarOverlayMarker = "UsageMonitor.TaskbarOverlay";
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    internal delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hWnd, int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    internal static bool TryGetTaskbarTrayNotifyBounds(IntPtr taskbar, out RECT bounds)
    {
        bounds = default;
        if (taskbar == IntPtr.Zero) return false;
        var trayNotify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        return trayNotify != IntPtr.Zero && GetWindowRect(trayNotify, out bounds) &&
               bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static void SetWindowNoActivate(IntPtr hWnd)
    {
        var style = GetWindowLong(hWnd, GwlExStyle);
        SetWindowLong(hWnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);

    internal static IntPtr FindTopLevelWindowForProcess(int processId)
    {
        var fallback = IntPtr.Zero;
        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != (uint)processId) return true;
            if (GetProp(hWnd, WidgetWindow.OverlayMarker) != IntPtr.Zero ||
                GetProp(hWnd, TaskbarOverlayController.NativeOverlayMarker) != IntPtr.Zero ||
                GetProp(hWnd, LegacyTaskbarOverlayMarker) != IntPtr.Zero) return true;
            if (GetProp(hWnd, ActivationHostMarker) != IntPtr.Zero)
            {
                found = hWnd;
                return false;
            }
            fallback = hWnd;
            return true;
        }, IntPtr.Zero);
        return found != IntPtr.Zero ? found : fallback;
    }

    internal static void CloseStaleWindows(string title, int currentProcessId)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var stale = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner == (uint)currentProcessId) return true;
            var length = GetWindowTextLength(hWnd);
            if (length <= 0) return true;
            var text = new System.Text.StringBuilder(length + 1);
            _ = GetWindowText(hWnd, text, text.Capacity);
            if (string.Equals(text.ToString(), title, StringComparison.Ordinal)) stale.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (var hWnd in stale)
        {
            RemoveFromTaskbar(hWnd);
            PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    internal static void CloseStaleOverlayWindows(string title, string marker, int currentProcessId)
    {
        var stale = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner == (uint)currentProcessId) return true;

            var marked = GetProp(hWnd, marker) != IntPtr.Zero;
            var length = GetWindowTextLength(hWnd);
            var titled = false;
            if (length > 0)
            {
                var text = new System.Text.StringBuilder(length + 1);
                _ = GetWindowText(hWnd, text, text.Capacity);
                titled = string.Equals(text.ToString(), title, StringComparison.Ordinal);
            }
            if (marked || titled) stale.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (var hWnd in stale)
        {
            RemoveFromTaskbar(hWnd);
            PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    internal static bool IsWindowAbove(IntPtr first, IntPtr second)
    {
        if (first == IntPtr.Zero || second == IntPtr.Zero) return false;
        for (var current = GetTopWindow(IntPtr.Zero); current != IntPtr.Zero; current = GetWindow(current, GwHwndNext))
        {
            if (current == first) return true;
            if (current == second) return false;
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    internal static void RemoveFromTaskbar(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            var taskbar = (ITaskbarList)Activator.CreateInstance(typeof(CTaskbarList))!;
            _ = taskbar.HrInit();
            _ = taskbar.DeleteTab(hWnd);
            Marshal.ReleaseComObject(taskbar);
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        catch (Exception) { }
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        int HrInit();
        int AddTab(IntPtr hWnd);
        int DeleteTab(IntPtr hWnd);
        int ActivateTab(IntPtr hWnd);
        int SetActiveAlt(IntPtr hWnd);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private sealed class CTaskbarList { }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_APPWINDOW = 0x00040000;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const uint WDA_NONE = 0x00000000;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;
    internal const uint WmClose = 0x0010;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_NOSENDCHANGING = 0x0400;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint GwHwndNext = 2;
    internal const uint GwHwndPrev = 3;
    internal const uint WinEventOutOfContext = 0;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemMoveSizeEnd = 0x000A;
    internal const uint EventSystemDesktopSwitch = 0x0020;
    internal const uint EventObjectShow = 0x8002;
    internal const uint EventObjectHide = 0x8003;
    internal const uint EventObjectLocationChange = 0x800B;
}
