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

    /// <summary>Internal test seam. When set, settings files are read from and written to this directory.</summary>
    internal static string? DirectoryOverride;

    /// <summary>True when the last Load fell back to defaults because the settings file was unreadable.</summary>
    internal static bool LastLoadFailed { get; private set; }

    /// <summary>Resets the failed-load flag between tests.</summary>
    internal static void ResetForTests() => LastLoadFailed = false;

    private static string DirectoryPath => DirectoryOverride ?? UsageMonitorPaths.Current.RoamingDataRoot;

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return UserSettings.Default;
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), JsonOptions);
            if (settings is null)
            {
                // A valid JSON document that is not a settings object is treated as corruption,
                // not as a reset. Preserve the file so the user's previous values can be recovered.
                PreserveUnreadableFile("empty settings document");
                LastLoadFailed = true;
                return UserSettings.Default;
            }
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
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Corrupt settings are a real data problem, not a soft migration. Keep the file for
            // recovery and tell the user at startup instead of silently resetting their choices.
            PreserveUnreadableFile(ex.GetType().Name);
            LastLoadFailed = true;
            FileDiagnosticsLogger.Default.Warning("Settings could not be read; defaults are in use", exception: ex);
            return UserSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transient lock or permission problem. Leave the file in place so the next launch
            // can retry it, and only surface the fallback once.
            LastLoadFailed = true;
            FileDiagnosticsLogger.Default.Warning("Settings could not be read; defaults are in use", exception: ex);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience. A read-only profile must not prevent the app from
            // starting, but the user must be able to learn their changes did not persist.
            FileDiagnosticsLogger.Default.Warning("Settings could not be saved", exception: ex);
        }
    }

    private static void PreserveUnreadableFile(string reason)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var backup = FilePath + ".corrupt";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(FilePath, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileDiagnosticsLogger.Default.Warning("Unreadable settings file could not be preserved", exception: ex);
        }
    }
}
