using UsageMonitor.LocalApi;

namespace UsageMonitor.Desktop;

/// <summary>A single provider quota line becoming available again.</summary>
internal sealed record ResetNotification(
    string ProviderId,
    string DisplayName,
    string MetricLabel,
    DateTimeOffset ResetAt);

/// <summary>
/// Keeps reset notifications independent from provider refresh cadence. The owner calls Observe
/// after each successful refresh and Tick from the existing one-second UI heartbeat.
/// </summary>
internal sealed class ResetNotificationScheduler
{
    private readonly Dictionary<ResetKey, ResetNotification> _scheduled = new();

    public int ScheduledCount => _scheduled.Count;

    public void Observe(
        IReadOnlyList<UsageSnapshotData> snapshots,
        bool enabled,
        IReadOnlyCollection<string>? selectedProviderIds,
        DateTimeOffset now)
    {
        if (!enabled)
        {
            _scheduled.Clear();
            return;
        }

        var selected = new HashSet<string>(
            (selectedProviderIds ?? Array.Empty<string>()).Select(UsageMonitor.Core.ProviderCatalog.NormalizeId),
            StringComparer.OrdinalIgnoreCase);
        var observedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedKeys = new HashSet<ResetKey>();

        foreach (var snapshot in snapshots)
        {
            // A warning/error can be a cached last-good snapshot with a new failure timestamp.
            // Preserve its existing timer instead of replacing it with stale data.
            if (!string.IsNullOrWhiteSpace(snapshot.Warning) || !string.IsNullOrWhiteSpace(snapshot.Error))
                continue;

            var providerId = UsageMonitor.Core.ProviderCatalog.NormalizeId(snapshot.ProviderId);
            observedProviders.Add(providerId);
            if (!selected.Contains(providerId)) continue;

            foreach (var line in snapshot.Lines.OfType<ProgressMetricData>())
            {
                if (line.ResetsAt is not { } resetAt || resetAt <= now) continue;

                var key = new ResetKey(providerId.ToLowerInvariant(), line.Label.Trim().ToLowerInvariant());
                observedKeys.Add(key);
                var notification = new ResetNotification(providerId, snapshot.DisplayName, line.Label, resetAt);
                if (!_scheduled.TryGetValue(key, out var existing) || existing.ResetAt != resetAt)
                    _scheduled[key] = notification;
            }
        }

        // Successful snapshots are authoritative for the providers they contain. Remove a line
        // that disappeared or expired, while leaving warning/error providers untouched.
        foreach (var pair in _scheduled.ToArray())
        {
            if (!selected.Contains(pair.Key.ProviderId) ||
                (observedProviders.Contains(pair.Key.ProviderId) && !observedKeys.Contains(pair.Key)))
                _scheduled.Remove(pair.Key);
        }
    }

    public int Tick(DateTimeOffset now, Action<ResetNotification> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);

        var due = _scheduled
            .Where(pair => pair.Value.ResetAt <= now)
            .OrderBy(pair => pair.Value.ResetAt)
            .ToArray();

        foreach (var pair in due)
        {
            _scheduled.Remove(pair.Key);
            notify(pair.Value);
        }

        return due.Length;
    }

    private readonly record struct ResetKey(string ProviderId, string MetricLabel);
}
