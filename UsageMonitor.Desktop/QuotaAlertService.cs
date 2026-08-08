using UsageMonitor.Core;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Desktop;

/// <summary>
/// Emits one notification when an enabled quota crosses a meaningful threshold.
/// It is intentionally in-memory and redacted. Alerts are opt-in and reset when disabled.
/// </summary>
internal sealed class QuotaAlertService
{
    private readonly Dictionary<string, string> _lastStates = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(
        IReadOnlyList<UsageSnapshotData> snapshots,
        bool enabled,
        bool almostOut,
        bool cuttingItClose,
        bool willRunOut,
        Action<string> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);
        if (!enabled)
        {
            _lastStates.Clear();
            return;
        }

        foreach (var snapshot in snapshots)
        {
            foreach (var line in snapshot.Lines.OfType<ProgressMetricData>())
            {
                var state = line.Limit <= 0
                    ? "unknown"
                    : line.Used >= line.Limit && willRunOut
                        ? "exhausted"
                        : line.Used >= line.Limit * 0.9 && cuttingItClose
                            ? "critical"
                            : line.Used >= line.Limit * 0.75 && almostOut
                                ? "warning"
                                : "normal";
                var key = $"{snapshot.ProviderId}:{line.Label}";
                _lastStates.TryGetValue(key, out var previous);
                _lastStates[key] = state;
                if (state is not ("warning" or "critical" or "exhausted") || state.Equals(previous, StringComparison.OrdinalIgnoreCase))
                    continue;

                var reset = line.ResetsAt is { } resetAt
                    ? $" Resets in {ResetCalculator.FormatRemaining(resetAt)}."
                    : string.Empty;
                var action = state switch
                {
                    "exhausted" => "is exhausted.",
                    "critical" => "is cutting it close.",
                    _ => "is almost out."
                };
                notify($"{snapshot.DisplayName} {line.Label} {action}{reset}");
            }
        }
    }
}
