namespace UsageMonitor.Core.Providers;

/// <summary>
/// Day-bucketed aggregation shared by the JSONL history scanners. Owns the totals, model/day
/// breakdown, and unknown-model collections so every provider's contribution is summed, priced,
/// and ordered identically. The per-entry totals value is caller-supplied because providers count
/// different token components (Codex reports total plus reasoning; Claude reports input, cache, and
/// output only).
/// </summary>
internal sealed class HistoryAggregator
{
    private readonly string _providerId;
    private readonly Dictionary<DateOnly, (double Tokens, double Cost)> _totals = new();
    private readonly Dictionary<(DateOnly Date, string Model, UsageCostBasis Basis), UsageBreakdownPoint> _breakdown = new();
    private readonly HashSet<string> _unknownModels = new(StringComparer.OrdinalIgnoreCase);

    public HistoryAggregator(string providerId) => _providerId = providerId;

    public void AddUnknownModel(string model)
    {
        if (!string.IsNullOrWhiteSpace(model)) _unknownModels.Add(model);
    }

    public void Add(
        DateOnly day,
        string model,
        UsageCostBasis basis,
        PricingBasis pricingBasis,
        double tokens,
        double uncachedInput,
        double cachedInput,
        double cacheCreation,
        double output,
        double reasoning,
        double cost,
        double cacheSavings,
        bool estimated)
    {
        _totals.TryGetValue(day, out var prior);
        _totals[day] = (prior.Tokens + tokens, prior.Cost + cost);
        var key = (day, model, basis);
        if (_breakdown.TryGetValue(key, out var existing))
        {
            _breakdown[key] = existing with
            {
                UncachedInputTokens = existing.UncachedInputTokens + uncachedInput,
                CachedInputTokens = existing.CachedInputTokens + cachedInput,
                CacheCreationTokens = existing.CacheCreationTokens + cacheCreation,
                OutputTokens = existing.OutputTokens + output,
                ReasoningTokens = existing.ReasoningTokens + reasoning,
                CostUsd = existing.CostUsd + cost,
                CacheSavingsUsd = existing.CacheSavingsUsd + cacheSavings
            };
        }
        else
        {
            _breakdown[key] = new UsageBreakdownPoint(day, _providerId, model, uncachedInput, cachedInput,
                cacheCreation, output, reasoning, cost, basis, pricingBasis, estimated, cacheSavings);
        }
    }

    public SourceHistoryContribution ToContribution(string fingerprint) => new()
    {
        Fingerprint = fingerprint,
        Points = _totals.OrderBy(pair => pair.Key)
            .Select(pair => new UsageHistoryPoint(pair.Key, pair.Value.Tokens, pair.Value.Cost, true))
            .ToList(),
        Breakdown = _breakdown.Values
            .OrderBy(point => point.Date)
            .ThenBy(point => point.ModelId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(point => point.CostBasis)
            .ToList(),
        UnknownModels = _unknownModels.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToList()
    };
}
