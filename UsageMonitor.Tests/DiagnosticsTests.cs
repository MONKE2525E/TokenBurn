using System.Text.Json;
using UsageMonitor.Cli;
using UsageMonitor.Core;
using UsageMonitor.Core.Providers;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void RedactorScrubsBearerTokensJsonValuesAndHeaders()
    {
        var text = SensitiveDataRedactor.Redact(
            "Authorization: Bearer sk-ant-abc123DEF456 cookie=session=xyz \"access_token\":\"secret-token-value\" " +
            "client_secret=abc-def-ghi api-key-12345 account_id=acct_999 user_id=usr_1");
        Assert.DoesNotContain("sk-ant-abc123DEF456", text);
        Assert.DoesNotContain("session=xyz", text);
        Assert.DoesNotContain("secret-token-value", text);
        Assert.DoesNotContain("abc-def-ghi", text);
        Assert.DoesNotContain("api-key-12345", text);
        Assert.DoesNotContain("acct_999", text);
        Assert.DoesNotContain("usr_1", text);
    }

    [Fact]
    public void RedactorScrubsNestedDictionariesByKeyAndPreservesMetadata()
    {
        var payload = new Dictionary<string, object?>
        {
            ["providerId"] = "claude-code",
            ["metricCount"] = 2,
            ["token"] = "super-secret-token",
            ["nested"] = new Dictionary<string, object?>
            {
                ["api_key"] = "nested-key",
                ["historyPoints"] = 12
            }
        };
        var redacted = (Dictionary<string, object?>)SensitiveDataRedactor.RedactObject(payload)!;

        Assert.Equal("claude-code", redacted["providerId"]);
        Assert.Equal(2, redacted["metricCount"]);
        Assert.Equal("[redacted]", redacted["token"]);
        var nested = Assert.IsType<Dictionary<string, object?>>(redacted["nested"]!);
        Assert.Equal("[redacted]", nested["api_key"]);
        Assert.Equal(12, nested["historyPoints"]);
    }

    [Fact]
    public void RedactorScrubsExceptionMessagesAndTruncatesGiantPayloads()
    {
        var exception = new InvalidOperationException("failed with token=abc123 and C:\\Users\\victim\\.codex");
        var text = SensitiveDataRedactor.Redact(exception.Message);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("C:\\Users\\victim", text);

        var huge = new string('a', 20_000) + " token=leak";
        var bounded = SensitiveDataRedactor.Redact(huge);
        Assert.True(bounded.Length is > 8_000 and <= 8_193, "payloads must be bounded");
        Assert.DoesNotContain("leak", bounded);
    }

    [Fact]
    public void InMemoryLoggerRedactsMessagesAndExceptionMessages()
    {
        var logger = new InMemoryDiagnosticsLogger();
        logger.Info("user developer@example.invalid signed in", new Dictionary<string, object?>
        {
            ["api_key"] = "key-value",
            ["count"] = 3
        });
        logger.Error("boom", exception: new InvalidOperationException("secret credential=abc"));

        Assert.Equal(2, logger.Entries.Count);
        var info = logger.Entries.First();
        Assert.DoesNotContain("developer@example.invalid", info.Message);
        Assert.Equal("[redacted]", info.Data!["api_key"]);
        Assert.Equal(3, info.Data!["count"]);
        Assert.DoesNotContain("abc", logger.Entries.Last().ExceptionMessage);
    }

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
    public void RedactorKeepsLogicalSecretStoreKeyNames()
    {
        // The credential store logs the logical key (e.g. "providers/openrouter/api-key") under
        // "storeKey" so support can tell which credential failed; the secret value itself stays
        // redacted. An old "credentialKey" name matched the redactor's "credential" sensitivity
        // and destroyed that diagnostic value.
        var redacted = (IReadOnlyDictionary<string, object?>)SensitiveDataRedactor.RedactObject(
            new Dictionary<string, object?>
            {
                ["storeKey"] = "providers/openrouter/api-key",
                ["credentialKey"] = "providers/openrouter/api-key",
                ["secret"] = "sk-live-123"
            })!;
        Assert.Equal("providers/openrouter/api-key", redacted["storeKey"]);
        Assert.Equal("[redacted]", redacted["credentialKey"]);
        Assert.Equal("[redacted]", redacted["secret"]);
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
