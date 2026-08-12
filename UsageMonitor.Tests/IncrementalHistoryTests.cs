using UsageMonitor.Core;
using UsageMonitor.Core.Providers;
using UsageMonitor.Core.Providers.Codex;
using UsageMonitor.Core.Providers.Claude;

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

    [Fact]
    public void ClaudeIncrementalScanReusesUnchangedFilesAndMatchesFullScan()
    {
        const string projectLine = "{\"timestamp\":\"2030-01-01T11:00:00Z\",\"requestId\":\"r1\",\"costUSD\":2.5,\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet\",\"usage\":{\"input_tokens\":800,\"cache_read_input_tokens\":200,\"output_tokens\":400}}}\n";
        var projectsDir = Path.Combine(_tempDirectory, "projects");
        Directory.CreateDirectory(projectsDir);
        var sessionPath = Path.Combine(projectsDir, "session.jsonl");
        File.WriteAllText(sessionPath, projectLine);
        File.SetLastWriteTimeUtc(sessionPath, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var first = new ClaudeLogUsageScanner().Scan(_tempDirectory, Now.AddDays(-30), _storeDirectory, report =>
        {
            Assert.Equal(1, report.FilesDiscovered);
            Assert.Equal(1, report.FilesChanged);
            Assert.Equal(0, report.FilesUnchanged);
            Assert.Equal(1, report.RowsRead);
        });
        Assert.Equal(1400, first.TotalTokens);
        Assert.Equal(2.5, first.TotalCostUsd, 3);

        var second = new ClaudeLogUsageScanner().Scan(_tempDirectory, Now.AddDays(-30), _storeDirectory, report =>
        {
            Assert.Equal(1, report.FilesDiscovered);
            Assert.Equal(0, report.FilesChanged);
            Assert.Equal(1, report.FilesUnchanged);
            Assert.Equal(0, report.RowsRead);
        });

        Assert.Equal(first.Points, second.Points);
        Assert.Equal(first.Breakdown, second.Breakdown);
        Assert.Equal(first.UnknownModels, second.UnknownModels);
        Assert.Equal(1400, second.TotalTokens);
    }

    [Fact]
    public void AppendedClaudeFileIsReparsedButUnchangedSiblingsAreReused()
    {
        const string firstLine = "{\"timestamp\":\"2030-01-01T11:00:00Z\",\"requestId\":\"r1\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet\",\"usage\":{\"input_tokens\":800,\"output_tokens\":400}}}\n";
        const string appendedLine = "{\"timestamp\":\"2030-01-01T11:00:02Z\",\"requestId\":\"r2\",\"message\":{\"id\":\"m2\",\"model\":\"claude-sonnet\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}}}\n";
        var projectsDir = Path.Combine(_tempDirectory, "projects");
        Directory.CreateDirectory(projectsDir);
        var sessionPath = Path.Combine(projectsDir, "session.jsonl");
        File.WriteAllText(sessionPath, firstLine);
        File.SetLastWriteTimeUtc(sessionPath, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var first = new ClaudeLogUsageScanner().Scan(_tempDirectory, Now.AddDays(-30), _storeDirectory, null);
        Assert.Equal(1200, first.TotalTokens);

        File.AppendAllText(sessionPath, appendedLine);
        File.SetLastWriteTimeUtc(sessionPath, new DateTime(2030, 1, 1, 0, 0, 1, DateTimeKind.Utc));

        var second = new ClaudeLogUsageScanner().Scan(_tempDirectory, Now.AddDays(-30), _storeDirectory, report =>
        {
            Assert.Equal(1, report.FilesChanged);
            Assert.Equal(0, report.FilesUnchanged);
        });

        Assert.Equal(1350, second.TotalTokens);
    }

    /// <summary>
    /// An index written by an older format version (v1 filtered at the `since` instant instead of
    /// the local day) must be rejected and fully rebuilt, so cached contributions can never
    /// silently disagree with a fresh parse on the boundary day.
    /// </summary>
    [Fact]
    public void OlderVersionIndexIsRejectedAndRebuilt()
    {
        WriteFile("a.jsonl", SessionLine("2029-12-10T10:00:00Z", 100, 20));
        var path = Path.Combine(_tempDirectory, "a.jsonl");
        Directory.CreateDirectory(_storeDirectory);
        var stale = new ProviderHistoryIndex
        {
            Version = 1,
            CatalogFingerprint = HistorySourceFingerprint.Catalog(UsageMonitorPaths.Current.PricingDirectory),
            Sources =
            {
                [path] = new SourceHistoryContribution
                {
                    Fingerprint = HistorySourceFingerprint.Of(path),
                    Points = [new UsageHistoryPoint(new DateOnly(2029, 12, 10), 999, 0, true)]
                }
            }
        };
        File.WriteAllText(Path.Combine(_storeDirectory, "history-codex.json"),
            System.Text.Json.JsonSerializer.Serialize(stale, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        var history = new CodexLogUsageScanner(localTimeZone: TimeZoneInfo.Utc)
            .Scan(_tempDirectory, new DateTimeOffset(2029, 12, 9, 12, 0, 0, TimeSpan.Zero), _storeDirectory, report =>
            {
                Assert.Equal(1, report.FilesChanged);
                Assert.Equal(0, report.FilesUnchanged);
            });

        // The stale 999-token contribution must be discarded in favor of the real file contents.
        Assert.Equal(120, history.TotalTokens);
    }

    /// <summary>
    /// Cached contributions are filtered at parse time by the then-current window boundary. When
    /// the window moves backward (clock rollback, shortened interval) the cached days are missing
    /// records a fresh parse would include, so the index must be rebuilt.
    /// </summary>
    [Fact]
    public void WindowMovingBackwardRebuildsTheIndex()
    {
        WriteFile("a.jsonl", SessionLine("2029-12-10T10:00:00Z", 100, 20) + SessionLine("2029-12-11T10:00:00Z", 50, 10));
        var scanner = new CodexLogUsageScanner(localTimeZone: TimeZoneInfo.Utc);

        // Window starts 12-11: only the 12-11 record is inside.
        var first = scanner.Scan(_tempDirectory, new DateTimeOffset(2029, 12, 11, 12, 0, 0, TimeSpan.Zero), _storeDirectory, null);
        Assert.Equal(60, first.TotalTokens);

        // Window moves back to 12-10: the cached contribution is missing the 12-10 record.
        var second = scanner.Scan(_tempDirectory, new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero), _storeDirectory,
            report => Assert.Equal(1, report.FilesChanged));

        var fresh = new CodexLogUsageScanner(localTimeZone: TimeZoneInfo.Utc)
            .Scan(_tempDirectory, new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(fresh.TotalTokens, second.TotalTokens);
        Assert.Equal(180, second.TotalTokens);
    }

    /// <summary>
    /// A timezone change re-labels every bucketed day, so contributions cached under the old zone
    /// must be rebuilt instead of showing mixed-zone days.
    /// </summary>
    [Fact]
    public void TimezoneChangeRebuildsTheIndex()
    {
        var utc = TimeZoneInfo.Utc;
        var pacific = TimeZoneInfo.CreateCustomTimeZone("Test Pacific", TimeSpan.FromHours(-7), "Test Pacific", "Test Pacific", "Test Pacific", [], false);
        // 2030-01-01T06:00Z is 2029-12-31 22:00 in UTC-7.
        WriteFile("a.jsonl", SessionLine("2030-01-01T06:00:00Z", 100, 20));
        var scanner = new CodexLogUsageScanner(localTimeZone: utc);
        var first = scanner.Scan(_tempDirectory, new DateTimeOffset(2029, 12, 20, 12, 0, 0, TimeSpan.Zero), _storeDirectory, null);
        Assert.Equal(new DateOnly(2030, 1, 1), first.Points.Single().Date);

        var second = new CodexLogUsageScanner(localTimeZone: pacific)
            .Scan(_tempDirectory, new DateTimeOffset(2029, 12, 20, 12, 0, 0, TimeSpan.Zero), _storeDirectory,
                report => Assert.Equal(1, report.FilesChanged));

        var fresh = new CodexLogUsageScanner(localTimeZone: pacific)
            .Scan(_tempDirectory, new DateTimeOffset(2029, 12, 20, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(fresh.Points, second.Points);
        Assert.Equal(new DateOnly(2029, 12, 31), second.Points.Single().Date);
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
