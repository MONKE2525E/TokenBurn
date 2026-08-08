using System.Globalization;
using System.Windows.Media;
using UsageMonitor.LocalApi;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace UsageMonitor.Desktop;

/// <summary>Time window used by the spend summary. Values match the compact OpenUsage picker.</summary>
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
    bool Estimated = false)
{
    public MediaBrush Color => SpendRingModel.BrushFromHex(ColorHex);
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
    bool HasEstimatedValues);

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
            var value = metric switch
            {
                SpendRingMetric.Tokens => tokens,
                SpendRingMetric.CostPerMillionTokens => tokens <= 0 ? 0 : cost / tokens * 1_000_000d,
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

        var ordered = slices.OrderByDescending(slice => slice.Value).ToArray();
        var total = ordered.Sum(slice => slice.Value);
        var hasEstimated = ordered.Any(slice => slice.Estimated);
        return new SpendRingSummary(period, metric, ordered, total, FormatTotal(total, metric),
            MetricUnit(metric), ordered.Length > 0 && total > 0, hasEstimated);
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
        SpendRingMetric.CostPerMillionTokens => "per MTok",
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
