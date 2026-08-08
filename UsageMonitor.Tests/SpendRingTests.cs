using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class SpendRingTests
{
    [Fact]
    public void ThirtyDayCostAggregatesProviderSlicesAndSortsLargestFirst()
    {
        var today = new DateOnly(2026, 8, 5);
        var snapshots = new[]
        {
            Snapshot("codex", "Codex", (today, 1_000_000d, 0.37d), (today.AddDays(-10), 500_000d, 0.13d)),
            Snapshot("claude", "Claude", (today.AddDays(-1), 2_000_000d, 0.75d)),
            Snapshot("old", "Old", (today.AddDays(-31), 9_000_000d, 9d))
        };

        var summary = SpendRingModel.Build(snapshots, SpendRingPeriod.ThirtyDays, SpendRingMetric.Cost, today);

        Assert.Equal(2, summary.Segments.Count);
        Assert.Equal("Claude", summary.Segments[0].DisplayName);
        Assert.Equal(1.25d, summary.Total, 3);
        Assert.Equal("$1.25", summary.TotalLabel);
        Assert.Empty(summary.UnitLabel);
    }

    [Fact]
    public void PeriodAndMetricSelectionUseOnlyMatchingHistory()
    {
        var today = new DateOnly(2026, 8, 5);
        var snapshot = Snapshot("codex", "Codex",
            (today, 1_000_000d, 0.50d),
            (today.AddDays(-1), 2_000_000d, 0.60d),
            (today.AddDays(-2), 3_000_000d, 0.90d));

        var todaySummary = SpendRingModel.Build(new[] { snapshot }, SpendRingPeriod.Today, SpendRingMetric.Tokens, today);
        var yesterdaySummary = SpendRingModel.Build(new[] { snapshot }, SpendRingPeriod.Yesterday, SpendRingMetric.CostPerMillionTokens, today);

        Assert.Equal(1_000_000d, todaySummary.Total, 3);
        Assert.Equal("1M", todaySummary.TotalLabel);
        Assert.Equal(0.30d, yesterdaySummary.Total, 3);
        Assert.Equal("$0.30", yesterdaySummary.TotalLabel);
        Assert.Equal("per MTok", yesterdaySummary.UnitLabel);
        Assert.Equal(2_000_000d, yesterdaySummary.Segments[0].Tokens, 3);

        Assert.Equal("tokens", todaySummary.UnitLabel);
    }

    [Fact]
    public void NoHistoryDoesNotInventZeroValuedSlices()
    {
        var summary = SpendRingModel.Build(new[]
        {
            new UsageSnapshotData("codex", "Codex", "Plus", Array.Empty<UsageMetricData>(), DateTimeOffset.UtcNow)
        }, today: new DateOnly(2026, 8, 5));

        Assert.False(summary.HasData);
        Assert.Empty(summary.Segments);
        Assert.Equal("$0.00", summary.TotalLabel);
    }

    [Fact]
    public void LargeDollarTotalsUseTheKSuffixCorrectly()
    {
        Assert.Equal("$1.2K", SpendRingModel.FormatTotal(1_234.56, SpendRingMetric.Cost));
        Assert.Equal("$1234.56", SpendRingModel.FormatTotal(1_234.56, SpendRingMetric.CostPerMillionTokens));
    }

    private static UsageSnapshotData Snapshot(string id, string name, params (DateOnly Date, double Tokens, double Cost)[] points)
        => new(id, name, "Plus", Array.Empty<UsageMetricData>(), DateTimeOffset.UtcNow)
        {
            UsageHistory = new UsageHistoryData(points.Select(point =>
                new UsageHistoryPointData(point.Date, point.Tokens, point.Cost)))
        };
}
