using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace UsageMonitor.Core;

/// <summary>How a numeric metric should be rendered.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetricKind
{
    Percent,
    Dollars,
    Count,
    Duration
}

/// <summary>Visual state used by status surfaces.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetricState
{
    Normal,
    Warning,
    Critical,
    Exhausted,
    Error,
    Unknown
}

/// <summary>The normalized shape of a progress metric.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetricLineType
{
    Text,
    Values,
    Progress,
    Badge,
    Chart
}

/// <summary>A raw number carried by a metric. Formatting belongs at the UI boundary.</summary>
public sealed record MetricValue(
    double Number,
    MetricKind Kind,
    string? Label = null,
    bool Estimated = false);

public sealed record MetricChartPoint(
    double Value,
    string Label,
    string? ValueLabel = null);

/// <summary>A normalized metric row shared by the dashboard, widget, CLI, and local API.</summary>
public sealed record MetricLine
{
    public MetricLineType Type { get; init; }
    public string Label { get; init; } = string.Empty;
    public string? Text { get; init; }
    public string? Subtitle { get; init; }
    public string? ColorHex { get; init; }
    public IReadOnlyList<MetricValue> Values { get; init; } = Array.Empty<MetricValue>();
    public IReadOnlyList<MetricChartPoint> Points { get; init; } = Array.Empty<MetricChartPoint>();
    public double? Used { get; init; }
    public double? Limit { get; init; }
    public MetricKind? Format { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public TimeSpan? Period { get; init; }
    public IReadOnlyList<DateTimeOffset> ExpiriesAt { get; init; } = Array.Empty<DateTimeOffset>();
    public IReadOnlyList<string> UnknownModels { get; init; } = Array.Empty<string>();
    public MetricState State { get; init; } = MetricState.Normal;

    public bool IsError => Type == MetricLineType.Badge &&
                           string.Equals(Label, ErrorBadgeLabel, StringComparison.OrdinalIgnoreCase);

    public const string ErrorBadgeLabel = "Error";

    public static MetricLine TextLine(string label, string value, string? colorHex = null, string? subtitle = null,
        MetricState state = MetricState.Normal) => new()
    {
        Type = MetricLineType.Text, Label = label, Text = value, ColorHex = colorHex, Subtitle = subtitle, State = state
    };

    public static MetricLine Badge(string label, string text, string? colorHex = null, string? subtitle = null,
        MetricState state = MetricState.Normal) => new()
    {
        Type = MetricLineType.Badge, Label = label, Text = text, ColorHex = colorHex, Subtitle = subtitle, State = state
    };

    public static MetricLine Progress(string label, double used, double limit, MetricKind format,
        DateTimeOffset? resetsAt = null, TimeSpan? period = null, string? colorHex = null,
        MetricState? state = null) => new()
    {
        Type = MetricLineType.Progress,
        Label = label,
        Used = used,
        Limit = limit,
        Format = format,
        ResetsAt = resetsAt,
        Period = period,
        ColorHex = colorHex,
        State = state ?? UsageMath.GetMetricState(used, limit)
    };

    public static MetricLine ValuesLine(string label, IEnumerable<MetricValue> values, string? colorHex = null,
        IEnumerable<DateTimeOffset>? expiriesAt = null, IEnumerable<string>? unknownModels = null,
        MetricState state = MetricState.Normal) => new()
    {
        Type = MetricLineType.Values,
        Label = label,
        Values = new ReadOnlyCollection<MetricValue>((values ?? Array.Empty<MetricValue>()).ToList()),
        ColorHex = colorHex,
        ExpiriesAt = new ReadOnlyCollection<DateTimeOffset>((expiriesAt ?? Array.Empty<DateTimeOffset>()).ToList()),
        UnknownModels = new ReadOnlyCollection<string>((unknownModels ?? Array.Empty<string>()).ToList()),
        State = state
    };

    public static MetricLine Chart(string label, IEnumerable<MetricChartPoint> points, string? note = null) => new()
    {
        Type = MetricLineType.Chart,
        Label = label,
        Points = new ReadOnlyCollection<MetricChartPoint>((points ?? Array.Empty<MetricChartPoint>()).ToList()),
        Subtitle = note
    };

    public static readonly MetricLine NoUsageData = Badge("Status", "No usage data", "#A3A3A3", state: MetricState.Unknown);
}

public sealed record UsageHistoryPoint(DateOnly Date, double Tokens = 0, double CostUsd = 0, bool Estimated = false);

/// <summary>How a local history row obtained its cost. This deliberately excludes any account or request data.</summary>
public enum UsageCostBasis
{
    ProviderReported,
    CatalogEstimated,
    CoarseEstimate,
    Unpriced
}

/// <summary>
/// A privacy-safe, locally aggregated usage row. Rows are grouped by day/model before crossing
/// the provider boundary, so raw prompts, paths, session IDs, and request IDs never reach UI.
/// </summary>
public sealed record UsageBreakdownPoint(
    DateOnly Date,
    string ProviderId,
    string? ModelId,
    double UncachedInputTokens,
    double CachedInputTokens,
    double CacheCreationTokens,
    double OutputTokens,
    double ReasoningTokens,
    double CostUsd,
    UsageCostBasis CostBasis,
    PricingBasis PricingBasis,
    bool Estimated,
    double CacheSavingsUsd = 0)
{
    public double ProcessedTokens => UncachedInputTokens + CachedInputTokens + CacheCreationTokens + OutputTokens + ReasoningTokens;
}

public sealed record ProviderUsageHistory(IReadOnlyList<UsageHistoryPoint> Points)
{
    public double TotalCostUsd => Points.Sum(p => p.CostUsd);
    public double TotalTokens => Points.Sum(p => p.Tokens);
    public IReadOnlyList<string> UnknownModels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<UsageBreakdownPoint> Breakdown { get; init; } = Array.Empty<UsageBreakdownPoint>();
}

public sealed record ProviderDescriptor(
    string Id,
    string DisplayName,
    string? IconKey = null,
    IReadOnlyList<ProviderLink>? Links = null)
{
    public IReadOnlyList<ProviderLink> Links { get; init; } = Links ?? Array.Empty<ProviderLink>();
}

public sealed record ProviderLink(string Label, string Url);

public sealed record ProviderSnapshot
{
    public string ProviderId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Plan { get; init; }
    public IReadOnlyList<MetricLine> Lines { get; init; } = Array.Empty<MetricLine>();
    public DateTimeOffset RefreshedAt { get; init; } = DateTimeOffset.UtcNow;
    public ProviderUsageHistory? UsageHistory { get; init; }
    public string? Warning { get; init; }
    public ProviderErrorCategory? ErrorCategory { get; init; }

    public MetricLine? GetLine(string label) => Lines.FirstOrDefault(x =>
        string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase));

    public static ProviderSnapshot Success(ProviderDescriptor provider, IEnumerable<MetricLine>? lines = null,
        string? plan = null, DateTimeOffset? refreshedAt = null, ProviderUsageHistory? history = null,
        string? warning = null) => new()
    {
        ProviderId = provider.Id,
        DisplayName = provider.DisplayName,
        Plan = plan,
        Lines = (lines ?? Array.Empty<MetricLine>()).ToList(),
        RefreshedAt = refreshedAt ?? DateTimeOffset.UtcNow,
        UsageHistory = history,
        Warning = warning
    };

    public static ProviderSnapshot Error(ProviderDescriptor provider, string message,
        ProviderErrorCategory category = ProviderErrorCategory.Other) => new()
    {
        ProviderId = provider.Id,
        DisplayName = provider.DisplayName,
        Lines = new[] { MetricLine.Badge(MetricLine.ErrorBadgeLabel, message, "#EF4444", state: MetricState.Error) },
        RefreshedAt = DateTimeOffset.UtcNow,
        ErrorCategory = category
    };

    public static ProviderSnapshot Error(ProviderDescriptor provider, Exception error,
        ProviderErrorCategory category = ProviderErrorCategory.Other)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Error(provider, SensitiveDataRedactor.Redact(error.Message), category);
    }
}

public enum ProviderErrorCategory
{
    NotConfigured,
    NotInstalled,
    Authentication,
    Authorization,
    RateLimited,
    Network,
    Parse,
    Unsupported,
    Other
}

public static class UsageMath
{
    public static double Clamp(double value, double min = 0, double max = 1)
    {
        if (double.IsNaN(value)) return min;
        return Math.Clamp(value, min, max);
    }

    public static MetricState GetMetricState(double used, double limit)
    {
        if (limit <= 0 || double.IsNaN(used) || double.IsNaN(limit)) return MetricState.Unknown;
        var fraction = used / limit;
        return fraction >= 1 ? MetricState.Exhausted : fraction >= 0.9 ? MetricState.Critical : fraction >= 0.75 ? MetricState.Warning : MetricState.Normal;
    }
}
