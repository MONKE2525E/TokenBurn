using Microsoft.Win32;

namespace UsageMonitor.Desktop;

internal static class StartupManager
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "UsageMonitor";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                    key?.SetValue(ValueName, $"\"{exe}\" --startup");
            }
            else
            {
                key?.DeleteValue(ValueName, false);
            }
        }
        catch
        {
            // Group policy or a locked profile can deny this optional preference.
        }
    }
}
