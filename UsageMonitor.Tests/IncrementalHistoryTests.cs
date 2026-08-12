using UsageMonitor.Core;
using UsageMonitor.Core.Providers;
using UsageMonitor.Core.Providers.Codex;

namespace UsageMonitor.Tests;

public sealed class IncrementalHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _tempDirectory;
    private readonly string _storeDirectory;

    public IncrementalHistoryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "tokburn-incremental-" + Guid.NewGuid().ToString("N"));
        _storeDirectory = Path.Combine(_tempDirectory, "store");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
    }

    private void WriteFile(string name, string content)
    {
        File.WriteAllText(Path.Combine(_tempDirectory, name), content);
        File.SetLastWriteTimeUtc(Path.Combine(_tempDirectory, name), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private static string SessionLine(string timestamp, long input, long output) =>
        $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},\"total_tokens\":{input + output}}}}}}}}}\n";

    [Fact]
    public void IncrementalScanReusesUnchangedFilesAndMatchesFullScan()
    {
        WriteFile("a.jsonl", SessionLine("2030-01-01T10:00:00Z", 100, 20) + SessionLine("2030-01-01T10:00:01Z", 50, 10));
        WriteFile("b.jsonl", SessionLine("2030-01-01T11:00:00Z", 200, 40));

        var first = new CodexLogUsageScanner().Scan(_tempDirectory, Now.AddDays(-30), _storeDirectory, report =>
        {
            Assert.Equal(2, report.FilesDiscovered);
            Assert.Equal(2, report.FilesChanged);
            Assert.Equal(0, report.FilesUnchanged);
            Assert.Equal(3, report.RowsRead);
        });
        Assert.Equal(420, first.TotalTokens);

        var second = new CodexLogUsageScanner().Scan(_tempDirectory, Now.AddDays(-30), _storeDirectory, report =>
        {
            Assert.Equal(2, report.FilesDiscovered);
            Assert.Equal(0, report.FilesChanged);
            Assert.Equal(2, report.FilesUnchanged);
            Assert.Equal(0, report.RowsRead);
        });

        Assert.Equal(first.Points, second.Points);
        Assert.Equal(first.Breakdown, second.Breakdown);
        Assert.Equal(420, second.TotalTokens);
    }

    [Fact]
    public void AppendedFileIsReparsedButUnchangedSiblingsAreReused()
    {
        WriteFile("a.jsonl", SessionLine("2030-01-01T10:00:00Z", 100, 20));
        WriteFile("b.jsonl", SessionLine("2030-01-01T11:00:00Z", 200, 40));

        var first = new CodexLogUsageScanner().Scan(_tempDirectory, Now.AddDays(-30), _storeDirectory, null);
        Assert.Equal(360, first.TotalTokens);

        // Append a new record to a.jsonl; b.jsonl stays untouched.
        var appendedPath = Path.Combine(_tempDirectory, "a.jsonl");
        File.AppendAllText(appendedPath, SessionLine("2030-01-01T10:00:02Z", 300, 60));
        File.SetLastWriteTimeUtc(appendedPath, new DateTime(2030, 1, 1, 0, 0, 1, DateTimeKind.Utc));

        var second = new CodexLogUsageScanner().Scan(_tempDirectory, Now.AddDays(-30), _storeDirectory, report =>
        {
            Assert.Equal(1, report.FilesChanged);
            Assert.Equal(1, report.FilesUnchanged);
        });

        Assert.Equal(720, second.TotalTokens);
    }

    [Fact]
    public void MergeAggregatesContributionsAtDayGranularity()
    {
        var first = new SourceHistoryContribution
        {
            Points = [new UsageHistoryPoint(new DateOnly(2029, 12, 10), 100, 0.5, true)],
            UnknownModels = ["gpt-5"]
        };
        var second = new SourceHistoryContribution
        {
            Points = [new UsageHistoryPoint(new DateOnly(2029, 12, 10), 50, 0.25, true),
                      new UsageHistoryPoint(new DateOnly(2029, 12, 11), 30, 0.1, true)]
        };

        var merged = HistoryIndexMerge.Merge([first, second], sinceDate: new DateOnly(2029, 12, 10));

        Assert.Equal(2, merged.Points.Count);
        Assert.Equal(150, merged.Points.Single(p => p.Date.Day == 10).Tokens);
        Assert.Equal(["gpt-5"], merged.UnknownModels);
    }

    [Fact]
    public void SlidingWindowDropsContributionsOutsideSinceDate()
    {
        var contribution = new SourceHistoryContribution
        {
            Points = [new UsageHistoryPoint(new DateOnly(2029, 11, 1), 100, 0.5, true),
                      new UsageHistoryPoint(new DateOnly(2029, 12, 20), 200, 1, true)]
        };

        var merged = HistoryIndexMerge.Merge([contribution], sinceDate: new DateOnly(2029, 12, 1));

        Assert.Single(merged.Points);
        Assert.Equal(200, merged.Points[0].Tokens);
    }

    /// <summary>
    /// A pricing-catalog change re-prices every record, so contributions cached under the old
    /// catalog must be rebuilt instead of keeping stale estimates forever.
    /// </summary>
    [Fact]
    public void CatalogChangeRebuildsTheIndex()
    {
        WriteFile("a.jsonl", SessionLine("2030-01-01T10:00:00Z", 100, 20));
        var path = Path.Combine(_tempDirectory, "a.jsonl");
        Directory.CreateDirectory(_storeDirectory);
        var stale = new ProviderHistoryIndex
        {
            Version = ProviderHistoryIndexStore.Version,
            // A fingerprint that matches nothing the current pricing directory produces forces
            // the rebuild path; an index built under any earlier catalog state looks exactly like
            // this to the store.
            CatalogFingerprint = "stale-catalog-fingerprint",
            Sources =
            {
                [path] = new SourceHistoryContribution
                {
                    Fingerprint = HistorySourceFingerprint.Of(path),
                    Points = [new UsageHistoryPoint(new DateOnly(2030, 1, 1), 999, 0, true)]
                }
            }
        };
        File.WriteAllText(Path.Combine(_storeDirectory, "history-codex.json"),
            System.Text.Json.JsonSerializer.Serialize(stale, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        var history = new CodexLogUsageScanner(localTimeZone: TimeZoneInfo.Utc)
            .Scan(_tempDirectory, new DateTimeOffset(2029, 12, 20, 12, 0, 0, TimeSpan.Zero), _storeDirectory, report =>
            {
                Assert.Equal(1, report.FilesChanged);
                Assert.Equal(0, report.FilesUnchanged);
            });

        Assert.Equal(120, history.TotalTokens);
    }

    /// <summary>
    /// A corrupt index file must fall back to a full rescan instead of serving nothing or keeping
    /// stale contributions, so totals can never silently drift from the real logs.
    /// </summary>
    [Fact]
    public void CorruptIndexFallsBackToFullRescan()
    {
        WriteFile("a.jsonl", SessionLine("2030-01-01T10:00:00Z", 100, 20));
        Directory.CreateDirectory(_storeDirectory);
        File.WriteAllText(Path.Combine(_storeDirectory, "history-codex.json"), "{ this is not valid json !!!");

        var history = new CodexLogUsageScanner(localTimeZone: TimeZoneInfo.Utc)
            .Scan(_tempDirectory, new DateTimeOffset(2029, 12, 20, 12, 0, 0, TimeSpan.Zero), _storeDirectory, report =>
            {
                Assert.Equal(1, report.FilesChanged);
                Assert.Equal(0, report.FilesUnchanged);
            });

        Assert.Equal(120, history.TotalTokens);
    }
}
