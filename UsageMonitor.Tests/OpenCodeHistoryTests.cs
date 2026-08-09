using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsageMonitor.Core;
using UsageMonitor.Core.Providers.OpenCode;
using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class OpenCodeHistoryTests
{
    private static readonly TimeZoneInfo Pacific =
        TimeZoneInfo.CreateCustomTimeZone("Test Pacific", TimeSpan.FromHours(-7), "Test Pacific", "Test Pacific");

    [Fact]
    public async Task SessionResponsesSpanningMidnightUseEachResponseLocalDate()
    {
        var root = NewRoot();
        try
        {
            var now = Local(2026, 8, 8, 12);
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 23, 59), "opencode-go", "kimi-k3", 1.25, 1_000),
                Message(Local(2026, 8, 8, 0, 1), "opencode-go", "kimi-k3", 2.50, 2_000));

            var snapshot = await Refresh(database, now);

            Assert.Equal(1_000, Point(snapshot, new DateOnly(2026, 8, 7)).Tokens);
            Assert.Equal(2_000, Point(snapshot, new DateOnly(2026, 8, 8)).Tokens);
            Assert.Equal(3.75, snapshot.UsageHistory!.TotalCostUsd, 6);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task HistoricalUsageIsRecoveredWithoutAPreviouslyWrittenCache()
    {
        var root = NewRoot();
        try
        {
            var now = Local(2026, 8, 8, 12);
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 17), "opencode-go", "kimi-k3", .5, 5_295_933));

            var snapshot = await Refresh(database, now);

            Assert.Equal(5_295_933, Point(snapshot, new DateOnly(2026, 8, 7)).Tokens);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task UnknownModelAndProviderKeepTokensWhenCostIsUnpriced()
    {
        var root = NewRoot();
        try
        {
            var now = Local(2026, 8, 8, 12);
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 17), "mystery-provider", "unknown-model", null, 1_234,
                    input: 1_000, output: 200, reasoning: 34));

            var snapshot = await Refresh(database, now);
            var history = snapshot.UsageHistory!;
            var point = Point(snapshot, new DateOnly(2026, 8, 7));
            var breakdown = Assert.Single(history.Breakdown);

            Assert.Equal(1_234, point.Tokens);
            Assert.Equal(0, point.CostUsd);
            Assert.Contains("mystery-provider/unknown-model", history.UnknownModels);
            Assert.Equal("mystery-provider", breakdown.ProviderId);
            Assert.Equal("unknown-model", breakdown.ModelId);
            Assert.Equal(1_234, breakdown.ProcessedTokens);
            Assert.Equal(UsageCostBasis.Unpriced, breakdown.CostBasis);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task KimiK3PreservesProviderModelAndTokenBreakdown()
    {
        var root = NewRoot();
        try
        {
            var now = Local(2026, 8, 8, 12);
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 17, 59, 21), "opencode-go", "kimi-k3", .089403, 20_285,
                    input: 17_906, output: 114, reasoning: 2_265));

            var snapshot = await Refresh(database, now);
            var history = snapshot.UsageHistory!;
            var breakdown = Assert.Single(history.Breakdown);

            Assert.Equal(20_285, Point(snapshot, new DateOnly(2026, 8, 7)).Tokens);
            Assert.Equal("opencode-go", breakdown.ProviderId);
            Assert.Equal("kimi-k3", breakdown.ModelId);
            Assert.Equal(17_906, breakdown.UncachedInputTokens);
            Assert.Equal(114, breakdown.OutputTokens);
            Assert.Equal(2_265, breakdown.ReasoningTokens);
            Assert.Equal(.089403, breakdown.CostUsd, 6);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task MissingTotalIsDerivedFromTokenComponents()
    {
        var root = NewRoot();
        try
        {
            var now = Local(2026, 8, 8, 12);
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 17), "opencode", "free-model", 0, null,
                    input: 10, cacheRead: 20, cacheWrite: 30, output: 40, reasoning: 50));

            var snapshot = await Refresh(database, now);

            Assert.Equal(150, Point(snapshot, new DateOnly(2026, 8, 7)).Tokens);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ZeroProviderCostUsesTheModelCashRateAndCacheRates()
    {
        var root = NewRoot();
        try
        {
            var now = Local(2026, 8, 8, 12);
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 17), "opencode", "priced-model", 0, 15_000_000,
                    input: 1_000_000, cacheRead: 2_000_000, cacheWrite: 3_000_000,
                    output: 4_000_000, reasoning: 5_000_000));
            var catalog = new FixedModelCatalog("priced-model", new ModelPrice(2, .5, 4, 8));

            var snapshot = await Refresh(database, now, catalog);
            var point = Point(snapshot, new DateOnly(2026, 8, 7));
            var breakdown = Assert.Single(snapshot.UsageHistory!.Breakdown);

            Assert.Equal(63, point.CostUsd, 6);
            Assert.True(point.Estimated);
            Assert.Equal(UsageCostBasis.CatalogEstimated, breakdown.CostBasis);
            Assert.Equal(3, breakdown.CacheSavingsUsd, 6);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task NonzeroProviderCostTakesPrecedenceOverTheCatalogEstimate()
    {
        var root = NewRoot();
        try
        {
            var now = Local(2026, 8, 8, 12);
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 17), "opencode", "priced-model", 1.23, 1_000_000,
                    input: 1_000_000));

            var snapshot = await Refresh(database, now,
                new FixedModelCatalog("priced-model", new ModelPrice(100, 100, 100)));
            var breakdown = Assert.Single(snapshot.UsageHistory!.Breakdown);

            Assert.Equal(1.23, Point(snapshot, new DateOnly(2026, 8, 7)).CostUsd, 6);
            Assert.Equal(UsageCostBasis.ProviderReported, breakdown.CostBasis);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task HistoryRangesProjectFromTheSameNinetyDayRecords()
    {
        var root = NewRoot();
        try
        {
            var today = new DateOnly(2026, 8, 8);
            var now = Local(2026, 8, 8, 12);
            var messages = Enumerable.Range(0, 90)
                .Select(offset => Message(Local(2026, 8, 8, 12).AddDays(-offset),
                    "opencode", "range-model", 0, 100))
                .ToArray();
            var database = CreateDatabase(root, messages);
            var snapshot = await Refresh(database, now);
            var points = snapshot.UsageHistory!.Points;

            Assert.Equal(100, SumRange(points, today, 1));
            Assert.Equal(200, SumRange(points, today, 2));
            Assert.Equal(700, SumRange(points, today, 7));
            Assert.Equal(3_000, SumRange(points, today, 30));
            Assert.Equal(9_000, SumRange(points, today, 90));

            var apiSnapshot = ToApiSnapshot(snapshot);
            var todaySummary = SpendRingModel.Build([apiSnapshot], SpendRingPeriod.Today,
                SpendRingMetric.Tokens, today);
            var yesterdaySummary = SpendRingModel.Build([apiSnapshot], SpendRingPeriod.Yesterday,
                SpendRingMetric.Tokens, today);
            var thirtyDaySummary = SpendRingModel.Build([apiSnapshot], SpendRingPeriod.ThirtyDays,
                SpendRingMetric.Tokens, today);

            Assert.Equal(100, todaySummary.Total);
            Assert.Equal(100, yesterdaySummary.Total);
            Assert.Equal(3_000, thirtyDaySummary.Total);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void LocatorUsesActivePathAndDeduplicatesReleaseLocations()
    {
        var root = NewRoot();
        try
        {
            var activeDirectory = Path.Combine(root, "active");
            var profile = Path.Combine(root, "profile");
            var localAppData = Path.Combine(root, "local");
            var activePath = Path.Combine(activeDirectory, "opencode-prod.db");
            var activeSiblingPath = Path.Combine(activeDirectory, "opencode.db");
            var profilePath = Path.Combine(profile, ".local", "share", "opencode", "opencode.db");
            var localPath = Path.Combine(localAppData, "opencode", "opencode.db");
            foreach (var path in new[] { activePath, activeSiblingPath, profilePath, localPath })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
            }

            var locator = new OpenCodeDatabaseLocator(
                dataDirectoryOverride: () => null,
                xdgDataHome: () => Path.Combine(profile, ".local", "share"),
                userProfile: () => profile,
                localAppData: () => localAppData,
                activeDatabasePath: () => activePath);

            var discovery = locator.Discover();

            Assert.Equal(4, discovery.DatabasePaths.Count);
            Assert.Contains(Path.GetFullPath(activePath), discovery.DatabasePaths);
            Assert.Contains(Path.GetFullPath(activeSiblingPath), discovery.DatabasePaths);
            Assert.Contains(Path.GetFullPath(profilePath), discovery.DatabasePaths);
            Assert.Contains(Path.GetFullPath(localPath), discovery.DatabasePaths);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CurrentMessageSchemaIsUsedWithoutDependingOnSessionOrPartTables()
    {
        var root = NewRoot();
        try
        {
            var database = CreateDatabase(root, [
                Message(Local(2026, 8, 7, 17), "opencode", "schema-model", 0, 10)
            ],
                includeOtherTables: true);

            var snapshot = await Refresh(database, Local(2026, 8, 8, 12));

            Assert.Null(snapshot.ErrorCategory);
            Assert.Equal(10, snapshot.UsageHistory!.TotalTokens);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task UnsupportedMessageSchemaProducesParseError()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            var database = Path.Combine(root, "opencode.db");
            using var connection = Open(database);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE part (time_created INTEGER NOT NULL, data TEXT NOT NULL);";
            command.ExecuteNonQuery();

            var scanner = Scanner(database);
            var snapshot = await new OpenCodeProvider(scanner).RefreshAsync(new ProviderContext
            {
                Now = Local(2026, 8, 8, 12)
            });

            Assert.Equal(ProviderErrorCategory.Parse, snapshot.ErrorCategory);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ApiPreservesCorrectedDatesAndModelBreakdown()
    {
        var root = NewRoot();
        try
        {
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 17), "opencode-go", "kimi-k3", .5, 500, input: 500));
            var provider = new OpenCodeProvider(Scanner(database));
            var snapshot = await provider.RefreshAsync(new ProviderContext { Now = Local(2026, 8, 8, 12) });
            var source = new CoreUsageSnapshotSource(new UsageProviderCatalog([
                new FixedProvider(provider.Descriptor, snapshot)
            ]));

            var response = await new UsageApiService(source).HandleAsync("GET", "/v1/usage/opencode");
            using var document = JsonDocument.Parse(response.Body);
            var history = document.RootElement[0].GetProperty("usageHistory");

            Assert.Equal("2026-08-07", history.GetProperty("points")[0].GetProperty("date").GetString());
            Assert.Equal("kimi-k3", history.GetProperty("breakdown")[0].GetProperty("modelId").GetString());
            Assert.Equal(500, history.GetProperty("breakdown")[0].GetProperty("uncachedInputTokens").GetDouble());
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ForcedRefreshUsesDatabaseHistoryInsteadOfCachedSnapshot()
    {
        var root = NewRoot();
        var cacheRoot = NewRoot();
        try
        {
            var database = CreateDatabase(root,
                Message(Local(2026, 8, 7, 17), "opencode-go", "kimi-k3", .5, 500, input: 500));
            var provider = new OpenCodeProvider(Scanner(database));
            var corrected = await provider.RefreshAsync(new ProviderContext { Now = Local(2026, 8, 8, 12) });
            var stale = corrected with
            {
                UsageHistory = new ProviderUsageHistory([
                    new UsageHistoryPoint(new DateOnly(2026, 8, 8), 999, 999)
                ])
            };
            using var cache = new JsonFileUsageCache(cacheRoot);
            await cache.WriteAsync("provider:opencode", stale);
            var source = new CoreUsageSnapshotSource(new UsageProviderCatalog([
                provider
            ]), cache);

            var snapshots = await source.GetSnapshotsAsync("opencode", force: true);
            var refreshed = snapshots.Single();

            Assert.Equal(500, refreshed.UsageHistory!.Points.Single().Tokens);
            Assert.Equal(new DateOnly(2026, 8, 7), refreshed.UsageHistory.Points.Single().Date);
        }
        finally
        {
            DeleteRoot(root);
            DeleteRoot(cacheRoot);
        }
    }

    private static async Task<ProviderSnapshot> Refresh(string database, DateTimeOffset now,
        IModelCatalog? catalog = null)
    {
        var snapshot = await new OpenCodeProvider(Scanner(database), catalog).RefreshAsync(new ProviderContext { Now = now, ModelCatalog = catalog });
        Assert.Null(snapshot.ErrorCategory);
        return snapshot;
    }

    private static OpenCodeUsageScanner Scanner(string database, int historyDays = 90) =>
        new(databaseLocator: new FixedLocator([database]), localTimeZone: Pacific, historyDays: historyDays);

    private static UsageHistoryPoint Point(ProviderSnapshot snapshot, DateOnly date) =>
        Assert.Single(snapshot.UsageHistory!.Points, point => point.Date == date);

    private static double SumRange(IReadOnlyList<UsageHistoryPoint> points, DateOnly today, int days) =>
        points.Where(point => point.Date >= today.AddDays(-(days - 1)) && point.Date <= today)
            .Sum(point => point.Tokens);

    private static UsageSnapshotData ToApiSnapshot(ProviderSnapshot snapshot) =>
        new(snapshot.ProviderId, snapshot.DisplayName, snapshot.Plan, [], snapshot.RefreshedAt)
        {
            UsageHistory = new UsageHistoryData(snapshot.UsageHistory!.Points.Select(point =>
                new UsageHistoryPointData(point.Date, point.Tokens, point.CostUsd)))
        };

    private static MessageSpec Message(DateTimeOffset timestamp, string provider, string model,
        double? cost, double? total, double input = 0, double cacheRead = 0, double cacheWrite = 0,
        double output = 0, double reasoning = 0) =>
        new(timestamp, provider, model, cost, total, input, cacheRead, cacheWrite, output, reasoning);

    private static string CreateDatabase(string root, params MessageSpec[] messages)
        => CreateDatabase(root, messages, includeOtherTables: false);

    private static string CreateDatabase(string root, MessageSpec[] messages, bool includeOtherTables)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "opencode.db");
        using var connection = Open(path);
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE message (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    time_created INTEGER NOT NULL,
                    time_updated INTEGER NOT NULL,
                    data TEXT NOT NULL
                );
                """ + (includeOtherTables
                    ? "CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, time_created INTEGER NOT NULL, data TEXT NOT NULL); CREATE TABLE session_message (id TEXT PRIMARY KEY, time_created INTEGER NOT NULL, data TEXT NOT NULL);"
                    : string.Empty);
            create.ExecuteNonQuery();
        }

        foreach (var message in messages) AddMessage(connection, message);
        return path;
    }

    private static void AddMessage(SqliteConnection connection, MessageSpec message)
    {
        var tokens = new Dictionary<string, object?>
        {
            ["input"] = message.Input,
            ["output"] = message.Output,
            ["reasoning"] = message.Reasoning,
            ["cache"] = new { read = message.CacheRead, write = message.CacheWrite }
        };
        if (message.Total is { } total) tokens["total"] = total;
        var data = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["providerID"] = message.Provider,
            ["modelID"] = message.Model,
            ["cost"] = message.Cost,
            ["tokens"] = tokens
        };
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ($id, $session, $created, $updated, $data);";
        insert.Parameters.AddWithValue("$id", "msg-" + Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("$session", "session-fixture");
        insert.Parameters.AddWithValue("$created", message.Timestamp.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$updated", message.Timestamp.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$data", JsonSerializer.Serialize(data));
        insert.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.FromHours(-7));

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed record MessageSpec(DateTimeOffset Timestamp, string Provider, string Model,
        double? Cost, double? Total, double Input, double CacheRead, double CacheWrite,
        double Output, double Reasoning);

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

    private sealed class FixedProvider(ProviderDescriptor descriptor, ProviderSnapshot snapshot) : IUsageProvider
    {
        public ProviderDescriptor Descriptor => descriptor;
        public Task<ProviderSnapshot> RefreshAsync(ProviderContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
