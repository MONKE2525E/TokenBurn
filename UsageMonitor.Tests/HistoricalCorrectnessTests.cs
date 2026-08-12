using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsageMonitor.Core;
using UsageMonitor.Core.Providers;
using UsageMonitor.Core.Providers.Antigravity;
using UsageMonitor.Core.Providers.Claude;
using UsageMonitor.Core.Providers.Codex;
using UsageMonitor.Core.Providers.OpenCode;

namespace UsageMonitor.Tests;

/// <summary>
/// Historical usage/data correctness regression matrix. Every test pins time to an injected
/// timezone and explicit instants so results are deterministic on any machine and never depend
/// on the system clock or the host timezone. Collected with the pricing-catalog tests because
/// OpenCodeProvider resolves models through the process-wide ModelPricingCatalog static state.
/// </summary>
[Collection("model-pricing-static")]
public sealed class HistoricalCorrectnessTests : IDisposable
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.CreateCustomTimeZone(
        "Test Pacific", TimeSpan.FromHours(-7), "Test Pacific", "Test Pacific", "Test Pacific", [], false);
    private static readonly TimeZoneInfo Kathmandu = TimeZoneInfo.CreateCustomTimeZone(
        "Test Kathmandu", TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(45)), "Test Kathmandu", "Test Kathmandu", "Test Kathmandu", [], false);
    private static readonly TimeZoneInfo DstZone = CreateDstZone();

    private readonly string _tempDirectory;

    public HistoricalCorrectnessTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "tokburn-correctness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
    }

    private static TimeZoneInfo CreateDstZone()
    {
        var spring = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var fall = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2025, 1, 1), new DateTime(2035, 12, 31), TimeSpan.FromHours(1), spring, fall);
        return TimeZoneInfo.CreateCustomTimeZone("Test DST", TimeSpan.FromHours(-8), "Test DST", "PST", "PDT", [rule]);
    }

    // ------------------------------------------------------------------
    // Boundary-day window semantics (Bug 1 regression)
    // ------------------------------------------------------------------

    /// <summary>
    /// The 90-day window is day-granular: records on the boundary local day before the `since`
    /// instant are part of the window. A parse-time instant filter would drop them, disagreeing
    /// with the cached contribution and with the merge's day-level filter.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void BoundaryDayRecordsBeforeSinceInstantAreIncluded(string provider)
    {
        var since = new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero); // boundary day = 2029-12-10
        var records = provider == "claude"
            ? ClaudeLine("2029-12-10T02:00:00Z", 100, 50) + ClaudeLine("2029-12-10T20:00:00Z", 100, 50)
            : CodexLastLine("2029-12-10T02:00:00Z", 100, 50) + CodexLastLine("2029-12-10T20:00:00Z", 100, 50);
        var fixture = provider == "claude"
            ? new Dictionary<string, string> { ["C:\\fixture\\projects\\session.jsonl"] = records }
            : new Dictionary<string, string> { ["C:\\fixture\\sessions\\s1.jsonl"] = records };
        var files = new FixtureFileSystem(fixture);

        var history = provider == "claude"
            ? new ClaudeLogUsageScanner(files, localTimeZone: Utc).Scan("C:\\fixture", since)
            : new CodexLogUsageScanner(files, localTimeZone: Utc).Scan("C:\\fixture", since);

        var point = Assert.Single(history.Points);
        Assert.Equal(new DateOnly(2029, 12, 10), point.Date);
        Assert.Equal(300, point.Tokens);
    }

    /// <summary>
    /// Appending to a file on the boundary day must not change which records on that day are
    /// counted: a fresh parse has to agree with the cached contribution it replaces.
    /// </summary>
    [Fact]
    public void AppendingOnBoundaryDayKeepsMorningRecordsCounted()
    {
        var codexHome = Path.Combine(_tempDirectory, "codex-home");
        var store = Path.Combine(_tempDirectory, "store");
        Directory.CreateDirectory(codexHome);
        var path = Path.Combine(codexHome, "session.jsonl");
        WriteFile(path, CodexLastLine("2029-12-09T18:00:00Z", 30, 10) +
                       CodexLastLine("2029-12-10T02:00:00Z", 100, 20) +
                       CodexLastLine("2029-12-10T20:00:00Z", 100, 20));

        var scanner = new CodexLogUsageScanner(localTimeZone: Utc);
        // First scan: both days fully inside the window.
        var first = scanner.Scan(codexHome, new DateTimeOffset(2029, 12, 9, 12, 0, 0, TimeSpan.Zero), store, null);
        Assert.Equal(240, first.Points.Single(p => p.Date.Day == 10).Tokens);

        // Slid window: boundary day is now 12-10; the cached contribution keeps the morning record.
        var second = scanner.Scan(codexHome, new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero), store, null);
        Assert.Equal(240, second.Points.Single(p => p.Date.Day == 10).Tokens);

        // Append a record; the file is re-parsed with the same day-level window. The morning
        // record must still be counted, so the boundary day only grows by the appended tokens.
        File.AppendAllText(path, CodexLastLine("2029-12-10T22:00:00Z", 10, 5));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        var third = scanner.Scan(codexHome, new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero), store, null);

        Assert.Equal(255, third.Points.Single(p => p.Date.Day == 10).Tokens);
        Assert.Equal(second.Points.Single(p => p.Date.Day == 10).Tokens + 15,
            third.Points.Single(p => p.Date.Day == 10).Tokens);
    }

    /// <summary>Codex baseline seeding uses the day window too, so boundary-day deltas match a cache built earlier.</summary>
    [Fact]
    public void CodexBoundaryDayRecordsContributeTrueDeltasToCumulativeTotals()
    {
        const string lines = """
            {"timestamp":"2029-12-09T18:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000,"output_tokens":200,"total_tokens":1200}}}}
            {"timestamp":"2029-12-10T02:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1010,"output_tokens":202,"total_tokens":1212}}}}
            {"timestamp":"2029-12-10T20:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1100,"output_tokens":220,"total_tokens":1320}}}}
            """;
        var files = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\sessions\\s1.jsonl"] = lines });

        var history = new CodexLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero));

        // 12 (morning) + 108 (evening): the full day, and identical to what an earlier parse
        // (window starting 12-09) would have cached for 12-10.
        var point = Assert.Single(history.Points);
        Assert.Equal(new DateOnly(2029, 12, 10), point.Date);
        Assert.Equal(120, point.Tokens);
    }

    /// <summary>Codex diagnostics SQL rows on the boundary day use local-midnight, not the `since` instant.</summary>
    [Fact]
    public void CodexDiagnosticsDatabaseIncludesTheFullBoundaryDay()
    {
        var codexHome = Path.Combine(_tempDirectory, "codex-diagnostics");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "session.jsonl"), string.Empty);
        CreateLogsDatabase(Path.Combine(codexHome, "logs_1.sqlite"),
            (2029, 12, 10, 8, 0, 500),   // boundary day, before the 12:00Z since instant
            (2029, 12, 10, 20, 0, 300)); // boundary day, after the since instant

        var history = new CodexLogUsageScanner(localTimeZone: Utc)
            .Scan(codexHome, new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero));

        var point = Assert.Single(history.Points);
        Assert.Equal(new DateOnly(2029, 12, 10), point.Date);
        Assert.Equal(800, point.Tokens);
    }

    /// <summary>
    /// One corrupt diagnostics row (absurd timestamp, far-future date) must neither kill the whole
    /// scan nor fabricate spend points in the window.
    /// </summary>
    [Fact]
    public void CodexDiagnosticsIgnoresOutOfRangeAndFutureTimestamps()
    {
        var codexHome = Path.Combine(_tempDirectory, "codex-diagnostics-corrupt");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "session.jsonl"), string.Empty);
        CreateLogsDatabase(Path.Combine(codexHome, "logs_1.sqlite"),
            (2029, 12, 10, 8, 0, 500));
        // A corrupt row plus a year-2201 row: both must be ignored, not crash the scan.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(codexHome, "logs_1.sqlite"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO logs (ts, target, feedback_log_body) VALUES ($ts, 'codex_core::session::turn', $body);";
            insert.Parameters.AddWithValue("$ts", long.MaxValue);
            insert.Parameters.AddWithValue("$body", "post sampling token usage total_usage_tokens=9999 model=gpt-5");
            insert.ExecuteNonQuery();
            using var insert2 = connection.CreateCommand();
            insert2.CommandText = "INSERT INTO logs (ts, target, feedback_log_body) VALUES ($ts, 'codex_core::session::turn', $body);";
            insert2.Parameters.AddWithValue("$ts", 7_300_000_000L);
            insert2.Parameters.AddWithValue("$body", "post sampling token usage total_usage_tokens=7777 model=gpt-5");
            insert2.ExecuteNonQuery();
        }

        var history = new CodexLogUsageScanner(localTimeZone: Utc)
            .Scan(codexHome, new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero));

        var point = Assert.Single(history.Points);
        Assert.Equal(new DateOnly(2029, 12, 10), point.Date);
        Assert.Equal(500, point.Tokens);
    }

    /// <summary>
    /// The logs SQLite database is authoritative for the days it covers: the same turns were also
    /// re-logged as JSONL token_count records, so counting both would double the day. JSONL points
    /// on covered days must be dropped in favor of the SQL rows; days the database does not cover
    /// keep their JSONL records.
    /// </summary>
    [Fact]
    public void CodexSqliteCoveredDaysExcludeOverlappingJsonlRecords()
    {
        var codexHome = Path.Combine(_tempDirectory, "codex-dedup-sources");
        Directory.CreateDirectory(codexHome);
        // JSONL: records on 12-10 (boundary day) and 12-11. The 12-10 records are the same turns
        // the logs database logs, so only 12-11 may keep its JSONL contribution.
        var path = Path.Combine(codexHome, "session.jsonl");
        WriteFile(path,
            CodexLastLine("2029-12-10T09:00:00Z", 100, 50) +
            CodexLastLine("2029-12-10T20:00:00Z", 200, 100) +
            CodexLastLine("2029-12-11T09:00:00Z", 100, 50));
        // SQLite: rows only on 12-10 — 300 + 500 tokens.
        CreateLogsDatabase(Path.Combine(codexHome, "logs_1.sqlite"),
            (2029, 12, 10, 8, 0, 300),
            (2029, 12, 10, 21, 0, 500));

        var history = new CodexLogUsageScanner(localTimeZone: Utc)
            .Scan(codexHome, new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, history.Points.Count);
        // 12-11 keeps its JSONL record; 12-10 is counted from the SQL rows only (300 + 500), never
        // from the overlapping JSONL records (150 + 300).
        Assert.Equal(150, history.Points.Single(p => p.Date.Day == 11).Tokens);
        Assert.Equal(800, history.Points.Single(p => p.Date.Day == 10).Tokens);
        Assert.Equal(950, history.TotalTokens);
    }

    /// <summary>
    /// The source-preference rule runs at merge time, so a scan served from the cached history
    /// index must produce the same day totals as a fresh parse of the same underlying data.
    /// </summary>
    [Fact]
    public void CodexSourceDedupStaysConsistentAcrossCachedAndFreshScans()
    {
        var codexHome = Path.Combine(_tempDirectory, "codex-dedup-cache");
        var store = Path.Combine(_tempDirectory, "codex-dedup-store");
        Directory.CreateDirectory(codexHome);
        var path = Path.Combine(codexHome, "session.jsonl");
        WriteFile(path,
            CodexLastLine("2029-12-10T09:00:00Z", 100, 50) +
            CodexLastLine("2029-12-11T09:00:00Z", 100, 50));
        CreateLogsDatabase(Path.Combine(codexHome, "logs_1.sqlite"),
            (2029, 12, 10, 8, 0, 300));

        var scanner = new CodexLogUsageScanner(localTimeZone: Utc);
        var since = new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero);
        var fresh = scanner.Scan(codexHome, since, store, null);
        // Second scan reuses the cached JSONL contribution; the merge-time source rule must drop
        // the covered day's JSONL points the same way the fresh parse did.
        var cached = scanner.Scan(codexHome, since, store, null);

        Assert.Equal(fresh.TotalTokens, cached.TotalTokens);
        Assert.Equal(fresh.Points.OrderBy(p => p.Date).Select(p => (p.Date, p.Tokens)),
            cached.Points.OrderBy(p => p.Date).Select(p => (p.Date, p.Tokens)));
        Assert.Equal(450, cached.TotalTokens); // 150 (12-11 JSONL) + 300 (12-10 SQL)
    }

    /// <summary>
    /// A days-without-SQL-rows database state must keep the pre-diagnostics behavior: JSONL records
    /// are the only source, and a logs database with no usable rows never suppresses them.
    /// </summary>
    [Fact]
    public void CodexEmptyLogsDatabaseLeavesJsonlRecordsIntact()
    {
        var codexHome = Path.Combine(_tempDirectory, "codex-dedup-empty");
        Directory.CreateDirectory(codexHome);
        var path = Path.Combine(codexHome, "session.jsonl");
        WriteFile(path, CodexLastLine("2029-12-10T09:00:00Z", 100, 50));
        // A logs database that exists but has no matching rows must not suppress the JSONL day.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(codexHome, "logs_1.sqlite"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE logs (ts INTEGER NOT NULL, target TEXT NOT NULL, feedback_log_body TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        var history = new CodexLogUsageScanner(localTimeZone: Utc)
            .Scan(codexHome, new DateTimeOffset(2029, 12, 10, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(150, history.TotalTokens);
    }

    /// <summary>
    /// NaN/Infinity token fields (string-typed "NaN"/"1e999") and negative components must never
    /// poison day totals: non-finite records are dropped and negatives are clamped to zero.
    /// </summary>
    [Fact]
    public void CodexScanDropsNonFiniteAndClampsNegativeRecords()
    {
        const string lines = """
            {"timestamp":"2029-12-10T09:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":"NaN","output_tokens":10,"total_tokens":10}}}}
            {"timestamp":"2029-12-10T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"cached_input_tokens":-5,"output_tokens":5,"total_tokens":15}}}}
            {"timestamp":"2029-12-10T11:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":50,"total_tokens":150}}}}
            """;
        var files = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\sessions\\s1.jsonl"] = lines });

        var history = new CodexLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 12, 9, 12, 0, 0, TimeSpan.Zero));

        var point = Assert.Single(history.Points);
        Assert.True(double.IsFinite(point.Tokens) && double.IsFinite(point.CostUsd), $"finite totals, got {point.Tokens}/{point.CostUsd}");
        // NaN input clamps to 0 and the known output is kept (10), negative cached clamps to 0 (15),
        // healthy line counted (150).
        Assert.Equal(175, point.Tokens);
        Assert.Equal(110, history.Breakdown.Single().UncachedInputTokens, 9);
    }

    /// <summary>
    /// Sub-second string timestamps are preserved so two distinct records in the same second with
    /// identical counts are not collapsed by the line-based dedupe fallback key.
    /// </summary>
    [Fact]
    public void ClaudeKeepsSubSecondTimestampsDistinctForLineDedupe()
    {
        // No message id and no request id: the line-based dedupe fallback key includes the timestamp.
        const string line = """{"timestamp":"__T__","message":{"model":"claude-sonnet","usage":{"input_tokens":50,"output_tokens":25}}}""";
        var files = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] =
                line.Replace("__T__", "2029-12-10T11:00:00.250Z", StringComparison.Ordinal) + "\n" +
                line.Replace("__T__", "2029-12-10T11:00:00.750Z", StringComparison.Ordinal)
        });

        var history = new ClaudeLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 12, 9, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(150, history.Points.Single().Tokens);
    }

    /// <summary>
    /// Advisor iterations carry their own tokens but must never inherit the record root's reported
    /// cost: the root costUSD belongs to the parent turn alone. Inheriting it priced every advisor
    /// entry at the parent's full message cost.
    /// </summary>
    [Fact]
    public void ClaudeAdvisorCostIsNeverInheritedFromTheParentRecord()
    {
        const string line = """{"timestamp":"2029-12-10T11:00:00Z","requestId":"r1","costUSD":2.5,"message":{"id":"m1","model":"claude-sonnet","usage":{"input_tokens":100,"output_tokens":50,"iterations":[{"type":"advisor_message","model":"claude-haiku","input_tokens":20,"output_tokens":10}]}}}""";
        var files = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] = line
        });
        var catalog = new FixedModelCatalog("claude-haiku", new ModelPrice(3, 0.3, 15));
        var haikuEstimate = new ModelPrice(3, 0.3, 15).Estimate(20, 0, 10, 0);

        var history = new ClaudeLogUsageScanner(files, catalog, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 12, 9, 12, 0, 0, TimeSpan.Zero));

        var point = Assert.Single(history.Points);
        Assert.Equal(180, point.Tokens);
        // Parent priced at the reported cost; the advisor at its own catalog estimate, never at
        // the parent's reported cost.
        Assert.Equal(2.5 + haikuEstimate, point.CostUsd, 9);
        var sonnet = history.Breakdown.Single(b => b.ModelId == "claude-sonnet");
        Assert.Equal(UsageCostBasis.ProviderReported, sonnet.CostBasis);
        Assert.Equal(2.5, sonnet.CostUsd, 9);
        var haiku = history.Breakdown.Single(b => b.ModelId == "claude-haiku");
        Assert.Equal(UsageCostBasis.CatalogEstimated, haiku.CostBasis);
        Assert.Equal(haikuEstimate, haiku.CostUsd, 9);
    }

    /// <summary>
    /// When a token-equal duplicate replaces a slot (reported-cost line wins), the replaced
    /// entry's request-id pointer must not keep swallowing a later, genuinely distinct message
    /// that happens to reuse that request id.
    /// </summary>
    [Fact]
    public void ClaudeDedupeKeepsDistinctMessagesAfterRequestSlotReplacement()
    {
        // A: m1/r1, estimate; B: m1/r2 with reported cost replaces A (token-equal, reported wins);
        // C: m2/r1 is a distinct message that must survive the stale r1 pointer.
        var a = """{"timestamp":"2029-12-10T11:00:00Z","requestId":"r1","message":{"id":"m1","model":"claude-sonnet","usage":{"input_tokens":100,"output_tokens":50}}}""";
        var b = """{"timestamp":"2029-12-10T11:00:01Z","requestId":"r2","costUSD":0.99,"message":{"id":"m1","model":"claude-sonnet","usage":{"input_tokens":100,"output_tokens":50}}}""";
        var c = """{"timestamp":"2029-12-10T11:00:02Z","requestId":"r1","message":{"id":"m2","model":"claude-sonnet","usage":{"input_tokens":100,"output_tokens":50}}}""";
        var files = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] = a + "\n" + b + "\n" + c
        });

        var history = new ClaudeLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 12, 9, 12, 0, 0, TimeSpan.Zero));

        // m1 (reported) + m2 (estimated): both must be counted.
        Assert.Equal(300, history.Points.Single().Tokens);
        Assert.Equal(0.99, history.Breakdown.Single(b => b.CostBasis == UsageCostBasis.ProviderReported).CostUsd, 9);
        Assert.Equal(2, history.Breakdown.Count);
    }

    /// <summary>Antigravity's 30-day window is day-granular like the ring and breakdown periods.</summary>
    [Fact]
    public void AntigravityIncludesTheFullBoundaryDay()
    {
        var home = Path.Combine(_tempDirectory, ".gemini", "antigravity-cli", "conversations");
        Directory.CreateDirectory(home);
        // now = 2030-01-01T12:00Z; boundary day = 2030-01-01 - 29 days = 2029-12-03.
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        CreateConversationDatabase(Path.Combine(home, "c1.db"),
            (2029, 12, 3, 8, 0, 100),   // boundary day morning, before the now-29d instant
            (2029, 12, 3, 20, 0, 50));  // boundary day evening, after the instant

        var history = new AntigravityCliUsageScanner(userProfile: () => _tempDirectory, localTimeZone: Utc)
            .Scan(now);

        var point = Assert.Single(history.Points);
        Assert.Equal(new DateOnly(2029, 12, 3), point.Date);
        Assert.Equal(150, point.Tokens);
    }

    /// <summary>
    /// Step rows pair with gen_metadata rows in raw idx order, and NULL-payload steps advance the
    /// pairing too. A step without a payload still consumed a generation slot, so skipping it
    /// would shift every later model attribution by one.
    /// </summary>
    [Fact]
    public void AntigravityAttributesModelsInStepOrderIncludingNullPayloads()
    {
        var home = Path.Combine(_tempDirectory, ".gemini", "antigravity-cli", "conversations");
        Directory.CreateDirectory(home);
        // now = 2030-01-01T12:00Z; window starts 2029-12-03. idx0 is pre-window.
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        CreateConversationDatabaseWithModels(Path.Combine(home, "c1.db"),
            (2029, 12, 2, 8, 0, 100, "gemini-2.5-flash"),  // pre-window step
            (2029, 12, 2, 9, 0, null, "claude-sonnet-4.5"), // NULL payload, still advances pairing
            (2029, 12, 3, 9, 0, 100, "gpt-5.1"),
            (2029, 12, 3, 10, 0, 50, "gemini-2.5-pro"));

        var history = new AntigravityCliUsageScanner(userProfile: () => _tempDirectory, localTimeZone: Utc)
            .Scan(now);

        var breakdown = history.Breakdown
            .GroupBy(point => point.ModelId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(point => point.OutputTokens), StringComparer.OrdinalIgnoreCase);
        // The NULL-payload step at idx1 must not steal idx2's generation slot.
        Assert.Equal(100, breakdown["gpt-5.1"]);
        Assert.Equal(50, breakdown["gemini-2.5-pro"]);
        Assert.False(breakdown.ContainsKey("claude-sonnet-4.5"));
    }

    /// <summary>
    /// The agy CLI reads conversations from `.gemini/antigravity/conversations` on current Windows
    /// builds (observed in the CLI's own log); older layouts keep them under
    /// `.gemini/antigravity-cli/conversations`. The scanner must find data in either layout.
    /// </summary>
    [Fact]
    public void AntigravityFindsDatabasesInTheCurrentCliLayout()
    {
        var home = Path.Combine(_tempDirectory, ".gemini", "antigravity", "conversations");
        Directory.CreateDirectory(home);
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        CreateConversationDatabase(Path.Combine(home, "c1.db"), (2029, 12, 10, 9, 0, 100));

        var history = new AntigravityCliUsageScanner(userProfile: () => _tempDirectory, localTimeZone: Utc)
            .Scan(now);

        Assert.Equal(100, history.TotalTokens);
    }

    /// <summary>
    /// When both CLI layouts contain databases, the current layout wins and the legacy layout is
    /// not scanned: conversation rows carry no message identity, so counting two layouts could
    /// double-count the same sessions.
    /// </summary>
    [Fact]
    public void AntigravityPrefersTheCurrentLayoutWhenBothHaveDatabases()
    {
        var current = Path.Combine(_tempDirectory, ".gemini", "antigravity", "conversations");
        var legacy = Path.Combine(_tempDirectory, ".gemini", "antigravity-cli", "conversations");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(legacy);
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        CreateConversationDatabase(Path.Combine(current, "c1.db"), (2029, 12, 10, 9, 0, 100));
        CreateConversationDatabase(Path.Combine(legacy, "c1.db"), (2029, 12, 10, 9, 0, 50));

        var history = new AntigravityCliUsageScanner(userProfile: () => _tempDirectory, localTimeZone: Utc)
            .Scan(now);

        // Only the current layout's database is read: 100, never 150.
        Assert.Equal(100, history.TotalTokens);
    }

    /// <summary>
    /// The SQL-side local-midnight cutoff must stay on the boundary day even when DST shifts at
    /// local midnight: America/Havana springs forward at 00:00 (midnight never occurs) and falls
    /// back at 01:00 (midnight occurs twice).
    /// </summary>
    [Fact]
    public void UtcSecondsAtLocalMidnightHandlesMidnightDstTransitions()
    {
        var havana = TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
        // Spring-forward at 00:00: the day's first instant is 01:00 local (05:00Z).
        Assert.Equal(new DateTimeOffset(2025, 3, 9, 5, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            IncrementalHistoryScan.UtcSecondsAtLocalMidnight(new DateOnly(2025, 3, 9), havana));
        // Fall-back with midnight repeated: the earlier instant (04:00Z) keeps the whole day in-window.
        Assert.Equal(new DateTimeOffset(2025, 11, 2, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            IncrementalHistoryScan.UtcSecondsAtLocalMidnight(new DateOnly(2025, 11, 2), havana));
        // Plain day: ordinary local midnight.
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            IncrementalHistoryScan.UtcSecondsAtLocalMidnight(new DateOnly(2025, 6, 1), havana));

        // Cutoff is the first instant of the boundary day; one second earlier is still yesterday.
        foreach (var day in new[] { new DateOnly(2025, 3, 9), new DateOnly(2025, 11, 2), new DateOnly(2025, 6, 1) })
        {
            var cutoff = IncrementalHistoryScan.UtcSecondsAtLocalMidnight(day, havana);
            Assert.Equal(day, IncrementalHistoryScan.DayOf(DateTimeOffset.FromUnixTimeSeconds(cutoff), havana));
            Assert.Equal(day.AddDays(-1), IncrementalHistoryScan.DayOf(DateTimeOffset.FromUnixTimeSeconds(cutoff - 1), havana));
        }
    }

    // ------------------------------------------------------------------
    // Timezone bucketing matrix (Bug 1 regression: deterministic local days)
    // ------------------------------------------------------------------

    public static TheoryData<string, string, string> TimezoneBucketingCases => new()
    {
        // record timestamp, injected timezone, expected local day
        { "2029-07-15T22:30:00Z", "utc", "2029-07-15" },
        { "2029-07-15T22:30:00Z", "pacific", "2029-07-15" },
        { "2029-07-15T22:30:00Z", "kathmandu", "2029-07-16" },
        { "2029-07-15T01:00:00Z", "utc", "2029-07-15" },
        { "2029-07-15T01:00:00Z", "pacific", "2029-07-14" },
        { "2029-07-15T01:00:00Z", "kathmandu", "2029-07-15" },
        { "2029-12-31T23:59:59Z", "pacific", "2029-12-31" },
        { "2030-01-01T00:00:01Z", "pacific", "2029-12-31" },
        // offset-less strings are assumed UTC (RFC3339 with an explicit offset is preferred, but
        // some fixtures omit it; UTC is the documented assumption)
        { "2029-07-15T22:30:00", "utc", "2029-07-15" },
        // explicit non-UTC offset normalizes before bucketing
        { "2029-07-15T22:30:00+05:30", "utc", "2029-07-15" },
        { "2029-07-15T22:30:00+05:30", "kathmandu", "2029-07-15" },
        { "2029-07-15T23:30:00+05:30", "kathmandu", "2029-07-15" },
        { "2029-07-16T00:15:00+05:30", "kathmandu", "2029-07-16" }
    };

    [Theory]
    [MemberData(nameof(TimezoneBucketingCases))]
    public void ClaudeBucketsByInjectedTimezone(string timestamp, string timezone, string expectedDay)
    {
        var zone = timezone switch { "pacific" => Pacific, "kathmandu" => Kathmandu, _ => Utc };
        var files = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] = ClaudeLine(timestamp, 100, 50)
        });

        var history = new ClaudeLogUsageScanner(files, localTimeZone: zone)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var point = Assert.Single(history.Points);
        Assert.Equal(DateOnly.Parse(expectedDay), point.Date);
    }

    /// <summary>Instants around both DST transitions bucket by the zone's wall clock, exactly like ConvertTime.</summary>
    [Theory]
    [InlineData("2029-03-11T09:30:00Z", "2029-03-11")] // 01:30 PST, before spring-forward
    [InlineData("2029-03-11T10:30:00Z", "2029-03-11")] // 03:30 PDT, after spring-forward
    [InlineData("2029-03-11T11:00:00Z", "2029-03-11")] // 04:00 PDT
    [InlineData("2029-11-04T08:30:00Z", "2029-11-04")] // 01:30 PDT, ambiguous fall-back hour
    [InlineData("2029-11-04T09:30:00Z", "2029-11-04")] // 01:30 PST, repeated hour
    [InlineData("2029-11-04T10:30:00Z", "2029-11-04")] // 02:30 PST, after fall-back
    [InlineData("2029-06-01T20:30:00Z", "2029-06-01")] // mid-summer PDT control
    [InlineData("2029-01-15T20:30:00Z", "2029-01-15")] // mid-winter PST control
    public void CodexDstBucketsMatchTheInjectedZone(string timestamp, string expectedDay)
    {
        var instant = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture);
        var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, DstZone).Date);
        Assert.Equal(DateOnly.Parse(expectedDay), expected); // sanity: the fixture itself is right

        var files = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\sessions\\s1.jsonl"] = CodexLastLine(timestamp, 100, 50)
        });

        var history = new CodexLogUsageScanner(files, localTimeZone: DstZone)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var point = Assert.Single(history.Points);
        Assert.Equal(expected, point.Date);
    }

    /// <summary>Midnight, month, year, and leap-day boundaries all produce distinct calendar days.</summary>
    [Fact]
    public void CalendarBoundariesProduceDistinctDays()
    {
        const string lines = """
            {"timestamp":"2028-02-29T12:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":50,"total_tokens":150}}}}
            {"timestamp":"2028-03-01T00:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":50,"total_tokens":150}}}}
            {"timestamp":"2028-03-31T23:59:59Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":50,"total_tokens":150}}}}
            {"timestamp":"2028-04-01T00:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":50,"total_tokens":150}}}}
            {"timestamp":"2028-12-31T23:59:59Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":50,"total_tokens":150}}}}
            {"timestamp":"2029-01-01T00:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":50,"total_tokens":150}}}}
            """;
        var files = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\sessions\\s1.jsonl"] = lines });

        var history = new CodexLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[]
        {
            new DateOnly(2028, 2, 29), new DateOnly(2028, 3, 1),
            new DateOnly(2028, 3, 31), new DateOnly(2028, 4, 1),
            new DateOnly(2028, 12, 31), new DateOnly(2029, 1, 1)
        }, history.Points.Select(p => p.Date).ToArray());
        Assert.All(history.Points, point => Assert.Equal(150, point.Tokens));
    }

    // ------------------------------------------------------------------
    // ProviderJson timestamp parsing matrix
    // ------------------------------------------------------------------

    public static TheoryData<string, string> TimestampParsingCases => new()
    {
        { "2029-12-10T14:30:00Z", "2029-12-10T14:30:00+00:00" },
        // Fractional seconds are preserved by the parser, not truncated (the .000 case below
        // collapses only because the fraction is literally zero).
        { "2029-12-10T14:30:00.1234567Z", "2029-12-10T14:30:00.1234567+00:00" },
        { "2029-12-10T14:30:00+05:30", "2029-12-10T09:00:00+00:00" },
        { "2029-12-10T14:30:00-07:00", "2029-12-10T21:30:00+00:00" },
        { "2029-12-10T14:30:00", "2029-12-10T14:30:00+00:00" }, // no offset: assumed UTC
        { "2029-12-10", "2029-12-10T00:00:00+00:00" },
        { "2030-01-01T00:00:00.000Z", "2030-01-01T00:00:00+00:00" }
    };

    [Theory]
    [MemberData(nameof(TimestampParsingCases))]
    public void ProviderJsonParsesIsoTimestamps(string input, string expected)
    {
        using var document = JsonDocument.Parse($"\"{input}\"");
        var parsed = ProviderJson.Date(document.RootElement);
        Assert.Equal(DateTimeOffset.Parse(expected), parsed);
    }

    [Theory]
    [InlineData(1_891_605_420, "2029-12-10T13:57:00+00:00")]  // epoch seconds
    [InlineData(1_891_605_420_000, "2029-12-10T13:57:00+00:00")] // epoch milliseconds
    [InlineData(-86_400, "1969-12-31T00:00:00+00:00")]           // pre-epoch
    public void ProviderJsonParsesNumericEpochTimestamps(double input, string expected)
    {
        using var document = JsonDocument.Parse(input.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var parsed = ProviderJson.Date(document.RootElement);
        Assert.Equal(DateTimeOffset.Parse(expected), parsed);
    }

    [Theory]
    [InlineData("\"not-a-timestamp\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("{}")]
    public void ProviderJsonRejectsInvalidTimestamps(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(ProviderJson.Date(document.RootElement));
    }

    // ------------------------------------------------------------------
    // Deduplication regressions
    // ------------------------------------------------------------------

    /// <summary>Token-equal duplicates prefer the line carrying a provider-reported cost over a local estimate.</summary>
    [Fact]
    public void ClaudeDedupePrefersReportedCostOverEstimateOnTokenEqualDuplicates()
    {
        const string lines = """
            {"timestamp":"2029-12-10T11:00:00Z","requestId":"r1","message":{"id":"m1","model":"claude-sonnet","usage":{"input_tokens":800,"output_tokens":400}}}
            {"timestamp":"2029-12-10T11:00:01Z","requestId":"r1","costUSD":2.5,"message":{"id":"m1","model":"claude-sonnet","usage":{"input_tokens":800,"output_tokens":400}}}
            """;
        var files = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\projects\\session.jsonl"] = lines });

        var history = new ClaudeLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var point = Assert.Single(history.Points);
        Assert.Equal(1200, point.Tokens);
        Assert.Equal(2.5, point.CostUsd, 6);
        var breakdown = Assert.Single(history.Breakdown);
        Assert.Equal(UsageCostBasis.ProviderReported, breakdown.CostBasis);
    }

    /// <summary>A repeated identical line counts once; a higher-token replacement wins.</summary>
    [Fact]
    public void ClaudeDedupeCountsExactDuplicatesOnce()
    {
        var line = ClaudeLine("2029-12-10T11:00:00Z", 800, 400);
        var files = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] = line + line
        });

        var history = new ClaudeLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1200, history.Points.Single().Tokens);
    }

    /// <summary>Malformed, empty, and usage-less lines are skipped without dropping valid neighbors.</summary>
    [Fact]
    public void MixedQualityLinesKeepOnlyValidRecords()
    {
        const string lines = """
            {"timestamp":"2029-12-10T11:00:00Z","message":{"model":"claude-sonnet","usage":{"input_tokens":800,"output_tokens":400}}}
            not json at all
            {"timestamp":"2029-12-10T11:00:01Z","message":{"model":"claude-sonnet","usage":{"input_tokens":-5,"output_tokens":400}}}
            {"timestamp":"2029-12-10T11:00:02Z","message":{"model":"claude-sonnet"}}
            {"timestamp":"2029-12-10T11:00:03Z","message":{"model":"claude-sonnet","usage":{"input_tokens":200,"output_tokens":100}}}
            """;
        var files = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\projects\\session.jsonl"] = lines });

        var history = new ClaudeLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1500, history.Points.Single().Tokens);
    }

    /// <summary>Messages persisted in two discovered databases are counted once per scan.</summary>
    [Fact]
    public async Task OpenCodeDeduplicatesOverlappingDatabasesByMessageId()
    {
        var root = Path.Combine(_tempDirectory, "opencode-multi");
        Directory.CreateDirectory(root);
        // Real OpenCode message tables keep the id in the row's own id column, not inside data.
        var data = """{"role":"assistant","providerID":"opencode-go","modelID":"kimi-k3","cost":1.25,"tokens":{"input":500,"output":250,"total":750}}""";
        var timestamp = new DateTimeOffset(2029, 12, 10, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var first = Path.Combine(root, "opencode.db");
        var second = Path.Combine(root, "opencode-prod.db");
        CreateSingleMessageDatabase(first, timestamp, data);
        CreateSingleMessageDatabase(second, timestamp, data);

        var scanner = new OpenCodeUsageScanner(
            // The repeated path exercises the scanner's own Distinct; the second copy exercises
            // cross-database message-id dedupe.
            databaseLocator: new FixedLocator([first, second, second]),
            localTimeZone: Utc);
        var snapshot = await new OpenCodeProvider(scanner).RefreshAsync(new ProviderContext
        {
            Now = new DateTimeOffset(2029, 12, 11, 12, 0, 0, TimeSpan.Zero)
        });

        Assert.Null(snapshot.ErrorCategory);
        // One logical message in two database copies: counted once (750), never twice (1500).
        Assert.Equal(750, snapshot.UsageHistory!.TotalTokens);
        // kimi-k3 is catalog-priced: 500 input + 250 output at 3/15 per million.
        Assert.Equal(500 / 1_000_000d * 3 + 250 / 1_000_000d * 15, snapshot.UsageHistory.TotalCostUsd, 12);
    }

    // ------------------------------------------------------------------
    // Pricing and aggregation invariants
    // ------------------------------------------------------------------

    /// <summary>Unknown models keep tokens with an unpriced cost and land in UnknownModels.</summary>
    [Fact]
    public void UnknownModelKeepsTokensAndReportsUnpriced()
    {
        var files = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] = ClaudeLine("2029-12-10T11:00:00Z", 500, 250, model: "totally-new-model-9000")
        });

        var history = new ClaudeLogUsageScanner(files, localTimeZone: Utc)
            .Scan("C:\\fixture", new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(750, history.Points.Single().Tokens);
        Assert.Equal(0, history.Points.Single().CostUsd);
        Assert.Equal(["totally-new-model-9000"], history.UnknownModels);
        Assert.Equal(UsageCostBasis.Unpriced, history.Breakdown.Single().CostBasis);
    }

    /// <summary>Point totals equal the sum of breakdown components across providers and files.</summary>
    [Fact]
    public void TotalsEqualBreakdownSumAcrossMixedSources()
    {
        var files = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] =
                ClaudeLine("2029-12-10T11:00:00Z", 800, 400, cached: 200) + "\n" +
                ClaudeLine("2029-12-10T12:00:00Z", 100, 50),
            ["C:\\fixture\\sessions\\s1.jsonl"] =
                CodexLastLine("2029-12-10T13:00:00Z", 300, 100, cached: 30)
        });

        var claude = new ClaudeLogUsageScanner(files, localTimeZone: Utc).Scan("C:\\fixture", new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var codex = new CodexLogUsageScanner(files, localTimeZone: Utc).Scan("C:\\fixture", new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero));

        foreach (var history in new[] { claude, codex })
        {
            Assert.Equal(history.TotalTokens, history.Breakdown.Sum(point => point.ProcessedTokens));
            Assert.Equal(history.TotalCostUsd, history.Breakdown.Sum(point => point.CostUsd), 9);
        }
    }

    /// <summary>Exact catalog estimate for the deepseek-v4-flash shape used by real OpenCode rows.</summary>
    [Fact]
    public void CatalogEstimateMatchesExactPerMillionRates()
    {
        var pricing = new ModelPrice(.14, .0028, .28);
        // input 1104, cacheRead 23680, output 104, reasoning 193 (real row shape)
        var cost = pricing.Estimate(1104, 23680, 104 + 193);
        Assert.Equal(1104 / 1_000_000d * .14 + 23680 / 1_000_000d * .0028 + 297 / 1_000_000d * .28, cost, 12);
        Assert.Equal(0.000304024, cost, 9);
        Assert.Equal(23680 / 1_000_000d * (.14 - .0028),
            pricing.Estimate(0, 0, 0) + 0 + 23680 / 1_000_000d * (.14 - .0028), 12);
    }

    // ------------------------------------------------------------------
    // Fixtures and helpers
    // ------------------------------------------------------------------

    private static string ClaudeLine(string timestamp, long input, long output, long cached = 0, string model = "claude-sonnet") =>
        $"{{\"timestamp\":\"{timestamp}\",\"message\":{{\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{input},\"cache_read_input_tokens\":{cached},\"output_tokens\":{output}}}}}}}\n";

    private static string CodexLastLine(string timestamp, long input, long output, long cached = 0) =>
        $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"input_tokens\":{input},\"cached_input_tokens\":{cached},\"output_tokens\":{output},\"total_tokens\":{input + output}}}}}}}}}\n";

    private void WriteFile(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private static void CreateLogsDatabase(string path, params (int Year, int Month, int Day, int Hour, int Minute, long Tokens)[] rows)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE logs (ts INTEGER NOT NULL, target TEXT NOT NULL, feedback_log_body TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }
        foreach (var (year, month, day, hour, minute, tokens) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO logs (ts, target, feedback_log_body) VALUES ($ts, 'codex_core::session::turn', $body);";
            insert.Parameters.AddWithValue("$ts", new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("$body", $"post sampling token usage total_usage_tokens={tokens} model=gpt-5");
            insert.ExecuteNonQuery();
        }
    }

    private static void CreateConversationDatabase(string path, params (int Year, int Month, int Day, int Hour, int Minute, long Tokens)[] rows)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE steps (idx INTEGER NOT NULL, step_payload BLOB NOT NULL);
                CREATE TABLE gen_metadata (idx INTEGER NOT NULL, data BLOB NOT NULL);
                """;
            create.ExecuteNonQuery();
        }
        var index = 0;
        foreach (var (year, month, day, hour, minute, tokens) in rows)
        {
            var payload = Message(5,
                Message(1, FieldVarint(1, (ulong)new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds())),
                Message(9, FieldVarint(9, (ulong)tokens)));
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO steps (idx, step_payload) VALUES ($idx, $payload);";
            insert.Parameters.AddWithValue("$idx", index++);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Conversation database with per-step model attribution. A null payload writes a NULL
    /// step_payload (still paired with its generation row); a null model writes no gen_metadata row.
    /// </summary>
    private static void CreateConversationDatabaseWithModels(string path,
        params (int Year, int Month, int Day, int Hour, int Minute, long? Tokens, string? Model)[] rows)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE steps (idx INTEGER NOT NULL, step_payload BLOB);
                CREATE TABLE gen_metadata (idx INTEGER NOT NULL, data BLOB NOT NULL);
                """;
            create.ExecuteNonQuery();
        }
        for (var index = 0; index < rows.Length; index++)
        {
            var (year, month, day, hour, minute, tokens, model) = rows[index];
            using var step = connection.CreateCommand();
            step.CommandText = "INSERT INTO steps (idx, step_payload) VALUES ($idx, $payload);";
            step.Parameters.AddWithValue("$idx", index);
            if (tokens is { } tokenCount)
            {
                var payload = Message(5,
                    Message(1, FieldVarint(1, (ulong)new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds())),
                    Message(9, FieldVarint(9, (ulong)tokenCount)));
                step.Parameters.AddWithValue("$payload", payload);
            }
            else
            {
                step.Parameters.AddWithValue("$payload", DBNull.Value);
            }
            step.ExecuteNonQuery();

            if (model is not null)
            {
                using var gen = connection.CreateCommand();
                gen.CommandText = "INSERT INTO gen_metadata (idx, data) VALUES ($idx, $data);";
                gen.Parameters.AddWithValue("$idx", index);
                gen.Parameters.AddWithValue("$data", System.Text.Encoding.UTF8.GetBytes(model));
                gen.ExecuteNonQuery();
            }
        }
    }

    private static void CreateSingleMessageDatabase(string path, long timestamp, string data)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-shared-1', 'session-fixture', $created, $updated, $data);";
        insert.Parameters.AddWithValue("$created", timestamp);
        insert.Parameters.AddWithValue("$updated", timestamp);
        insert.Parameters.AddWithValue("$data", data);
        insert.ExecuteNonQuery();
    }

    private static byte[] Message(params byte[][] fields) => fields.SelectMany(field => field).ToArray();

    private static byte[] Message(int field, params byte[][] children)
    {
        var data = children.SelectMany(child => child).ToArray();
        return [.. Varint((ulong)(field << 3 | 2)), .. Varint((ulong)data.Length), .. data];
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        } while (value != 0);
        return bytes.ToArray();
    }

    private static byte[] FieldVarint(int field, ulong value) => [.. Varint((ulong)(field << 3)), .. Varint(value)];

    private sealed class FixtureFileSystem : IProviderFileSystem
    {
        private readonly Dictionary<string, string> _files;

        public FixtureFileSystem(IReadOnlyDictionary<string, string> files)
            => _files = new(files, StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => _files.ContainsKey(path);
        public string? ReadAllText(string path) => _files.TryGetValue(path, out var value) ? value : null;
        public IEnumerable<string> EnumerateFiles(string root, string pattern, SearchOption searchOption) =>
            _files.Keys.Where(x => x.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FixedLocator(IReadOnlyList<string> paths) : IOpenCodeDatabaseLocator
    {
        public OpenCodeDatabaseDiscovery Discover() => new(paths, []);
    }

    private sealed class FixedModelCatalog(string modelId, ModelPrice price) : IModelCatalog
    {
        public Task<ModelPricingSnapshot> GetAsync(string? providerId = null, bool forceRefresh = false,
            CancellationToken cancellationToken = default) => Task.FromResult(new ModelPricingSnapshot([], DateTimeOffset.UtcNow, "test", false));

        public ModelPrice? ResolvePrice(string providerId, string requestedModelId) =>
            string.Equals(modelId, requestedModelId, StringComparison.OrdinalIgnoreCase) ? price : null;
    }
}
