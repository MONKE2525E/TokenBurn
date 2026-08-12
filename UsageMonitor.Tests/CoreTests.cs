using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using UsageMonitor.Cli;
using UsageMonitor.Core;
using UsageMonitor.Core.Providers;
using UsageMonitor.Core.Providers.OpenCode;
using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class CoreTests
{
    [Fact]
    public void LocalFileSystemDecodesOnlyMatchingJsonlRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // Put the marker across the 64 KiB scan-buffer boundary and make the first record
            // large enough to prove it is skipped without changing the result.
            File.WriteAllText(path,
                new string('x', 65_535) + "\n" +
                new string('y', 65_530) + "ToKeN_CoUnT" + new string('z', 40) + "\r\n" +
                "{\"usage\":true}\n");

            var lines = new LocalProviderFileSystem()
                .ReadLinesContaining(path, "token_count", "\"usage\"")
                .ToArray();

            Assert.Equal(2, lines.Length);
            Assert.Contains("ToKeN_CoUnT", lines[0], StringComparison.Ordinal);
            Assert.Equal("{\"usage\":true}", lines[1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MetricLineProgressCalculatesStateAndRoundTrips()
    {
        var line = MetricLine.Progress("Session", 80, 100, MetricKind.Percent,
            DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(5));
        Assert.Equal(MetricLineType.Progress, line.Type);
        Assert.Equal(MetricState.Warning, line.State);
        var json = JsonSerializer.Serialize(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var copy = JsonSerializer.Deserialize<MetricLine>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(copy);
        Assert.Equal(line.Label, copy!.Label);
        Assert.Equal(line.Used, copy.Used);
    }

    [Fact]
    public async Task CacheReturnsFreshValueWithoutRefreshing()
    {
        using var cache = new JsonFileUsageCache(Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N")));
        var called = 0;
        var result = await cache.GetAsync("fresh", _ =>
        {
            Interlocked.Increment(ref called);
            return Task.FromResult<string?>("value");
        });
        var second = await cache.GetAsync("fresh", _ =>
        {
            Interlocked.Increment(ref called);
            return Task.FromResult<string?>("new-value");
        });
        Assert.Equal("value", result.Value);
        Assert.Equal("value", second.Value);
        Assert.Equal(1, called);
        Assert.False(second.IsStale);
    }

    [Fact]
    public async Task ConcurrentColdMissesRefreshTheProviderOnce()
    {
        using var cache = new JsonFileUsageCache(Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N")));
        var called = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new Task<CacheReadResult<string?>>[6];

        for (var i = 0; i < results.Length; i++)
        {
            results[i] = cache.GetAsync<string?>("cold-race", async token =>
            {
                if (Interlocked.Increment(ref called) == 1)
                {
                    // Hold the winning refresh open so the losers all line up behind it.
                    await gate.Task.WaitAsync(token).ConfigureAwait(false);
                }
                return "winner-value";
            });
        }
        await Task.Delay(50);
        gate.SetResult();
        var completed = await Task.WhenAll(results);

        Assert.All(completed, result => Assert.Equal("winner-value", result.Value));
        Assert.Equal(1, called);
        Assert.Equal("winner-value", await cache.ReadAsync<string>("cold-race"));
    }

    [Fact]
    public async Task ConcurrentColdMissesShareAnErrorSnapshotWithoutRetrying()
    {
        using var cache = new JsonFileUsageCache(Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N")));
        var provider = new ProviderDescriptor("fixture", "Fixture");
        var called = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new Task<CacheReadResult<ProviderSnapshot?>>[4];

        for (var i = 0; i < results.Length; i++)
        {
            results[i] = cache.GetAsync<ProviderSnapshot?>("cold-error", async token =>
            {
                if (Interlocked.Increment(ref called) == 1)
                {
                    await gate.Task.WaitAsync(token).ConfigureAwait(false);
                }
                return ProviderSnapshot.Error(provider, "temporarily unavailable", ProviderErrorCategory.Authentication);
            });
        }
        await Task.Delay(50);
        gate.SetResult();
        var completed = await Task.WhenAll(results);

        Assert.All(completed, result =>
        {
            Assert.NotNull(result.Value);
            Assert.Equal(ProviderErrorCategory.Authentication, result.Value!.ErrorCategory);
        });
        Assert.Equal(1, called);
        // Error snapshots are observations, not cache values: nothing persisted.
        Assert.Null(await cache.ReadAsync<ProviderSnapshot>("cold-error"));
    }

    [Fact]
    public async Task WaiterCancellationDoesNotAbortTheSharedColdRefresh()
    {
        using var cache = new JsonFileUsageCache(Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N")));
        var called = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var winner = cache.GetAsync("cold-cancel", async token =>
        {
            Interlocked.Increment(ref called);
            await gate.Task.WaitAsync(token).ConfigureAwait(false);
            return "shared-value";
        });
        await Task.Delay(50);

        using var shortLived = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var waiter = cache.GetAsync("cold-cancel", async _ =>
        {
            Interlocked.Increment(ref called);
            return "waiter-should-never-refresh";
        }, shortLived.Token);
        var waiterResult = await waiter;
        Assert.Null(waiterResult.Value);

        gate.SetResult();
        var winnerResult = await winner;
        Assert.Equal("shared-value", winnerResult.Value);
        Assert.Equal(1, called);
    }

    [Fact]
    public async Task ConcurrentColdMissesWithNullResultRefreshOnlyOnce()
    {
        using var cache = new JsonFileUsageCache(Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N")));
        var called = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new Task<CacheReadResult<string?>>[4];

        for (var i = 0; i < results.Length; i++)
        {
            results[i] = cache.GetAsync<string?>("cold-null", async token =>
            {
                if (Interlocked.Increment(ref called) == 1)
                {
                    await gate.Task.WaitAsync(token).ConfigureAwait(false);
                }
                return null;
            });
        }
        await Task.Delay(50);
        gate.SetResult();
        var completed = await Task.WhenAll(results);

        Assert.All(completed, result => Assert.Null(result.Value));
        // A null outcome is shared like any other: waiters must not each re-run the provider.
        Assert.Equal(1, called);
    }

    [Fact]
    public async Task CacheReturnsStaleValueAndRefreshesInBackground()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        using var cache = new JsonFileUsageCache(root, TimeSpan.FromMilliseconds(1));
        await cache.WriteAsync("stale", "old", DateTimeOffset.UtcNow.AddMinutes(-1));
        var refreshed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = await cache.GetAsync("stale", _ => refreshed.Task);
        Assert.Equal("old", stale.Value);
        Assert.True(stale.IsStale);
        Assert.True(stale.RefreshStarted);
        refreshed.SetResult("new");
        for (var attempt = 0; attempt < 50 && await cache.ReadAsync<string>("stale") != "new"; attempt++)
            await Task.Delay(10);
        Assert.Equal("new", await cache.ReadAsync<string>("stale"));
    }

    [Fact]
    public async Task CacheDoesNotPersistProviderErrorsOverLastGoodValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        using var cache = new JsonFileUsageCache(root);
        var provider = new ProviderDescriptor("fixture", "Fixture");
        var calls = 0;

        var first = await cache.GetAsync("provider-error", _ =>
        {
            calls++;
            return Task.FromResult<ProviderSnapshot?>(ProviderSnapshot.Error(provider, "temporarily unavailable", ProviderErrorCategory.Network));
        });

        var second = await cache.GetAsync("provider-error", _ =>
        {
            calls++;
            return Task.FromResult<ProviderSnapshot?>(ProviderSnapshot.Error(provider, "still unavailable", ProviderErrorCategory.Network));
        });

        Assert.NotNull(first.Value);
        Assert.Null(await cache.ReadAsync<ProviderSnapshot>("provider-error"));
        Assert.NotNull(second.Value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ForcedRefreshKeepsCachedLimitsButPreservesAuthenticationFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        using var cache = new JsonFileUsageCache(root);
        var descriptor = new ProviderDescriptor(ProviderIds.ClaudeCode, "Claude Code");
        var lastGood = ProviderSnapshot.Success(descriptor,
            [MetricLine.Progress("Session", 78, 100, MetricKind.Percent, DateTimeOffset.UtcNow.AddHours(1))],
            "Pro", DateTimeOffset.UtcNow.AddMinutes(-1));
        await cache.WriteAsync("provider:claude-code", lastGood, lastGood.RefreshedAt);
        var cachedRoundTrip = await cache.ReadAsync<ProviderSnapshot>("provider:claude-code");
        Assert.NotNull(cachedRoundTrip);
        Assert.Contains(cachedRoundTrip!.Lines, line => line.Type == MetricLineType.Progress);

        var failure = ProviderSnapshot.Error(descriptor,
            "Claude Code is signed out on this Windows account. Run `claude auth login`, then refresh.",
            ProviderErrorCategory.NotConfigured);
        Assert.Equal(ProviderErrorCategory.NotConfigured, failure.ErrorCategory);
        var source = new CoreUsageSnapshotSource(
            new UsageProviderCatalog([new FixedProvider(descriptor, failure)]),
            cache);

        var snapshot = Assert.Single(await source.GetSnapshotsAsync(ProviderIds.ClaudeCode, force: true));

        Assert.Equal(ProviderIds.ClaudeCode, snapshot.ProviderId);
        Assert.NotEmpty(snapshot.Lines);
        Assert.Contains("signed out", snapshot.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signed out", snapshot.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth login", snapshot.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(78, snapshot.Lines.OfType<ProgressMetricData>().Single().Used);
    }

    [Fact]
    public async Task RecentAuthFailureSurfacesOnCachedReadKeepingLastGoodBars()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        using var cache = new JsonFileUsageCache(root);
        var descriptor = new ProviderDescriptor("antigravity", "Antigravity");
        var lastGood = ProviderSnapshot.Success(descriptor,
            [MetricLine.Progress("Session", 55, 100, MetricKind.Percent, DateTimeOffset.UtcNow.AddHours(1))],
            "Pro", DateTimeOffset.UtcNow);
        await cache.WriteAsync("provider:antigravity", lastGood, lastGood.RefreshedAt);

        var provider = new SwitchableProvider(descriptor, ProviderSnapshot.Error(descriptor,
            "Antigravity sign-in expired. Open Antigravity or Gemini CLI and sign in again.",
            ProviderErrorCategory.Authentication));
        var source = new CoreUsageSnapshotSource(new UsageProviderCatalog([provider]), cache);

        // A forced refresh records the auth failure and surfaces it while keeping the bars.
        var forced = Assert.Single(await source.GetSnapshotsAsync("antigravity", force: true));
        Assert.Equal("Authentication", forced.ErrorCategory);
        Assert.Equal(55, forced.Lines.OfType<ProgressMetricData>().Single().Used);

        // A later cached read still carries the failure over the last-good bars instead of
        // pretending the provider is healthy.
        var cached = Assert.Single(await source.GetSnapshotsAsync("antigravity", force: false));
        Assert.Equal("Authentication", cached.ErrorCategory);
        Assert.NotNull(cached.Warning);
        Assert.Equal(55, cached.Lines.OfType<ProgressMetricData>().Single().Used);
        Assert.Contains(cached.Lines.OfType<BadgeMetricData>(), badge =>
            badge.Label == "Error");

        // After the provider recovers, the failure is cleared and cached reads are healthy again.
        provider.Snapshot = ProviderSnapshot.Success(descriptor,
            [MetricLine.Progress("Session", 10, 100, MetricKind.Percent, DateTimeOffset.UtcNow.AddHours(1))],
            "Pro", DateTimeOffset.UtcNow);
        _ = Assert.Single(await source.GetSnapshotsAsync("antigravity", force: true));
        var recovered = Assert.Single(await source.GetSnapshotsAsync("antigravity", force: false));
        Assert.Null(recovered.Error);
        Assert.Equal(10, recovered.Lines.OfType<ProgressMetricData>().Single().Used);
    }

    [Fact]
    public void RedactorRemovesSecretsAndEmailAddresses()
    {
        var text = SensitiveDataRedactor.Redact("email developer@example.invalid token=abc123 account_id=acct_123 Authorization: Bearer xyz C:\\Users\\developer\\.codex");
        Assert.DoesNotContain("developer@example.invalid", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("xyz", text);
        Assert.DoesNotContain("acct_123", text);
        Assert.DoesNotContain("C:\\Users\\developer", text);
    }

    [Fact]
    public void PacingAndResetHelpersHandleBoundaries()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var reset = start.AddHours(4);
        var result = PacingCalculator.Calculate(50, 100, start, reset, start.AddHours(2));
        Assert.Equal(PacingStatus.OnTrack, result.Status);
        Assert.Equal(TimeSpan.FromHours(2), result.RemainingPeriod);
        Assert.Equal("2h 0m", ResetCalculator.FormatRemaining(reset, start.AddHours(2)));
        Assert.Equal("Resetting now", ResetCalculator.FormatRemaining(reset, reset.AddSeconds(1)));
    }

    [Fact]
    public void ProviderCatalogNormalizesLegacyDisplayNamesToCanonicalIds()
    {
        Assert.Equal(ProviderIds.Cursor, ProviderCatalog.NormalizeId("Cursor"));
        Assert.Equal(ProviderIds.Copilot, ProviderCatalog.NormalizeId("Copilot"));
        Assert.Equal(ProviderIds.Devin, ProviderCatalog.NormalizeId("Devin"));
        Assert.Equal(ProviderIds.Grok, ProviderCatalog.NormalizeId("Grok"));
        Assert.Equal(ProviderIds.OpenCode, ProviderCatalog.NormalizeId("OpenCode"));
        Assert.Equal(ProviderIds.ClaudeCode, ProviderCatalog.NormalizeId("Claude Code"));
    }

    [Fact]
    public void ResetFormatterHonorsCountdownAndExactTimeModes()
    {
        var reset = new DateTimeOffset(2026, 8, 6, 18, 30, 0, TimeSpan.Zero);
        Assert.StartsWith("resets in ", ResetTimeFormatter.Format(reset, "Countdown"));
        Assert.Contains("resets", ResetTimeFormatter.Format(reset, "Exact time"));
    }

    [Fact]
    public void ProgressWidthConverterFillsTheTrackByValue()
    {
        var converter = new ProgressWidthConverter();
        var width = converter.Convert(new object[] { 0.49d, 1d, 200d }, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(98d, width);
    }

    [Fact]
    public async Task DefaultCatalogUsesTruthfulNotConfiguredSnapshots()
    {
        var catalog = ProviderCatalog.CreateDefault();
        Assert.Equal(8, catalog.Providers.Count);
        var ids = catalog.Providers.Select(provider => provider.Descriptor.Id).ToArray();
        Assert.Contains(ProviderIds.Cursor, ids);
        Assert.Contains(ProviderIds.Copilot, ids);
        Assert.Contains(ProviderIds.Devin, ids);
        Assert.Contains(ProviderIds.Grok, ids);
        Assert.Contains(ProviderIds.OpenCode, ids);
        var snapshot = await catalog.Find(ProviderIds.Codex)!.RefreshAsync(new ProviderContext());
        Assert.Equal(ProviderErrorCategory.NotConfigured, snapshot.ErrorCategory);
        Assert.True(snapshot.Lines.Single().IsError);
    }

    [Fact]
    public async Task SecretStoreDefaultsNeverExposeMissingValues()
    {
        Assert.Null(await NullSecretStore.Instance.GetAsync("missing"));
        var store = new CredentialManagerSecretStore("UsageMonitorTests/");
        Assert.Null(await store.GetAsync("missing-secret"));
    }

    [Fact]
    public async Task CliDiagnoseIncludesOpenCodeAndExcludesApiBillingProviders()
    {
        using var output = new StringWriter();
        var exitCode = await CliApplication.RunAsync(["--diagnose"], output, new StringWriter());
        using var document = JsonDocument.Parse(output.ToString());
        var providers = document.RootElement.GetProperty("providers").EnumerateArray()
            .Select(item => item.GetString()).ToArray();

        Assert.Equal(CliApplication.Success, exitCode);
        Assert.Contains(ProviderIds.OpenCode, providers);
        Assert.DoesNotContain(ProviderIds.OpenRouter, providers);
        Assert.DoesNotContain(ProviderIds.Zai, providers);
    }

    [Fact]
    public async Task OpenCodeReadsWindowsLocalHistoryWithoutAnApiKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 8, 6, 18, 0, 0, TimeSpan.Zero);
            await File.WriteAllTextAsync(Path.Combine(root, "auth.json"), "{\"opencode-go\":{\"key\":\"fixture-key\"}}");
            var database = Path.Combine(root, "opencode.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE message (time_created INTEGER NOT NULL, data TEXT NOT NULL);";
                await create.ExecuteNonQueryAsync();
                await AddOpenCodeMessage(connection, now.AddHours(-1), "opencode-go", 1.25, 1_000);
                await AddOpenCodeMessage(connection, now.AddDays(-2), "opencode", 0.50, 500);
                await AddOpenCodeMessage(connection, now.AddDays(-92), "opencode", 99, 99);
            }

            var locator = new OpenCodeDatabaseLocator(
                dataDirectoryOverride: () => root,
                xdgDataHome: () => null,
                userProfile: () => root,
                localAppData: () => null,
                activeDatabasePath: () => null,
                includeDefaultLocations: false);
            var provider = new OpenCodeProvider(new OpenCodeUsageScanner(databaseLocator: locator));
            var snapshot = await provider.RefreshAsync(new ProviderContext { Now = now });

            Assert.Null(snapshot.ErrorCategory);
            Assert.Equal("Go", snapshot.Plan);
            Assert.Equal(1.75, snapshot.UsageHistory!.TotalCostUsd, precision: 4);
            Assert.Equal(1_500, snapshot.UsageHistory.TotalTokens);
            Assert.Equal(1.25, snapshot.GetLine("Session")!.Used!.Value, precision: 4);
            Assert.Equal(1.25, snapshot.GetLine("Weekly")!.Used!.Value, precision: 4);
            Assert.Equal(1.25, snapshot.GetLine("Monthly")!.Used!.Value, precision: 4);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AddOpenCodeMessage(SqliteConnection connection, DateTimeOffset timestamp,
        string providerId, double cost, int tokens)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO message (time_created, data) VALUES ($time, $data);";
        insert.Parameters.AddWithValue("$time", timestamp.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new
        {
            role = "assistant",
            providerID = providerId,
            cost,
            tokens = new { total = tokens }
        }));
        await insert.ExecuteNonQueryAsync();
    }

    internal sealed class FixedProvider(ProviderDescriptor descriptor, ProviderSnapshot snapshot) : IUsageProvider
    {
        public ProviderDescriptor Descriptor => descriptor;

        public Task<ProviderSnapshot> RefreshAsync(ProviderContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class SwitchableProvider(ProviderDescriptor descriptor, ProviderSnapshot initial) : IUsageProvider
    {
        public ProviderSnapshot Snapshot = initial;
        public ProviderDescriptor Descriptor => descriptor;

        public Task<ProviderSnapshot> RefreshAsync(ProviderContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }
}
