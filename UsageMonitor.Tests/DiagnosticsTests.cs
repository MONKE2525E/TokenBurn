using System.Text.Json;
using UsageMonitor.Cli;
using UsageMonitor.Core;
using UsageMonitor.Core.Providers;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void CorrelatingLoggerAppendsRefreshIdWithoutMutatingCallerData()
    {
        var inner = new InMemoryDiagnosticsLogger();
        var original = new Dictionary<string, object?> { ["providerId"] = "codex" };
        var correlated = new CorrelatingDiagnosticsLogger(inner, "r1234ab");

        correlated.Debug("d", original);
        correlated.Info("i", original);
        correlated.Warning("w", original);
        correlated.Error("e", original, new Exception("x"));

        Assert.DoesNotContain("refreshId", original);
        Assert.Equal(4, inner.Entries.Count);
        Assert.All(inner.Entries, entry =>
        {
            Assert.Equal("r1234ab", entry.Data!["refreshId"]);
            Assert.Equal("codex", entry.Data!["providerId"]);
        });
    }

    [Fact]
    public void CorrelatingLoggerKeepsCallerProvidedRefreshId()
    {
        var inner = new InMemoryDiagnosticsLogger();
        var correlated = new CorrelatingDiagnosticsLogger(inner, "caller-supplied");
        correlated.Info("message", new Dictionary<string, object?> { ["refreshId"] = "already-set" });
        Assert.Equal("already-set", inner.Entries.Single().Data!["refreshId"]);
    }

    [Fact]
    public void ProviderContextGeneratesShortRefreshIdPerInstance()
    {
        var first = new ProviderContext();
        var second = new ProviderContext();
        Assert.Equal(8, first.RefreshId.Length);
        Assert.NotEqual(first.RefreshId, second.RefreshId);
    }

    [Fact]
    public async Task BackgroundCacheRefreshLogsCompletionAtDebug()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        var logger = new InMemoryDiagnosticsLogger();
        try
        {
            using var cache = new JsonFileUsageCache(root, TimeSpan.FromMilliseconds(1), logger: logger);
            await cache.WriteAsync("provider:claude-code", "old", DateTimeOffset.UtcNow.AddMinutes(-1));
            var refreshed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = await cache.GetAsync("provider:claude-code", _ => refreshed.Task);
            refreshed.SetResult("new");
            for (var attempt = 0; attempt < 50 && !logger.Entries.Any(e => e.Message == "Background cache refresh completed"); attempt++)
                await Task.Delay(10);

            var entry = Assert.Single(logger.Entries, e => e.Message == "Background cache refresh completed");
            Assert.Equal("debug", entry.Level);
            var key = Assert.IsType<string>(entry.Data!["cacheKey"]);
            Assert.Matches("^[0-9a-f]{12}$", key);
            Assert.DoesNotContain("claude-code", key);
            Assert.True((long)entry.Data!["elapsedMs"]! >= 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CliRefreshCorrelatesWithTheSourceRefreshId()
    {
        var logger = new InMemoryDiagnosticsLogger();
        var codex = new ProviderDescriptor("codex", "Codex");
        var provider = new FixedSnapshotProvider(codex,
            ProviderSnapshot.Success(codex, [MetricLine.Progress("Weekly", 10, 100, MetricKind.Percent)], "Pro", DateTimeOffset.UtcNow));
        var source = new CoreUsageSnapshotSource(new UsageProviderCatalog([provider]),
            context: new ProviderContext { Logger = logger });

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApplication.RunAsync(["codex"], output, error,
            source: source, logger: logger);

        Assert.Equal(CliApplication.Success, exitCode);
        var started = Assert.Single(logger.Entries, e => e.Message == "CLI refresh started");
        var completed = Assert.Single(logger.Entries, e => e.Message == "CLI refresh completed");
        var startedId = Assert.IsType<string>(started.Data!["refreshId"]);
        Assert.Equal(startedId, completed.Data!["refreshId"]);
        Assert.NotEmpty(output.ToString());
    }

    [Fact]
    public async Task SingleRefreshCorrelatesEveryProviderEntry()
    {
        var logger = new InMemoryDiagnosticsLogger();
        var codex = new ProviderDescriptor("codex", "Codex");
        var claude = new ProviderDescriptor("claude-code", "Claude Code");
        var source = new CoreUsageSnapshotSource(
            new UsageProviderCatalog([
                new FixedSnapshotProvider(codex, ProviderSnapshot.Success(codex, [MetricLine.Badge("Status", "ok")], null, DateTimeOffset.UtcNow)),
                new FixedSnapshotProvider(claude, ProviderSnapshot.Success(claude, [MetricLine.Badge("Status", "ok")], null, DateTimeOffset.UtcNow))
            ]),
            context: new ProviderContext { Logger = logger });

        var firstRun = await source.GetSnapshotsAsync(null, force: true);
        Assert.Equal(2, firstRun.Count);

        var completed = logger.Entries.Where(e => e.Message == "Provider read completed").ToArray();
        Assert.Equal(2, completed.Length);
        var ids = completed.Select(e => e.Data!["refreshId"]).Cast<string>().Distinct().ToArray();
        Assert.Single(ids);
        Assert.False(string.IsNullOrWhiteSpace(ids[0]));

        var secondRun = await source.GetSnapshotsAsync(null, force: true);
        Assert.Equal(2, secondRun.Count);
        var secondIds = logger.Entries
            .Where(e => e.Message == "Provider read completed" && e.Data!["refreshId"] is string id && id != ids[0])
            .Select(e => e.Data!["refreshId"]).Cast<string>().Distinct().ToArray();
        Assert.Single(secondIds);
        Assert.NotEqual(ids[0], secondIds[0]);
    }

    [Fact]
    public async Task SourceHonorsCallerSuppliedRefreshId()
    {
        var logger = new InMemoryDiagnosticsLogger();
        var codex = new ProviderDescriptor("codex", "Codex");
        var source = new CoreUsageSnapshotSource(
            new UsageProviderCatalog([new FixedSnapshotProvider(codex,
                ProviderSnapshot.Success(codex, [MetricLine.Badge("Status", "ok")], null, DateTimeOffset.UtcNow))]),
            context: new ProviderContext { Logger = logger });

        _ = await source.GetSnapshotsAsync("codex", force: true, refreshId: "desktop-abc");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("desktop-abc", entry.Data!["refreshId"]);
    }

    [Fact]
    public void FileLoggerWritesRedactedJsonlAndRotatesAtTheCap()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "diagnostics.log");
        try
        {
            using (var logger = new FileDiagnosticsLogger(path, maxBytes: 32_768))
            {
                logger.Info("refresh started with Authorization: Bearer sk-secret-123",
                    new Dictionary<string, object?> { ["api_key"] = "secret-api-key", ["count"] = 1 });
                // Redaction truncates each string at 8 KiB, so a few padded entries push the
                // file past the 32 KiB cap and force a rotation.
                for (var i = 0; i < 8; i++)
                {
                    logger.Info("large payload", new Dictionary<string, object?>
                    {
                        ["blob"] = new string('a', 20_000),
                        ["token"] = "leak-me"
                    });
                }
                logger.Info("final entry");
            }

            Assert.True(File.Exists(path));
            var main = File.ReadAllText(path);
            Assert.Contains("final entry", main);
            Assert.DoesNotContain("sk-secret-123", main);
            Assert.DoesNotContain("secret-api-key", main);
            Assert.DoesNotContain("leak-me", main);

            Assert.True(File.Exists(path + ".1"));
            var rotated = File.ReadAllText(path + ".1");
            Assert.DoesNotContain("sk-secret-123", rotated);
            Assert.DoesNotContain("leak-me", rotated);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileLoggerKeepsOnlyBoundedSingleRotation()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "diagnostics.log");
        try
        {
            using (var logger = new FileDiagnosticsLogger(path, maxBytes: 32_768))
            {
                for (var i = 0; i < 10; i++)
                {
                    logger.Info(new string('b', 10_000) + $" entry {i}");
                    logger.Info("small entry");
                }
            }

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length <= 32_768 + 2_000);
            Assert.True(File.Exists(path + ".1"));
            Assert.False(File.Exists(path + ".2"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProductInfoReportsANonEmptyVersion()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProductInfo.Version));
    }

    private sealed class FixedSnapshotProvider(ProviderDescriptor descriptor, ProviderSnapshot snapshot) : IUsageProvider
    {
        public ProviderDescriptor Descriptor => descriptor;

        public Task<ProviderSnapshot> RefreshAsync(ProviderContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
