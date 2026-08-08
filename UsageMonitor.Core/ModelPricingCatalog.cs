namespace UsageMonitor.Core;

/// <summary>
/// Model pricing used by local-history estimates. Values are local conservative defaults and
/// never require a hosted provider key or a network catalog request.
/// </summary>
public sealed record ModelPrice(double InputPerMillion, double CachedInputPerMillion, double OutputPerMillion,
    double? CacheCreationPerMillion = null)
{
    public double Estimate(double inputTokens, double cachedInputTokens, double outputTokens, double cacheCreationTokens = 0)
        => Math.Max(0, inputTokens) / 1_000_000d * InputPerMillion
         + Math.Max(0, cachedInputTokens) / 1_000_000d * CachedInputPerMillion
         + Math.Max(0, cacheCreationTokens) / 1_000_000d * (CacheCreationPerMillion ?? InputPerMillion)
         + Math.Max(0, outputTokens) / 1_000_000d * OutputPerMillion;
}

/// <summary>
/// Safe local pricing catalog for session-history estimates. A future custom-model pack can add
/// explicit local overrides without turning the monitor into an API billing client.
/// </summary>
public static class ModelPricingCatalog
{
    public static ModelPrice Resolve(string? model)
    {
        var normalized = Normalize(model);
        var value = normalized;
        if (value.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains("opus", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(15, 1.5, 75);
            if (value.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(.8, .08, 4);
            return new ModelPrice(3, .3, 15, 3.75);
        }
        if (value.Contains("mini", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(.25, .025, 2);
        if (value.Contains("o4", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(1.1, .275, 4.4);
        if (value.Contains("o3", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(2, .5, 8);
        return new ModelPrice(5, .5, 15);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
