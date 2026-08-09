using System.Text.Json.Serialization;
using System.Windows.Media;
using UsageMonitor.Core;

namespace UsageMonitor.Desktop;

public enum StatusSurfaceMode
{
    /// <summary>Transparent top-level taskbar overlay.</summary>
    /// <remarks>Keep this first for settings JSON compatibility with 0.1.</remarks>
    TaskbarWidget = 0,
    TrayOnly = 1,
    /// <summary>Compatibility-only value from retired WPF taskbar builds.</summary>
    [Obsolete("Native taskbar button surfaces are no longer active.")]
    NativeTaskbarButton = 2
}

public sealed class UserSettings
{
    // The compact strip is the primary Windows status surface, with the tray retained as a fallback.
    public StatusSurfaceMode StatusSurface { get; set; } = StatusSurfaceMode.TaskbarWidget;
    // The Tauri popup is the compact presentation layer. If its hosted shell is missing,
    // the existing WPF dashboard remains the safe fallback.
    public bool UseTauriPopup { get; set; } = true;
    public string SelectedMonitor { get; set; } = MonitorPlacementService.PrimaryMonitorId;
    // Moving the shell surface is an intentional maintenance action. Keep it locked during
    // normal use so a click cannot accidentally turn into a drag.
    public bool TaskbarPositionLocked { get; set; } = true;
    public bool StartAtLogin { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    // Reset notifications are independent from dashboard provider visibility. Keep the full
    // catalog selected by default so a newly connected provider participates automatically.
    public List<string> NotificationProviderIds { get; set; } =
        [.. ProviderCatalog.DefaultDescriptors.Select(provider => provider.Id)];
    // Legacy fields are retained for settings-file compatibility. The popup is always compact
    // and the spend ring is always shown, so they are no longer user-facing preferences.
    public bool CompactDensity { get; set; } = true;
    public bool ShowTotalSpend { get; set; } = true;
    // Stored values are canonical provider IDs. SettingsStore migrates the display names used by
    // older releases before the settings reach the dashboard or popup.
    public List<string> DisabledProviders { get; set; } =
        [ProviderIds.Cursor, ProviderIds.Copilot, ProviderIds.Devin, ProviderIds.Grok, ProviderIds.OpenCode];
    // Older builds knew only the first three providers. This flag lets one migration preserve
    // their compact default while still exposing the complete upstream-style Customize catalog.
    public bool ProviderSelectionInitialized { get; set; } = true;
    public string UsageDisplay { get; set; } = "Used";
    public string ResetTimeDisplay { get; set; } = "Countdown";
    // "system" follows Windows accessibility, while "full" and "reduced" are explicit
    // per-app overrides. This lets someone keep global desktop effects disabled while still
    // opting into TokenBurn's short popup and dashboard transitions.
    public string MotionPreference { get; set; } = "system";
    // Compact spend-chart metric selected in the Tauri popup or native dashboard.
    public string SpendMetric { get; set; } = "cost";
    // Legacy threshold fields remain for settings-file compatibility. Reset notifications use
    // NotificationsEnabled and NotificationProviderIds instead.
    public bool AlmostOutAlerts { get; set; } = true;
    public bool CuttingItCloseAlerts { get; set; }
    public bool WillRunOutAlerts { get; set; }
    public string NotificationTrigger { get; set; } = "threshold5";
    public bool HideFromScreenShare { get; set; }
    public List<string> StarredMetrics { get; set; } =
        ["claude-code:session", "claude-code:weekly", "codex:weekly", "antigravity:session"];

    // Keyed by the Windows display device identity (for example \\.\DISPLAY2),
    // not by the current monitor order. The offset is in device-independent
    // pixels so moving the widget between 100% and 200% DPI displays does not
    // make it jump to a different edge position.
    public Dictionary<string, TaskbarWidgetPlacement> WidgetPlacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public static UserSettings Default => new();

    public UserSettings Clone() => new()
    {
        StatusSurface = StatusSurface,
        UseTauriPopup = UseTauriPopup,
        SelectedMonitor = SelectedMonitor,
        TaskbarPositionLocked = TaskbarPositionLocked,
        StartAtLogin = StartAtLogin,
        NotificationsEnabled = NotificationsEnabled,
        NotificationProviderIds = [.. (NotificationProviderIds ?? [])],
        CompactDensity = CompactDensity,
        ShowTotalSpend = ShowTotalSpend,
        DisabledProviders = [.. (DisabledProviders ?? [])],
        ProviderSelectionInitialized = ProviderSelectionInitialized,
        UsageDisplay = UsageDisplay,
        ResetTimeDisplay = ResetTimeDisplay,
        MotionPreference = MotionPreference,
        SpendMetric = SpendMetric,
        AlmostOutAlerts = AlmostOutAlerts,
        CuttingItCloseAlerts = CuttingItCloseAlerts,
        WillRunOutAlerts = WillRunOutAlerts,
        NotificationTrigger = NotificationTrigger,
        HideFromScreenShare = HideFromScreenShare,
        StarredMetrics = [.. (StarredMetrics ?? [])],
        WidgetPlacements = WidgetPlacements is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(WidgetPlacements, StringComparer.OrdinalIgnoreCase)
    };

    public TaskbarWidgetPlacement GetWidgetPlacement(string monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId)) monitorId = MonitorPlacementService.PrimaryMonitorId;
        if (WidgetPlacements is not null)
        {
            if (WidgetPlacements.TryGetValue(monitorId, out var placement)) return placement;
            var match = WidgetPlacements.FirstOrDefault(pair => pair.Key.Equals(monitorId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key)) return match.Value;
        }
        return TaskbarWidgetPlacement.Default;
    }

    public void SetWidgetPlacement(string monitorId, double edgeOffsetDip)
    {
        if (string.IsNullOrWhiteSpace(monitorId)) monitorId = MonitorPlacementService.PrimaryMonitorId;
        WidgetPlacements ??= new(StringComparer.OrdinalIgnoreCase);
        WidgetPlacements[monitorId] = new TaskbarWidgetPlacement(Math.Clamp(edgeOffsetDip, 4, 2000));
    }
}

public sealed record TaskbarWidgetPlacement(double EdgeOffsetDip)
{
    public static TaskbarWidgetPlacement Default { get; } = new(180);
}

/// <summary>
/// A presentation-safe metric shared by the dashboard, tray, and taskbar.
/// <para>
/// <see cref="ResetAt"/> is deliberately optional and contains only a timestamp. Status surfaces
/// format it at render time, which keeps reset countdowns honest without copying provider credentials
/// or raw response data into shell state.
/// </para>
/// </summary>
public sealed record MetricDisplay(
    string Label,
    string Value,
    string Detail,
    double Progress,
    string State,
    string Provider,
    DateTimeOffset? ResetAt = null,
    bool IsMeter = false,
    IReadOnlyList<MetricChartDisplay>? ChartPoints = null)
{
    public bool IsChart => ChartPoints is { Count: > 0 };
}

public sealed record MetricChartDisplay(string Label, double Value, string? ValueLabel = null);

public static class ResetTimeFormatter
{
    public static string FormatSurface(DateTimeOffset resetAt, string? displayMode)
        => resetAt <= DateTimeOffset.UtcNow ? "Waiting for reset data" : Format(resetAt, displayMode);

    public static string Format(DateTimeOffset resetAt, string? displayMode)
    {
        if (string.Equals(displayMode, "Exact time", StringComparison.OrdinalIgnoreCase))
        {
            var local = resetAt.ToLocalTime();
            return $"resets {local:MMM d, h:mm tt}";
        }

        return $"resets in {ResetCalculator.FormatRemaining(resetAt)}";
    }
}

public sealed record ProviderCardDisplay(
    string Provider,
    string Plan,
    string Status,
    string Used,
    string Limit,
    string Reset,
    double Progress,
    string Accent,
    IReadOnlyList<MetricDisplay> Metrics)
{
    public string ProviderId { get; init; } = string.Empty;

    /// <summary>Compact provider mark used in the header and taskbar-adjacent surfaces.</summary>
    public string Mark { get; init; } = string.Empty;

    /// <summary>Packaged provider logo path used by the WPF and Tauri presentation layers.</summary>
    public string LogoPath { get; init; } = string.Empty;

    public Geometry? LogoGeometry { get; init; }

    /// <summary>Rows starred by the user, shown in the collapsed Always Visible group.</summary>
    public IReadOnlyList<MetricDisplay> AlwaysMetrics { get; init; } = [];

    /// <summary>Remaining rows, available behind the provider's On Demand expander.</summary>
    public IReadOnlyList<MetricDisplay> OnDemandMetrics { get; init; } = [];

    /// <summary>Optional provider recovery action, shown only when a local auth repair is available.</summary>
    public bool HasAction { get; init; }
    public string ActionLabel { get; init; } = string.Empty;
}
