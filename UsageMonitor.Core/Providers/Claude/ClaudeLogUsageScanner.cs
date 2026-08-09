namespace UsageMonitor.Core.Providers.Claude;

public sealed class ClaudeLogUsageScanner
{
    private readonly IProviderFileSystem _files;
    private readonly IModelCatalog _catalog;
    public ClaudeLogUsageScanner(IProviderFileSystem? files = null, IModelCatalog? catalog = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _catalog = catalog ?? new CachedModelCatalog();
    }

    public static ProviderUsageHistory ScanDirectory(string claudeHome, DateTimeOffset since) => new ClaudeLogUsageScanner().Scan(claudeHome, since);

    public ProviderUsageHistory Scan(string claudeHome, DateTimeOffset since)
    {
        var entries = new List<ClaudeUsageEntry>();
        // Claude keeps billable conversation records under `projects`. Older installations and
        // sanitized fixtures may pass that directory itself or place a single fixture directly at
        // the root. Prefer the canonical project tree when it exists so debug, cache, and sidecar
        // JSONL files cannot silently inflate spend.
        var projectsRoot = Path.Combine(claudeHome, "projects");
        var projectFiles = _files.EnumerateFiles(projectsRoot, "*.jsonl", SearchOption.AllDirectories).ToArray();
        var files = projectFiles.Length > 0
            ? projectFiles
            : _files.EnumerateFiles(claudeHome, "*.jsonl", SearchOption.AllDirectories);
        foreach (var path in files)
        {
            // Claude session files can be hundreds of megabytes because a single line may contain
            // a tool result or a long conversation transcript. Stream them so a refresh cannot
            // retain the complete history and its split-line copies simultaneously.
            foreach (var raw in _files.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                // Most Claude records are transcript/tool metadata and cannot contribute usage.
                // Avoid allocating a JsonDocument for those very large lines.
                if (!raw.Contains("\"usage\"", StringComparison.OrdinalIgnoreCase)) continue;
                using var doc = ProviderJson.Parse(raw.Trim());
                if (doc is null) continue;
                var root = doc.RootElement;
                var timestamp = ProviderJson.Date(ProviderJson.Property(root, "timestamp", "created_at", "time"));
                if (timestamp is null || timestamp < since || !IsValidEntry(root)) continue;
                var message = ProviderJson.Object(ProviderJson.Property(root, "message"));
                var usage = ProviderJson.Object(message is { } msg ? ProviderJson.Property(msg, "usage") : ProviderJson.Property(root, "usage"));
                if (usage is null) continue;
                var parsed = ParseEntries(root, message, usage.Value, timestamp.Value);
                entries.AddRange(parsed);
            }
        }

        var deduped = Deduplicate(entries);
        var totals = new Dictionary<DateOnly, (double Tokens, double Cost)>();
        var breakdown = new Dictionary<(DateOnly Date, string Model, UsageCostBasis Basis), UsageBreakdownPoint>();
        var unknownModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in deduped.Where(entry => entry.Timestamp >= since))
        {
            if (!entry.PricingKnown && !string.IsNullOrWhiteSpace(entry.Model)) unknownModels.Add(entry.Model);
            var day = DateOnly.FromDateTime(entry.Timestamp.UtcDateTime);
            if (totals.TryGetValue(day, out var prior)) totals[day] = (prior.Tokens + entry.Tokens, prior.Cost + entry.Cost);
            else totals[day] = (entry.Tokens, entry.Cost);
            var basis = entry.ReportedCost ? UsageCostBasis.ProviderReported
                : entry.PricingKnown ? UsageCostBasis.CatalogEstimated : UsageCostBasis.Unpriced;
            var key = (day, entry.Model, basis);
            if (breakdown.TryGetValue(key, out var existing))
            {
                breakdown[key] = existing with
                {
                    UncachedInputTokens = existing.UncachedInputTokens + entry.Input,
                    CachedInputTokens = existing.CachedInputTokens + entry.Cached,
                    CacheCreationTokens = existing.CacheCreationTokens + entry.CacheCreation,
                    OutputTokens = existing.OutputTokens + entry.Output,
                    CostUsd = existing.CostUsd + entry.Cost,
                    CacheSavingsUsd = existing.CacheSavingsUsd + entry.CacheSavings
                };
            }
            else
            {
                breakdown[key] = new UsageBreakdownPoint(day, ProviderIds.ClaudeCode, entry.Model,
                    entry.Input, entry.Cached, entry.CacheCreation, entry.Output, 0, entry.Cost, basis,
                    basis == UsageCostBasis.ProviderReported ? PricingBasis.ProviderCredits
                        : basis == UsageCostBasis.CatalogEstimated ? PricingBasis.LocalEstimate : PricingBasis.Unknown,
                    basis != UsageCostBasis.ProviderReported, entry.CacheSavings);
            }
        }
        return new ProviderUsageHistory(totals.OrderBy(x => x.Key).Select(x => new UsageHistoryPoint(x.Key, x.Value.Tokens, x.Value.Cost, true)).ToArray())
        {
            UnknownModels = unknownModels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            Breakdown = breakdown.Values.OrderBy(point => point.Date).ThenBy(point => point.ModelId, StringComparer.OrdinalIgnoreCase).ToArray()
        };
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
            var exactKey = entry.MessageId is { Length: > 0 } messageId
                ? $"message:{messageId}|request:{entry.RequestId}"
                : entry.RequestId is { Length: > 0 } requestId
                    ? $"request:{requestId}"
                    : $"line:{entry.Timestamp:O}|{entry.Tokens}|{entry.Cost:0.########}";
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
                    result[collision] = entry;
                    exact[exactKey] = collision;
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

    private static bool ShouldReplace(ClaudeUsageEntry candidate, ClaudeUsageEntry existing)
    {
        if (candidate.IsSidechain != existing.IsSidechain) return existing.IsSidechain;
        if (candidate.Tokens != existing.Tokens) return candidate.Tokens > existing.Tokens;
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

        var cached = Number(usage, "cache_read_input_tokens", "cached_input_tokens", "cache_read_tokens");
        var cacheCreation = Number(usage, "cache_creation_input_tokens", "cache_write_input_tokens");
        if (ProviderJson.Object(ProviderJson.Property(usage, "cache_creation")) is { } cacheObject)
        {
            cacheCreation = Number(cacheObject, "ephemeral_5m_input_tokens", "cache_write_5m_input_tokens") +
                            Number(cacheObject, "ephemeral_1h_input_tokens", "cache_write_1h_input_tokens");
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
        var reportedCost = ProviderJson.Number(ProviderJson.Property(root, "costUSD", "cost_usd"))
            ?? (message is { } costMessage ? ProviderJson.Number(ProviderJson.Property(costMessage, "costUSD", "cost_usd")) : null);
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

    private static double Number(System.Text.Json.JsonElement element, params string[] names) => ProviderJson.Number(ProviderJson.Property(element, names)) ?? 0;
}
