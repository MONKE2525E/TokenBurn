using System.Text.Json;
using UsageMonitor.Cli;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class UsageApiTests
{
    [Fact]
    public async Task LimitsEnvelopeUsesStableSchemaAndProgressResources()
    {
        var fetched = DateTimeOffset.UtcNow.AddSeconds(-2);
        var source = new FakeSource(new UsageSnapshotData("codex", "Codex", "Pro",
            [new ProgressMetricData("Session", 25, 100, "percent", fetched.AddHours(1), 300_000)], fetched));
        var response = await new UsageApiService(source).HandleAsync("GET", "/v1/limits");

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal("openusage.limits.v1", doc.RootElement.GetProperty("schema").GetString());
        var resource = doc.RootElement.GetProperty("providers").GetProperty("codex").GetProperty("resources").GetProperty("session");
        Assert.Equal(25, resource.GetProperty("used").GetDouble());
        Assert.Equal(75, resource.GetProperty("remaining").GetDouble());
        Assert.Equal(0.25, resource.GetProperty("utilization").GetDouble());
    }

    [Fact]
    public async Task ProviderRoutesAndOptionsHaveDocumentedStatuses()
    {
        var source = new FakeSource();
        var service = new UsageApiService(source);
        Assert.Equal(204, (await service.HandleAsync("OPTIONS", "/v1/limits")).StatusCode);
        Assert.Equal(404, (await service.HandleAsync("GET", "/v1/limits/nope")).StatusCode);
        Assert.Equal(405, (await service.HandleAsync("POST", "/v1/limits")).StatusCode);
        Assert.Equal(404, (await service.HandleAsync("GET", "/unknown")).StatusCode);
    }

    [Fact]
    public async Task UsageRouteReturnsLegacyArrayWithTypeTaggedLines()
    {
        var source = new FakeSource(new UsageSnapshotData("codex", "Codex", null,
            [new TextMetricData("Today", "$1.00")], DateTimeOffset.UtcNow));
        var response = await new UsageApiService(source).HandleAsync("GET", "/v1/usage/codex");
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("text", doc.RootElement[0].GetProperty("lines")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task UsageRoutePreservesRedactedProviderFailureDetails()
    {
        var source = new FakeSource(new UsageSnapshotData("claude-code", "Claude Code", "Pro", [], DateTimeOffset.UtcNow)
        {
            Error = "Authentication failed for user@example.com",
            Warning = "Run `claude auth login`"
        });

        var response = await new UsageApiService(source).HandleAsync("GET", "/v1/usage/claude-code");

        using var doc = JsonDocument.Parse(response.Body);
        var provider = doc.RootElement[0];
        Assert.Equal("Authentication failed for [redacted-email]", provider.GetProperty("error").GetString());
        Assert.Equal("Run `claude auth login`", provider.GetProperty("warning").GetString());
        Assert.DoesNotContain("user@example.com", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageHistoryIsSerializedWithoutLocalPaths()
    {
        var snapshot = new UsageSnapshotData("codex", "Codex", "Pro", [], DateTimeOffset.UtcNow)
        {
            UsageHistory = new UsageHistoryData([
                new UsageHistoryPointData(DateOnly.FromDateTime(DateTime.UtcNow), 1200, 0.42, true)])
        };
        var response = await new UsageApiService(new FakeSource(snapshot)).HandleAsync("GET", "/v1/usage/codex");
        using var doc = JsonDocument.Parse(response.Body);
        var provider = doc.RootElement[0];
        Assert.Equal(0.42, provider.GetProperty("usageHistory").GetProperty("totalCostUsd").GetDouble(), 3);
        Assert.DoesNotContain(Environment.UserName, response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliPrintsLimitsAndPassesForceThroughWithoutLaunchingGui()
    {
        var source = new FakeSource(new UsageSnapshotData("codex", "Codex", null, [], DateTimeOffset.UtcNow));
        using var output = new StringWriter();
        var exitCode = await CliApplication.RunAsync(["codex", "--force"], output, TextWriter.Null, source);
        Assert.Equal(CliApplication.Success, exitCode);
        Assert.True(source.LastForce);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("openusage.limits.v1", doc.RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public async Task CliUnknownProviderIsInvalidArgument()
    {
        var output = new StringWriter();
        var exitCode = await CliApplication.RunAsync(["unknown"], output, TextWriter.Null, new FakeSource());
        Assert.Equal(CliApplication.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task FailedProviderIsReportedInLimitsAndReturnsRefreshExitCode()
    {
        var source = new FakeSource(new UsageSnapshotData("codex", "Codex", null, [], DateTimeOffset.UtcNow)
        {
            Error = "Authentication failed for user@example.com"
        });
        var output = new StringWriter();
        var exitCode = await CliApplication.RunAsync(["codex"], output, TextWriter.Null, source);
        Assert.Equal(CliApplication.RefreshFailed, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("Authentication failed for [redacted-email]", doc.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task DiagnoseOutputUsesPlaceholdersForPersonalPaths()
    {
        using var output = new StringWriter();
        var exitCode = await CliApplication.RunAsync(["--diagnose"], output, TextWriter.Null, new FakeSource());
        Assert.Equal(CliApplication.Success, exitCode);
        Assert.Contains("local-app-data", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonFiniteMetricsSerializeAsNullNotAsJsonStringsOrZeros()
    {
        var snapshot = new UsageSnapshotData("codex", "Codex", null,
            [new ValuesMetricData("Balance", [new ScalarValueData(double.NaN, "usd")])],
            DateTimeOffset.UtcNow)
        {
            UsageHistory = new UsageHistoryData([
                new UsageHistoryPointData(DateOnly.FromDateTime(DateTime.UtcNow), double.PositiveInfinity, double.NaN)])
        };
        var response = await new UsageApiService(new FakeSource(snapshot)).HandleAsync("GET", "/v1/usage/codex");

        Assert.Equal(200, response.StatusCode);
        Assert.DoesNotContain("NaN", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", response.Body, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(response.Body);
        var provider = doc.RootElement[0];
        var value = provider.GetProperty("lines")[0].GetProperty("value").GetString();
        Assert.Equal(string.Empty, value);
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("usageHistory").GetProperty("totalCostUsd").ValueKind);
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("usageHistory").GetProperty("points")[0].GetProperty("tokens").ValueKind);
    }

    [Fact]
    public async Task MalformedPercentEscapesNeverBecomeServerErrors()
    {
        var service = new UsageApiService(new FakeSource());
        // Modern Uri.UnescapeDataString is lenient and returns malformed escapes verbatim, so the
        // contract is: every hostile path yields the JSON error envelope, never an exception or a
        // 500. The host layer additionally catches any decode-time exception as a 400.
        var hostilePaths = new[] { "/v1/limits/%zz", "/v1/limits/%", "/v1/limits/%2", "/v1/limits/a%2Fb", "/v1/limits/%25", "/v1/limits/\u0000" };
        foreach (var path in hostilePaths)
        {
            var response = await service.HandleAsync("GET", path);
            Assert.InRange(response.StatusCode, 400, 599);
            using var doc = JsonDocument.Parse(response.Body);
            Assert.True(doc.RootElement.TryGetProperty("error", out _), $"path {path} must use the JSON error envelope");
        }
    }

    [Fact]
    public async Task UnknownVerbsAndRoutesKeepTheJsonErrorEnvelope()
    {
        var service = new UsageApiService(new FakeSource());
        Assert.Equal("{\"error\":\"method_not_allowed\"}", (await service.HandleAsync("DELETE", "/v1/limits")).Body);
        Assert.Equal("{\"error\":\"not_found\"}", (await service.HandleAsync("GET", "/v1/settings")).Body);
        Assert.Equal("{\"error\":\"provider_not_found\"}", (await service.HandleAsync("GET", "/v1/limits/nope")).Body);
    }

    private sealed class FakeSource(params UsageSnapshotData[] snapshots) : IUsageSnapshotSource
    {
        private readonly IReadOnlyList<UsageSnapshotData> _snapshots = snapshots;
        public IReadOnlySet<string> KnownProviderIds { get; } = new HashSet<string>(["codex", "claude-code"], StringComparer.OrdinalIgnoreCase);
        public bool LastForce { get; private set; }
        public string? LastRefreshId { get; private set; }
        public Task<IReadOnlyList<UsageSnapshotData>> GetSnapshotsAsync(string? providerId, bool force,
            CancellationToken cancellationToken = default, string? refreshId = null)
        {
            LastForce = force;
            LastRefreshId = refreshId;
            return Task.FromResult<IReadOnlyList<UsageSnapshotData>>(providerId is null
                ? _snapshots
                : _snapshots.Where(x => string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
    }
}
