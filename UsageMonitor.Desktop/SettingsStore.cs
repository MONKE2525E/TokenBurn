using System.Text.Json;
using System.IO;
using System.Linq;
using UsageMonitor.Core;

namespace UsageMonitor.Desktop;

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageMonitor");

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return UserSettings.Default;
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), JsonOptions);
            if (settings is null) return UserSettings.Default;
            settings.SelectedMonitor ??= MonitorPlacementService.PrimaryMonitorId;
            // Older builds exposed several mutually exclusive surfaces. The Windows build now
            // keeps the taskbar text strip (macOS-menu-bar style, shows live quota values inline)
            // plus the tray icon as the single predictable status surface; a failed embed falls
            // back to the tray icon internally without leaving a stale user-selected mode.
            settings.StatusSurface = StatusSurfaceMode.TaskbarWidget;
            if (settings.StarredMetrics is not { Count: > 0 })
                settings.StarredMetrics = ["Claude Code Session", "Claude Code Weekly", "Codex Weekly", "Antigravity"];
            // Antigravity shipped as a default-enabled provider (it is not in the default
            // DisabledProviders list), but the original pinned-metrics default never included it,
            // so it silently never appeared on the compact taskbar/tray surface. Only touch an
            // untouched legacy default, never a list the user has customized.
            var legacyDefault = new[] { "Codex Session", "Codex Weekly", "Claude Code Session", "Claude Code Weekly" };
            if (settings.StarredMetrics.Count == legacyDefault.Length &&
                settings.StarredMetrics.Zip(legacyDefault, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).All(match => match))
            {
                settings.StarredMetrics = ["Claude Code Session", "Claude Code Weekly", "Codex Weekly", "Antigravity"];
            }
            // Missing migration metadata must never overwrite an explicit old selection. In
            // particular, an older settings file with an empty disabled list means the user
            // intentionally enabled every provider.
            settings.ProviderSelectionInitialized = true;
            SettingsMigration.Normalize(settings);
            settings.WidgetPlacements ??= new(StringComparer.OrdinalIgnoreCase);
            Save(settings);
            return settings;
        }
        catch
        {
            return UserSettings.Default;
        }
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            SettingsMigration.Normalize(settings);
            Directory.CreateDirectory(DirectoryPath);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // Settings are a convenience. A read-only profile must not prevent the app from starting.
        }
    }

}
