namespace UsageMonitor.Core;

/// <summary>Stable per-user locations used by every process in the app.</summary>
public sealed class UsageMonitorPaths
{
    public UsageMonitorPaths(string? localDataRoot = null, string? roamingDataRoot = null)
    {
        LocalDataRoot = Normalize(localDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageMonitor"));
        RoamingDataRoot = Normalize(roamingDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageMonitor"));
    }

    public static UsageMonitorPaths Current { get; } = new();
    public string LocalDataRoot { get; }
    public string RoamingDataRoot { get; }
    public string CacheDirectory => Path.Combine(LocalDataRoot, "Cache");
    public string LogsDirectory => Path.Combine(LocalDataRoot, "Logs");
    public string DiagnosticsLogFile => Path.Combine(LogsDirectory, "usage-monitor.log");
    public string PricingDirectory => Path.Combine(LocalDataRoot, "Pricing");
    public string SettingsDirectory => RoamingDataRoot;
    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
    public string LayoutFile => Path.Combine(SettingsDirectory, "layout.json");
    public string CredentialsTargetPrefix => "UsageMonitor/";

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(LocalDataRoot);
        Directory.CreateDirectory(RoamingDataRoot);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(PricingDirectory);
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Trim());
}
