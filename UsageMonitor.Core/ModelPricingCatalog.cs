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

public enum PricingBasis
{
    OfficialApi,
    PublicCatalog,
    LocalEstimate,
    ProviderCredits,
    Unknown
}

public sealed record ModelCatalogEntry(
    string ProviderId,
    string ModelId,
    string DisplayName,
    ModelPrice? Price,
    string? ContextWindow,
    DateTimeOffset RetrievedAt,
    string Source,
    bool IsStale,
    PricingBasis Basis = PricingBasis.Unknown);

public sealed record ModelPricingSnapshot(
    IReadOnlyList<ModelCatalogEntry> Models,
    DateTimeOffset RetrievedAt,
    string Source,
    bool IsStale,
    string? ETag = null,
    string? LastModified = null);

public sealed record ModelPricingOverride(
    string? CanonicalModel,
    ModelPrice? Price);

public interface IModelCatalogSource
{
    string Id { get; }
    Task<ModelPricingSnapshot?> FetchAsync(ModelCatalogRequest? request = null,
        CancellationToken cancellationToken = default);
}

public interface IModelCatalog
{
    Task<ModelPricingSnapshot> GetAsync(string? providerId = null, bool forceRefresh = false,
        CancellationToken cancellationToken = default);
    ModelPrice? ResolvePrice(string providerId, string modelId);
}

/// <summary>
/// Safe local pricing catalog for session-history estimates. A future custom-model pack can add
/// explicit local overrides without turning the monitor into an API billing client.
/// </summary>
public static class ModelPricingCatalog
{
    private static IReadOnlyList<ModelCatalogEntry> _remote = Array.Empty<ModelCatalogEntry>();

    public static ModelPrice? TryResolve(string? model)
        => TryResolve(null, model);

    public static ModelPrice? TryResolve(string? providerId, string? model)
    {
        var normalized = Normalize(model);
        // Free-routed models are free. This must beat the remote catalog's family matching: a
        // "deepseek-v4-flash" entry would otherwise price its "-free" sibling.
        if (normalized.Contains("-free", StringComparison.OrdinalIgnoreCase))
            return new ModelPrice(0, 0, 0);
        var exactMatches = _remote.Where(entry => entry.Price is not null && ModelMatches(entry.ModelId, normalized)).ToArray();
        var matches = exactMatches.Length > 0
            ? exactMatches
            : _remote.Where(entry => entry.Price is not null && FamilyMatches(entry.ModelId, normalized)).ToArray();
        var provider = Normalize(providerId);
        var remote = matches.Length == 1
            ? matches[0]
            : matches.FirstOrDefault(entry => ProviderMatches(provider, entry.ProviderId)) ?? PreferLiveRate(matches);
        if (remote?.Price is { } remotePrice) return remotePrice;
        var value = normalized;
        if (value.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains("opus", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(15, 1.5, 75);
            if (value.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(.8, .08, 4);
            return new ModelPrice(3, .3, 15, 3.75);
        }
        // Codex's local model id is not guaranteed to be present in the remote catalog before
        // the first refresh. Keep the known local price available so Codex history is not
        // silently assigned $0 and omitted from the spend ring on startup or offline.
        if (value.Contains("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
            return new ModelPrice(.1, .01, .6);
        // OpenCode persists a zero provider cost for subscription and free-routed responses.
        // Keep their cash-equivalent estimates meaningful when no fresh remote catalog entry is
        // available. Reasoning tokens are priced as generated output by the OpenCode reader.
        if (value.Contains("kimi-k3", StringComparison.OrdinalIgnoreCase))
            return new ModelPrice(3, .3, 15);
        if (value.Contains("deepseek-v4-flash", StringComparison.OrdinalIgnoreCase))
            return new ModelPrice(.14, .0028, .28);
        if (value.Contains("big-pickle", StringComparison.OrdinalIgnoreCase))
            return new ModelPrice(0, 0, 0);
        if (value.Contains("mini", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(.25, .025, 2);
        if (value.Contains("o4", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(1.1, .275, 4.4);
        if (value.Contains("o3", StringComparison.OrdinalIgnoreCase)) return new ModelPrice(2, .5, 8);
        return null;
    }

    private static bool ProviderMatches(string requested, string catalog)
        => requested switch
        {
            "claude-code" => catalog is "claude" or "anthropic",
            "codex" => catalog is "codex" or "openai",
            "grok" => catalog is "grok" or "xai",
            _ => requested.Length > 0 && requested == Normalize(catalog)
        };

    private static bool ModelMatches(string catalogModel, string requestedModel)
    {
        var catalog = Normalize(catalogModel);
        return catalog == requestedModel ||
               catalog.EndsWith('/' + requestedModel, StringComparison.Ordinal) ||
               requestedModel.EndsWith('/' + catalog, StringComparison.Ordinal);
    }

    private static bool FamilyMatches(string catalogModel, string requestedModel) =>
        FamilyModelName(catalogModel) == FamilyModelName(requestedModel);

    private static string FamilyModelName(string model)
    {
        var name = Normalize(model);
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[..colon];
        foreach (var suffix in new[] { "-free", "-latest" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        var dash = name.LastIndexOf('-');
        if (dash >= 0 && name[(dash + 1)..].Length == 4 && name[(dash + 1)..].All(char.IsDigit))
            name = name[..dash];
        return name;
    }

    private static ModelCatalogEntry? PreferLiveRate(IEnumerable<ModelCatalogEntry> matches) =>
        matches.FirstOrDefault(entry => Normalize(entry.ModelId).EndsWith("-latest", StringComparison.Ordinal)) ??
        matches.FirstOrDefault(entry => !Normalize(entry.ModelId).Contains(':'));

    internal static void ApplyRemote(IReadOnlyList<ModelCatalogEntry> entries)
        => Volatile.Write(ref _remote, entries);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
