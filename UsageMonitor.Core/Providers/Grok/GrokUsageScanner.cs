using System.Text.Json;
using UsageMonitor.Core.Providers;

namespace UsageMonitor.Core.Providers.Grok;

/// <summary>
/// Reads Grok Build's local turn-completion ledger. The CLI writes one compact usage record per
/// completed turn under <c>~/.grok/sessions</c>; this scanner keeps the raw session content out of
/// the normalized model and only emits day/model aggregates.
/// </summary>
public sealed class GrokUsageScanner
{
    private const double CostTicksPerUsd = 10_000_000_000d;
    private readonly IProviderFileSystem _files;
    private readonly IModelCatalog _catalog;
    private readonly TimeZoneInfo _localTimeZone;

    public GrokUsageScanner(IProviderFileSystem? files = null, IModelCatalog? catalog = null,
        TimeZoneInfo? localTimeZone = null, int historyDays = 90)
    {
        if (historyDays <= 0) throw new ArgumentOutOfRangeException(nameof(historyDays));
        _files = files ?? new LocalProviderFileSystem();
        _catalog = catalog ?? new CachedModelCatalog();
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        HistoryDays = historyDays;
    }

    public int HistoryDays { get; }

    public ProviderUsageHistory Scan(string grokHome, DateTimeOffset now,
        string? historyStoreDirectory = null, Action<HistoryScanReport>? report = null)
    {
        var files = DiscoverFiles(grokHome).ToArray();
        return IncrementalHistoryScan.Run("grok", files, historyStoreDirectory,
            now.AddDays(-HistoryDays), ParseFile, afterMerge: null, report, _localTimeZone);
    }

    private IReadOnlyList<string> DiscoverFiles(string grokHome)
    {
        if (string.IsNullOrWhiteSpace(grokHome)) return Array.Empty<string>();
        var sessionsRoot = Path.Combine(grokHome, "sessions");
        var sessionFiles = _files.EnumerateFiles(sessionsRoot, "updates.jsonl", SearchOption.AllDirectories).ToArray();
        return sessionFiles.Length > 0
            ? sessionFiles
            : _files.EnumerateFiles(grokHome, "updates.jsonl", SearchOption.AllDirectories).ToArray();
    }

    private SourceHistoryContribution ParseFile(string path, DateTimeOffset since, DateOnly sinceDate,
        HistoryScanReport report)
    {
        var fingerprint = HistorySourceFingerprint.Of(path);
        var aggregator = new HistoryAggregator(ProviderIds.Grok);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The usage marker narrows the decoded strings while keeping the scanner safe for large
        // session ledgers. No prompt, transcript, or session record is retained after each line.
        foreach (var raw in _files.ReadLinesContaining(path, "\"usage\""))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var document = ProviderJson.Parse(raw.Trim());
            if (document is null) continue;
            var root = document.RootElement;
            var timestamp = ProviderJson.Date(ProviderJson.Property(root, "timestamp", "created_at", "time"));
            if (timestamp is null || IncrementalHistoryScan.DayOf(timestamp.Value, _localTimeZone) < sinceDate)
                continue;

            var parameters = ProviderJson.Object(ProviderJson.Property(root, "params", "parameters"));
            var update = parameters is { } parameterObject
                ? ProviderJson.Object(ProviderJson.Property(parameterObject, "update"))
                : null;
            var sessionUpdate = update is { } updateObject
                ? ProviderJson.String(ProviderJson.Property(updateObject, "sessionUpdate", "session_update"))
                : null;
            if (!string.IsNullOrWhiteSpace(sessionUpdate) &&
                !sessionUpdate.Equals("turn_completed", StringComparison.OrdinalIgnoreCase))
                continue;

            var usage = update is { } updated
                ? ProviderJson.Object(ProviderJson.Property(updated, "usage"))
                : null;
            usage ??= ProviderJson.Object(ProviderJson.Property(root, "usage"));
            if (usage is null) continue;

            var metadata = parameters is { } parameterData
                ? ProviderJson.Object(ProviderJson.Property(parameterData, "_meta", "meta"))
                : null;
            var eventId = ProviderJson.String(ProviderJson.Property(metadata ?? default,
                "eventId", "event_id", "id"))
                ?? ProviderJson.String(ProviderJson.Property(root, "eventId", "event_id", "id"));
            var modelUsage = ProviderJson.Object(ProviderJson.Property(usage.Value, "modelUsage", "model_usage"));
            if (modelUsage is { } models)
            {
                foreach (var model in models.EnumerateObject())
                {
                    if (!TryAddUsage(aggregator, seen, eventId, model.Name, model.Value, timestamp.Value,
                            report)) continue;
                }
                continue;
            }

            var modelId = ProviderJson.String(ProviderJson.Property(update ?? default,
                "modelId", "model_id", "model"))
                ?? ProviderJson.String(ProviderJson.Property(root, "modelId", "model_id", "model"))
                ?? "grok-build";
            TryAddUsage(aggregator, seen, eventId, modelId, usage.Value, timestamp.Value, report);
        }

        return aggregator.ToContribution(fingerprint);
    }

    private bool TryAddUsage(HistoryAggregator aggregator, HashSet<string> seen, string? eventId,
        string modelId, JsonElement usage, DateTimeOffset timestamp, HistoryScanReport report)
    {
        if (string.IsNullOrWhiteSpace(modelId)) modelId = "grok-build";
        var input = NonNegative(ProviderJson.Number(ProviderJson.Property(usage,
            "inputTokens", "input_tokens", "promptTokens", "prompt_tokens")));
        var output = NonNegative(ProviderJson.Number(ProviderJson.Property(usage,
            "outputTokens", "output_tokens", "completionTokens", "completion_tokens")));
        var cached = NonNegative(ProviderJson.Number(ProviderJson.Property(usage,
            "cachedReadTokens", "cached_read_tokens", "cacheReadTokens", "cache_read_tokens")));
        var cacheCreation = NonNegative(ProviderJson.Number(ProviderJson.Property(usage,
            "cacheCreationTokens", "cache_creation_tokens", "cacheWriteTokens", "cache_write_tokens")));
        var reasoning = NonNegative(ProviderJson.Number(ProviderJson.Property(usage,
            "reasoningTokens", "reasoning_tokens")));
        var total = NonNegative(ProviderJson.Number(ProviderJson.Property(usage,
            "totalTokens", "total_tokens")));
        var reportedCostTicks = ProviderJson.Number(ProviderJson.Property(usage,
            "costUsdTicks", "cost_usd_ticks"));
        var costTicks = NonNegative(reportedCostTicks);
        var hasReportedCost = reportedCostTicks is { } reported && double.IsFinite(reported) && reported >= 0;

        if (total <= 0) total = input + output;
        if (total <= 0 && !hasReportedCost) return false;
        cached = Math.Min(cached, input);
        cacheCreation = Math.Min(cacheCreation, Math.Max(0, input - cached));
        var uncached = Math.Max(0, input - cached - cacheCreation);
        var outputWithoutReasoning = Math.Max(0, output - Math.Min(output, reasoning));
        var stableEventId = string.IsNullOrWhiteSpace(eventId)
            ? $"{timestamp:O}|{modelId}|{input}|{cached}|{cacheCreation}|{output}|{reasoning}|{total}|{costTicks}"
            : $"{eventId}|{modelId}";
        if (!seen.Add(stableEventId)) return false;

        var pricing = _catalog.ResolvePrice(ProviderIds.Grok, modelId);
        var reportedCost = hasReportedCost ? costTicks / CostTicksPerUsd : (double?)null;
        var estimatedCost = pricing?.Estimate(uncached, cached, outputWithoutReasoning + reasoning, cacheCreation);
        var cost = reportedCost ?? estimatedCost ?? 0;
        var basis = reportedCost is not null
            ? UsageCostBasis.ProviderReported
            : estimatedCost is not null ? UsageCostBasis.CatalogEstimated : UsageCostBasis.Unpriced;
        if (basis == UsageCostBasis.Unpriced) aggregator.AddUnknownModel(modelId);

        var day = IncrementalHistoryScan.DayOf(timestamp, _localTimeZone);
        aggregator.Add(day, modelId, basis,
            basis == UsageCostBasis.ProviderReported ? PricingBasis.ProviderCredits
                : basis == UsageCostBasis.CatalogEstimated ? PricingBasis.LocalEstimate
                : PricingBasis.Unknown,
            tokens: total,
            uncachedInput: uncached,
            cachedInput: cached,
            cacheCreation: cacheCreation,
            output: outputWithoutReasoning,
            reasoning: Math.Min(output, reasoning),
            cost: cost,
            cacheSavings: pricing is null ? 0 : cached / 1_000_000d *
                Math.Max(0, pricing.InputPerMillion - pricing.CachedInputPerMillion),
            estimated: basis != UsageCostBasis.ProviderReported);
        report.RowsRead++;
        report.Track(timestamp);
        return true;
    }

    private static double NonNegative(double? value) => value is { } number && double.IsFinite(number)
        ? Math.Max(0, number)
        : 0;
}
