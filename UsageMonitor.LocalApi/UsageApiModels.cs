using System.Collections.ObjectModel;

namespace UsageMonitor.LocalApi;

/// <summary>Safe values supplied by the Core refresh/cache engine to the local transport.</summary>
public sealed record UsageSnapshotData(
    string ProviderId,
    string DisplayName,
    string? Plan,
    IReadOnlyList<UsageMetricData> Lines,
    DateTimeOffset FetchedAt)
{
    /// <summary>Redacted refresh failure text, if the provider returned an error snapshot.</summary>
    public string? Error { get; init; }

    /// <summary>Non-fatal provider notice, such as rate limiting while cached values remain usable.</summary>
    public string? Warning { get; init; }

    /// <summary>Optional local-only spend history. Providers without a local history source leave this null.</summary>
    public UsageHistoryData? UsageHistory { get; init; }

    public UsageSnapshotData(string providerId, string displayName, string? plan,
        IEnumerable<UsageMetricData>? lines, DateTimeOffset fetchedAt)
        : this(providerId, displayName, plan,
            new ReadOnlyCollection<UsageMetricData>((lines ?? []).ToList()), fetchedAt) { }
}

public sealed record UsageHistoryData(IReadOnlyList<UsageHistoryPointData> Points)
{
    public UsageHistoryData(IEnumerable<UsageHistoryPointData>? points)
        : this(new ReadOnlyCollection<UsageHistoryPointData>((points ?? []).ToList())) { }

    public double TotalCostUsd => Points.Sum(point => point.CostUsd);
    public double TotalTokens => Points.Sum(point => point.Tokens);
}

public sealed record UsageHistoryPointData(DateOnly Date, double Tokens, double CostUsd, bool Estimated = false);

public abstract record UsageMetricData(string Label);

public sealed record ProgressMetricData(string Label, double Used, double Limit,
    string Unit = "percent", DateTimeOffset? ResetsAt = null, long? PeriodDurationMs = null,
    string? Color = null) : UsageMetricData(Label);

public sealed record TextMetricData(string Label, string Value, string? Color = null,
    string? Subtitle = null) : UsageMetricData(Label);

public sealed record BadgeMetricData(string Label, string Text, string? Color = null,
    string? Subtitle = null) : UsageMetricData(Label);

public sealed record ScalarValueData(double Number, string Unit, string? ValueLabel = null,
    bool Estimated = false);

public sealed record ValuesMetricData(string Label, IReadOnlyList<ScalarValueData> Values,
    string? Color = null, IReadOnlyList<DateTimeOffset>? ExpiresAt = null) : UsageMetricData(Label)
{
    public ValuesMetricData(string label, IEnumerable<ScalarValueData>? values, string? color = null,
        IEnumerable<DateTimeOffset>? expiresAt = null)
        : this(label, new ReadOnlyCollection<ScalarValueData>((values ?? []).ToList()), color,
            new ReadOnlyCollection<DateTimeOffset>((expiresAt ?? []).ToList())) { }
}

public sealed record ChartPointData(string Label, double Value, string? ValueLabel = null);

public sealed record BarChartMetricData(string Label, IReadOnlyList<ChartPointData> Points,
    string? Note = null) : UsageMetricData(Label)
{
    public BarChartMetricData(string label, IEnumerable<ChartPointData>? points, string? note = null)
        : this(label, new ReadOnlyCollection<ChartPointData>((points ?? []).ToList()), note) { }
}

/// <summary>Transport boundary that keeps API/CLI independent of Core implementation details.</summary>
public interface IUsageSnapshotSource
{
    IReadOnlySet<string> KnownProviderIds { get; }
    Task<IReadOnlyList<UsageSnapshotData>> GetSnapshotsAsync(string? providerId, bool force,
        CancellationToken cancellationToken = default);
}

public sealed class EmptyUsageSnapshotSource : IUsageSnapshotSource
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(
        ["codex", "claude-code", "claude", "antigravity", "copilot", "cursor", "devin", "grok", "opencode"],
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> KnownProviderIds => Known;
    public Task<IReadOnlyList<UsageSnapshotData>> GetSnapshotsAsync(string? providerId, bool force,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UsageSnapshotData>>([]);
}

public sealed record UsageApiOptions
{
    public int Port { get; init; } = 6736;
    public string Host { get; init; } = "127.0.0.1";
    public bool EnableCors { get; init; }
    public int MaxConcurrentRequests { get; init; } = 16;
    public TimeSpan SnapshotFreshness { get; init; } = TimeSpan.FromMinutes(5);
}
