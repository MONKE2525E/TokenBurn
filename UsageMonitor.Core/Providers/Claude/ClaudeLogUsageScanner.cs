namespace UsageMonitor.Core.Providers.Claude;

public sealed class ClaudeLogUsageScanner
{
    private readonly IProviderFileSystem _files;
    private readonly IModelCatalog _catalog;
    private readonly TimeZoneInfo _localTimeZone;
    public ClaudeLogUsageScanner(IProviderFileSystem? files = null, IModelCatalog? catalog = null,
        TimeZoneInfo? localTimeZone = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _catalog = catalog ?? new CachedModelCatalog();
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
    }

    public static ProviderUsageHistory ScanDirectory(string claudeHome, DateTimeOffset since) => new ClaudeLogUsageScanner().Scan(claudeHome, since);

    public ProviderUsageHistory Scan(string claudeHome, DateTimeOffset since)
        => Scan(claudeHome, since, null, null);

    /// <summary>
    /// Incremental history scan. When <paramref name="historyStoreDirectory"/> is provided, files
    /// whose length and last-write time are unchanged since the previous scan are skipped and their
    /// previously computed contribution is reused. Without a store directory (CLI, tests) every file
    /// is parsed every time.
    /// </summary>
    public ProviderUsageHistory Scan(string claudeHome, DateTimeOffset since,
        string? historyStoreDirectory, Action<HistoryScanReport>? report)
    {
        var files = DiscoverFiles(claudeHome).ToArray();
        return IncrementalHistoryScan.Run("claude-code", files, historyStoreDirectory, since, ParseFile,
            afterMerge: null, report, _localTimeZone);
    }

    private IReadOnlyList<string> DiscoverFiles(string claudeHome)
    {
        // Claude keeps billable conversation records under `projects`. Older installations and
        // sanitized fixtures may pass that directory itself or place a single fixture directly at
        // the root. Prefer the canonical project tree when it exists so debug, cache, and sidecar
        // JSONL files cannot silently inflate spend.
        var projectsRoot = Path.Combine(claudeHome, "projects");
        var projectFiles = _files.EnumerateFiles(projectsRoot, "*.jsonl", SearchOption.AllDirectories).ToArray();
        return projectFiles.Length > 0
            ? projectFiles
            : _files.EnumerateFiles(claudeHome, "*.jsonl", SearchOption.AllDirectories).ToArray();
    }

    private SourceHistoryContribution ParseFile(string path, DateTimeOffset since, DateOnly sinceDate, HistoryScanReport report)
    {
        // Capture the fingerprint before reading. If the file is appended to mid-parse the stored
        // fingerprint will not match the file's final state, so the next scan re-parses it instead
        // of permanently missing the appended records.
        var fingerprint = HistorySourceFingerprint.Of(path);
        var entries = new List<ClaudeUsageEntry>();
        // Claude session files can be hundreds of megabytes because a single line may contain
        // a tool result or a long conversation transcript. Stream them so a refresh cannot
        // retain the complete history and its split-line copies simultaneously.
        foreach (var raw in _files.ReadLinesContaining(path, "\"usage\""))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var doc = ProviderJson.Parse(raw.Trim());
            if (doc is null) continue;
            var root = doc.RootElement;
            var timestamp = ProviderJson.Date(ProviderJson.Property(root, "timestamp", "created_at", "time"));
            // The window is applied at local-day granularity: records on the boundary day are part
            // of the window even before the `since` instant, so fresh parses and cached
            // contributions can never disagree about the boundary day.
            if (timestamp is null || IncrementalHistoryScan.DayOf(timestamp.Value, _localTimeZone) < sinceDate || !IsValidEntry(root)) continue;
            var message = ProviderJson.Object(ProviderJson.Property(root, "message"));
            var usage = ProviderJson.Object(message is { } msg ? ProviderJson.Property(msg, "usage") : ProviderJson.Property(root, "usage"));
            if (usage is null) continue;
            var parsed = ParseEntries(root, message, usage.Value, timestamp.Value);
            if (parsed.Count > 0)
            {
                report.RowsRead++;
                report.Track(timestamp);
            }
            entries.AddRange(parsed);
        }

        var deduped = Deduplicate(entries);
        var aggregator = new HistoryAggregator(ProviderIds.ClaudeCode);
        foreach (var entry in deduped.Where(entry => IncrementalHistoryScan.DayOf(entry.Timestamp, _localTimeZone) >= sinceDate))
        {
            if (!entry.PricingKnown && !string.IsNullOrWhiteSpace(entry.Model)) aggregator.AddUnknownModel(entry.Model);
            // Bucket by the local calendar day like Codex, OpenCode, and the dashboard
            // selectors. UTC bucketing moves evening usage into tomorrow and makes "Today" empty
            // for west-of-UTC machines.
            var day = IncrementalHistoryScan.DayOf(entry.Timestamp, _localTimeZone);
            var basis = entry.ReportedCost ? UsageCostBasis.ProviderReported
                : entry.PricingKnown ? UsageCostBasis.CatalogEstimated : UsageCostBasis.Unpriced;
            var pricingBasis = basis == UsageCostBasis.ProviderReported ? PricingBasis.ProviderCredits
                : basis == UsageCostBasis.CatalogEstimated ? PricingBasis.LocalEstimate
                : PricingBasis.Unknown;
            aggregator.Add(day, entry.Model, basis, pricingBasis,
                tokens: entry.Tokens,
                uncachedInput: entry.Input,
                cachedInput: entry.Cached,
                cacheCreation: entry.CacheCreation,
                output: entry.Output,
                reasoning: 0,
                cost: entry.Cost,
                cacheSavings: entry.CacheSavings,
                estimated: basis != UsageCostBasis.ProviderReported);
        }
        return aggregator.ToContribution(fingerprint);
    }

    private static IReadOnlyList<ClaudeUsageEntry> Deduplicate(IReadOnlyList<ClaudeUsageEntry> entries)
    {
        var result = new List<ClaudeUsageEntry>();
        var exact = new Dictionary<string, int>(StringComparer.Ordinal);
        var messages = new Dictionary<string, int>(StringComparer.Ordinal);
        var requests = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var exactKey = ExactKeyOf(entry);
            var collision = exact.TryGetValue(exactKey, out var exactIndex)
                ? exactIndex
                : entry.MessageId is { Length: > 0 } id && messages.TryGetValue(id, out var messageIndex)
                    ? messageIndex
                    : entry.RequestId is { Length: > 0 } request && requests.TryGetValue(request, out var requestIndex)
                        ? requestIndex
                        : -1;
            if (collision >= 0)
            {
                if (ShouldReplace(entry, result[collision]))
                {
                    // Drop the replaced entry's identity pointers so a later distinct message
                    // sharing the old request id is not wrongly merged into this slot.
                    var replaced = result[collision];
                    if (replaced.MessageId is { Length: > 0 } replacedMessage &&
                        messages.TryGetValue(replacedMessage, out var staleMessageSlot) && staleMessageSlot == collision)
                        messages.Remove(replacedMessage);
                    if (replaced.RequestId is { Length: > 0 } replacedRequest &&
                        requests.TryGetValue(replacedRequest, out var staleRequestSlot) && staleRequestSlot == collision)
                        requests.Remove(replacedRequest);
                    if (exact.TryGetValue(ExactKeyOf(replaced), out var staleExactSlot) && staleExactSlot == collision)
                        exact.Remove(ExactKeyOf(replaced));
                    result[collision] = entry;
                    exact[exactKey] = collision;
                    if (entry.MessageId is { Length: > 0 } entryMessage) messages[entryMessage] = collision;
                    if (entry.RequestId is { Length: > 0 } entryRequest) requests[entryRequest] = collision;
                }
                continue;
            }

            var resultIndex = result.Count;
            result.Add(entry);
            exact[exactKey] = resultIndex;
            if (entry.MessageId is { Length: > 0 } message) messages[message] = resultIndex;
            if (entry.RequestId is { Length: > 0 } requestKey) requests[requestKey] = resultIndex;
        }
        return result;
    }

    private static string ExactKeyOf(ClaudeUsageEntry entry) =>
        entry.MessageId is { Length: > 0 } messageId
            ? $"message:{messageId}|request:{entry.RequestId}"
            : entry.RequestId is { Length: > 0 } requestId
                ? $"request:{requestId}"
                : $"line:{entry.Timestamp:O}|{entry.Tokens}|{entry.Cost:0.########}";

    private static bool ShouldReplace(ClaudeUsageEntry candidate, ClaudeUsageEntry existing)
    {
        if (candidate.IsSidechain != existing.IsSidechain) return existing.IsSidechain;
        if (candidate.Tokens != existing.Tokens) return candidate.Tokens > existing.Tokens;
        // On token-equal duplicates prefer the line that carries a provider-reported cost over a
        // local estimate: Claude can re-emit the same message with its final cost annotated later.
        if (candidate.ReportedCost != existing.ReportedCost) return candidate.ReportedCost;
        return candidate.HasSpeed && !existing.HasSpeed;
    }

    private sealed record ClaudeUsageEntry(
        DateTimeOffset Timestamp,
        double Tokens,
        double Cost,
        double Input,
        double Cached,
        double CacheCreation,
        double Output,
        double CacheSavings,
        string? MessageId,
        string? RequestId,
        string Model,
        bool PricingKnown,
        bool ReportedCost,
        bool IsSidechain,
        bool HasSpeed);

    private IReadOnlyList<ClaudeUsageEntry> ParseEntries(
        System.Text.Json.JsonElement root,
        System.Text.Json.JsonElement? message,
        System.Text.Json.JsonElement usage,
        DateTimeOffset timestamp)
    {
        var entries = new List<ClaudeUsageEntry>();
        if (TryCreateEntry(root, message, usage, timestamp, null, null, null, out var parent))
            entries.Add(parent!);

        // Claude Code can include advisor work in usage.iterations. Count only
        // advisor_message iterations as separate model usage. Ordinary iterations are already
        // represented by the parent usage and would double-count the same turn.
        var iterations = ProviderJson.Array(ProviderJson.Property(usage, "iterations"));
        if (iterations is not { } array) return entries;
        var advisorIndex = 0;
        foreach (var iteration in array.EnumerateArray())
        {
            if (!string.Equals(ProviderJson.String(ProviderJson.Property(iteration, "type")), "advisor_message", StringComparison.OrdinalIgnoreCase)) continue;
            var model = ProviderJson.String(ProviderJson.Property(iteration, "model"));
            if (string.IsNullOrWhiteSpace(model)) continue;
            if (TryCreateEntry(
                    root,
                    message,
                    iteration,
                    timestamp,
                    model,
                    parent?.MessageId is { Length: > 0 } id ? $"{id}:advisor:{advisorIndex}" : null,
                    parent?.RequestId is { Length: > 0 } request ? $"{request}:advisor:{advisorIndex}" : null,
                    out var advisor))
            {
                entries.Add(advisor!);
                advisorIndex++;
            }
        }
        return entries;
    }

    private bool TryCreateEntry(
        System.Text.Json.JsonElement root,
        System.Text.Json.JsonElement? message,
        System.Text.Json.JsonElement usage,
        DateTimeOffset timestamp,
        string? modelOverride,
        string? messageIdOverride,
        string? requestIdOverride,
        out ClaudeUsageEntry? entry)
    {
        entry = null;
        var inputValue = ProviderJson.Number(ProviderJson.Property(usage, "input_tokens", "prompt_tokens"));
        var outputValue = ProviderJson.Number(ProviderJson.Property(usage, "output_tokens", "completion_tokens"));
        if (inputValue is not { } input || outputValue is not { } output) return false;
        if (!IsFiniteNonNegative(input) || !IsFiniteNonNegative(output)) return false;

        var cached = ProviderJson.NumberOrZero(usage, "cache_read_input_tokens", "cached_input_tokens", "cache_read_tokens");
        var cacheCreation = ProviderJson.NumberOrZero(usage, "cache_creation_input_tokens", "cache_write_input_tokens");
        if (ProviderJson.Object(ProviderJson.Property(usage, "cache_creation")) is { } cacheObject)
        {
            cacheCreation = ProviderJson.NumberOrZero(cacheObject, "ephemeral_5m_input_tokens", "cache_write_5m_input_tokens") +
                            ProviderJson.NumberOrZero(cacheObject, "ephemeral_1h_input_tokens", "cache_write_1h_input_tokens");
        }
        if (!IsFiniteNonNegative(cached) || !IsFiniteNonNegative(cacheCreation)) return false;
        var tokens = input + cached + cacheCreation + output;
        if (!IsFiniteNonNegative(tokens) || tokens <= 0) return false;

        var speed = ProviderJson.String(ProviderJson.Property(usage, "speed"));
        if (!string.IsNullOrWhiteSpace(speed) && !speed.Equals("fast", StringComparison.OrdinalIgnoreCase) &&
            !speed.Equals("standard", StringComparison.OrdinalIgnoreCase)) return false;
        var model = modelOverride ?? (message is { } messageValue ? ProviderJson.String(ProviderJson.Property(messageValue, "model")) : null) ?? "claude";
        var messageId = messageIdOverride ?? (message is { } messageObject
            ? ProviderJson.String(ProviderJson.Property(messageObject, "id", "message_id"))
            : ProviderJson.String(ProviderJson.Property(root, "message_id", "id")));
        var requestId = requestIdOverride
            ?? ProviderJson.String(ProviderJson.Property(root, "requestId", "request_id"))
            ?? (message is { } requestObject ? ProviderJson.String(ProviderJson.Property(requestObject, "requestId", "request_id")) : null);
        // Advisor iterations carry their own token counts but the record root's costUSD belongs
        // to the parent turn alone. Inheriting it priced every advisor entry at the parent's full
        // message cost; only entries without a model override may use the root-reported cost.
        var reportedCost = modelOverride is null
            ? ProviderJson.Number(ProviderJson.Property(root, "costUSD", "cost_usd"))
                ?? (message is { } costMessage ? ProviderJson.Number(ProviderJson.Property(costMessage, "costUSD", "cost_usd")) : null)
            : ProviderJson.Number(ProviderJson.Property(usage, "costUSD", "cost_usd"));
        var hasReportedCost = reportedCost is { } exactCost && IsFiniteNonNegative(exactCost);
        var pricing = _catalog.ResolvePrice(ProviderIds.ClaudeCode, model);
        var cost = hasReportedCost
            ? reportedCost!.Value
            : pricing?.Estimate(input, cached, output, cacheCreation) ?? 0;
        if (!IsFiniteNonNegative(cost)) return false;
        var isSidechain = ProviderJson.Bool(ProviderJson.Property(root, "isSidechain")) ?? false;
        var cacheSavings = pricing is null ? 0 : cached / 1_000_000d * Math.Max(0, pricing.InputPerMillion - pricing.CachedInputPerMillion);
        entry = new ClaudeUsageEntry(timestamp, tokens, cost, input, cached, cacheCreation, output, cacheSavings, messageId, requestId, model,
            hasReportedCost || pricing is not null, hasReportedCost, isSidechain, speed is not null);
        return true;
    }

    private static bool IsValidEntry(System.Text.Json.JsonElement root)
    {
        var version = ProviderJson.String(ProviderJson.Property(root, "version"));
        if (!string.IsNullOrWhiteSpace(version) && !IsSemverPrefix(version)) return false;
        foreach (var property in new[] { "sessionId", "requestId", "id", "model" })
        {
            var value = ProviderJson.Property(root, property);
            if (value is { ValueKind: System.Text.Json.JsonValueKind.Null }) return false;
            if (value is { ValueKind: System.Text.Json.JsonValueKind.String } && string.IsNullOrEmpty(value.Value.GetString())) return false;
        }
        if (ProviderJson.Object(ProviderJson.Property(root, "message")) is { } message)
        {
            foreach (var property in new[] { "id", "model" })
            {
                var value = ProviderJson.Property(message, property);
                if (value is { ValueKind: System.Text.Json.JsonValueKind.Null }) return false;
                if (value is { ValueKind: System.Text.Json.JsonValueKind.String } && string.IsNullOrEmpty(value.Value.GetString())) return false;
            }
        }
        return true;
    }

    private static bool IsSemverPrefix(string value)
    {
        var parts = value.Split('.');
        if (parts.Length < 3) return false;
        for (var i = 0; i < 3; i++)
        {
            var part = parts[i];
            if (i == 2) part = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (part.Length == 0 || !part.All(char.IsDigit)) return false;
        }
        return true;
    }

    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;
}
