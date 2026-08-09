using System.Globalization;
using System.Windows.Media;
using UsageMonitor.LocalApi;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace UsageMonitor.Desktop;

/// <summary>Time window used by the spend summary.</summary>
public enum SpendRingPeriod
{
    Today,
    Yesterday,
    ThirtyDays
}

/// <summary>Value shown in the spend summary ring.</summary>
public enum SpendRingMetric
{
    Cost,
    CostPerMillionTokens,
    Tokens
}

/// <summary>A provider slice and its source values for the selected time window.</summary>
public sealed record SpendRingSegment(
    string ProviderId,
    string DisplayName,
    double Value,
    double CostUsd,
    double Tokens,
    string ColorHex,
    bool Estimated = false,
    IReadOnlyList<SpendRingSegment>? Children = null)
{
    public MediaBrush Color => SpendRingModel.BrushFromHex(ColorHex);
    public bool IsAggregate => Children is { Count: > 0 };
}

/// <summary>The complete, redacted spend summary consumed by the card and renderer.</summary>
public sealed record SpendRingSummary(
    SpendRingPeriod Period,
    SpendRingMetric Metric,
    IReadOnlyList<SpendRingSegment> Segments,
    double Total,
    string TotalLabel,
    string UnitLabel,
    bool HasData,
    bool HasEstimatedValues,
    bool IsDrillDown = false,
    string? ParentLabel = null);

/// <summary>
/// Aggregates local usage history into stable, UI-independent values. It deliberately ignores
/// providers without history instead of manufacturing zero-valued slices.
/// </summary>
public static class SpendRingModel
{
    public static SpendRingSummary Build(
        IEnumerable<UsageSnapshotData>? snapshots,
        SpendRingPeriod period = SpendRingPeriod.ThirtyDays,
        SpendRingMetric metric = SpendRingMetric.Cost,
        DateOnly? today = null,
        IReadOnlyDictionary<string, string>? colors = null)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.Now);
        var (from, to) = period switch
        {
            SpendRingPeriod.Today => (now, now),
            SpendRingPeriod.Yesterday => (now.AddDays(-1), now.AddDays(-1)),
            _ => (now.AddDays(-29), now)
        };

        var source = snapshots ?? Array.Empty<UsageSnapshotData>();
        var slices = new List<SpendRingSegment>();
        foreach (var snapshot in source)
        {
            var points = snapshot.UsageHistory?.Points
                .Where(point => point.Date >= from && point.Date <= to)
                .ToArray();
            if (points is null || points.Length == 0) continue;

            var cost = points.Sum(point => SafeNonNegative(point.CostUsd));
            var tokens = points.Sum(point => SafeNonNegative(point.Tokens));
            // Cost/MTok is a rate, and rates are not additive, so it cannot size slices (a
            // high-rate, near-zero-usage provider would swallow the ring). Weight slices by
            // tokens: the blended rate in the center is a token-weighted average.
            var value = metric switch
            {
                SpendRingMetric.Tokens => tokens,
                SpendRingMetric.CostPerMillionTokens => tokens,
                _ => cost
            };
            if (!double.IsFinite(value) || value <= 0) continue;
            var id = string.IsNullOrWhiteSpace(snapshot.ProviderId) ? snapshot.DisplayName : snapshot.ProviderId;
            var color = colors is not null && colors.TryGetValue(id, out var configured)
                ? configured
                : colors is not null && colors.TryGetValue(snapshot.DisplayName, out configured)
                    ? configured
                    : DefaultColor(id);
            slices.Add(new SpendRingSegment(id, snapshot.DisplayName, value, cost, tokens, color,
                points.Any(point => point.Estimated)));
        }

        var ordered = GroupSmallSlices(slices.OrderByDescending(slice => slice.Value).ToArray());
        var ringTotal = ordered.Sum(slice => slice.Value);
        // The headline number for Cost/MTok is the blended rate across every included provider
        // (total cost / total tokens), never the meaningless sum of per-provider rates.
        var total = metric == SpendRingMetric.CostPerMillionTokens
            ? ringTotal <= 0 ? 0 : ordered.Sum(slice => slice.CostUsd) / ringTotal * 1_000_000d
            : ringTotal;
        var hasEstimated = ordered.Any(slice => slice.Estimated);
        return new SpendRingSummary(period, metric, ordered, total, FormatTotal(total, metric),
            MetricUnit(metric), ordered.Length > 0 && ringTotal > 0, hasEstimated);
    }

    public static SpendRingSummary Expand(SpendRingSummary summary, SpendRingSegment aggregate)
    {
        if (!aggregate.IsAggregate) return summary;
        var children = aggregate.Children!.OrderByDescending(x => x.Value).ToArray();
        return Rebuild(summary with { Segments = children, IsDrillDown = true, ParentLabel = aggregate.DisplayName });
    }

    public static SpendRingSummary Collapse(SpendRingSummary summary)
    {
        if (!summary.IsDrillDown) return summary;
        return summary with { IsDrillDown = false, ParentLabel = null };
    }

    private static SpendRingSummary Rebuild(SpendRingSummary summary)
    {
        var ringTotal = summary.Segments.Sum(x => x.Value);
        var total = summary.Metric == SpendRingMetric.CostPerMillionTokens
            ? ringTotal <= 0 ? 0 : summary.Segments.Sum(x => x.CostUsd) / ringTotal * 1_000_000d
            : ringTotal;
        return summary with
        {
            Total = total,
            TotalLabel = FormatTotal(total, summary.Metric),
            HasData = summary.Segments.Count > 0 && ringTotal > 0,
            HasEstimatedValues = summary.Segments.Any(x => x.Estimated)
        };
    }

    private static SpendRingSegment[] GroupSmallSlices(IReadOnlyList<SpendRingSegment> ordered)
    {
        if (ordered.Count < 3) return ordered.ToArray();
        var total = ordered.Sum(x => x.Value);
        if (total <= 0) return ordered.ToArray();
        var threshold = total * 0.01;
        var tail = new List<SpendRingSegment>();
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            var candidate = ordered[index];
            if (tail.Sum(x => x.Value) + candidate.Value > threshold) break;
            tail.Add(candidate);
        }
        if (tail.Count < 2) return ordered.ToArray();
        var grouped = new SpendRingSegment(
            "others", "Others", tail.Sum(x => x.Value), tail.Sum(x => x.CostUsd),
            tail.Sum(x => x.Tokens), "#77777D", tail.Any(x => x.Estimated), tail);
        return ordered.Take(ordered.Count - tail.Count).Append(grouped).ToArray();
    }

    public static string FormatTotal(double total, SpendRingMetric metric)
    {
        if (total <= 0 || double.IsNaN(total) || double.IsInfinity(total))
            return metric == SpendRingMetric.Tokens ? "0" : "$0.00";
        return metric switch
        {
            SpendRingMetric.Tokens => FormatTokens(total),
            SpendRingMetric.CostPerMillionTokens => $"${total:0.00}",
            _ => total >= 1000 ? $"${total / 1000:0.0}K" : $"${total:0.00}"
        };
    }

    public static string MetricUnit(SpendRingMetric metric) => metric switch
    {
        SpendRingMetric.Tokens => "tokens",
        // The metric picker already identifies this rate. Repeating "per MTok" inside the
        // compact ring adds noise beside a value that is already unambiguous.
        SpendRingMetric.CostPerMillionTokens => string.Empty,
        _ => string.Empty
    };

    public static string FormatTokens(double tokens)
    {
        if (tokens >= 1_000_000_000) return $"{tokens / 1_000_000_000:0.##}B";
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000:0.##}M";
        if (tokens >= 1_000) return $"{tokens / 1_000:0.##}K";
        return Math.Round(tokens).ToString("N0", CultureInfo.InvariantCulture);
    }

    internal static MediaBrush BrushFromHex(string? hex)
    {
        try
        {
            var color = (MediaColor)MediaColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? "#53D2C3" : hex)!;
            return new MediaSolidColorBrush(color);
        }
        catch
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(83, 210, 195));
        }
    }

    private static string DefaultColor(string providerId)
    {
        if (string.Equals(providerId, "opencode", StringComparison.OrdinalIgnoreCase)) return "#FFFFFF";
        var palette = new[] { "#53D2C3", "#E77B5D", "#9A8CFF", "#69A7FF", "#F5B95A", "#C58BFF" };
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in providerId.ToUpperInvariant())
            {
                hash ^= character;
                hash *= 16777619;
            }
            var index = (int)(hash % (uint)palette.Length);
            return palette[index];
        }
    }

    private static double SafeNonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
