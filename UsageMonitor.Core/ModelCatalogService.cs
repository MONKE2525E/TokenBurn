using System.Text.Json;
using System.Text.Json.Serialization;
using UsageMonitor.Core.Providers;

namespace UsageMonitor.Core;

public sealed class OpenRouterModelCatalogSource : IModelCatalogSource
{
    private readonly IProviderHttpClient _http;
    private readonly Uri _uri;

    public OpenRouterModelCatalogSource(IProviderHttpClient? http = null, Uri? uri = null)
    {
        _http = http ?? new ProviderHttpClient();
        _uri = uri ?? new Uri("https://openrouter.ai/api/v1/models");
    }

    public string Id => "openrouter";

    public async Task<ModelPricingSnapshot?> FetchAsync(ModelCatalogRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/json",
            ["User-Agent"] = "UsageMonitor/1.0"
        };
        if (!string.IsNullOrWhiteSpace(request?.ETag)) headers["If-None-Match"] = request.ETag;
        if (!string.IsNullOrWhiteSpace(request?.LastModified)) headers["If-Modified-Since"] = request.LastModified;
        var response = await _http.SendAsync(HttpMethod.Get, _uri,
            headers,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == 304 && (_lastSnapshot ?? request?.CachedSnapshot) is { } unchanged)
            return unchanged with { RetrievedAt = DateTimeOffset.UtcNow, IsStale = false };
        if (response.StatusCode is < 200 or >= 300) return null;
        if (response.Body.Length > 8_000_000) return null;
        using var document = ProviderJson.Parse(response.Body);
        var array = ProviderJson.Array(ProviderJson.Property(document?.RootElement ?? default, "data"));
        if (array is not { } models) return null;

        var now = DateTimeOffset.UtcNow;
        var entries = new List<ModelCatalogEntry>();
        foreach (var item in models.EnumerateArray())
        {
            var id = ProviderJson.String(ProviderJson.Property(item, "id"));
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = ProviderJson.String(ProviderJson.Property(item, "name")) ?? id;
            var context = ProviderJson.Number(ProviderJson.Property(item, "context_length"));
            var pricing = ProviderJson.Object(ProviderJson.Property(item, "pricing"));
            var input = ParsePrice(pricing, "prompt");
            var output = ParsePrice(pricing, "completion");
            var cacheRead = ParsePrice(pricing, "input_cache_read");
            var cacheWrite = ParsePrice(pricing, "input_cache_write");
            ModelPrice? price = input is { } inputPrice && output is { } outputPrice
                ? new ModelPrice(inputPrice * 1_000_000, (cacheRead ?? inputPrice) * 1_000_000,
                    outputPrice * 1_000_000, cacheWrite is { } writePrice ? writePrice * 1_000_000 : null)
                : null;
            entries.Add(new ModelCatalogEntry(
                ProviderCatalog.NormalizeId(id.Split('/')[0]), id, name, price,
                context is { } length ? length.ToString("0") : null, now, Id, false,
                PricingBasis.PublicCatalog));
        }
        if (entries.Count == 0) return null;
        _lastSnapshot = new ModelPricingSnapshot(entries, now, Id, false,
            response.Header("ETag"), response.Header("Last-Modified"));
        return _lastSnapshot;
    }

    private ModelPricingSnapshot? _lastSnapshot;

    private static double? ParsePrice(JsonElement? pricing, string key)
    {
        var raw = ProviderJson.Number(ProviderJson.Property(pricing ?? default, key));
        return raw is { } value && double.IsFinite(value) && value >= 0 ? value : null;
    }
}

public sealed class CachedModelCatalog : IModelCatalog
{
    private readonly IReadOnlyList<IModelCatalogSource> _sources;
    private readonly string _path;
    private readonly TimeSpan _freshness;
    private readonly IReadOnlyDictionary<string, ModelPricingOverride> _overrides;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModelPricingSnapshot? _snapshot;

    public CachedModelCatalog(IEnumerable<IModelCatalogSource>? sources = null, string? pricingDirectory = null,
        TimeSpan? freshness = null)
    {
        _sources = (sources ?? [new OpenRouterModelCatalogSource()]).ToArray();
        _freshness = freshness ?? TimeSpan.FromHours(24);
        _path = Path.Combine(pricingDirectory ?? UsageMonitorPaths.Current.PricingDirectory, "model-catalog.json");
        var overridePath = Path.Combine(pricingDirectory ?? UsageMonitorPaths.Current.PricingDirectory, "model-overrides.json");
        _overrides = ReadOverrides(overridePath);
    }

    public async Task<ModelPricingSnapshot> GetAsync(string? providerId = null, bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _snapshot ?? await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!forceRefresh && current is { } cached && DateTimeOffset.UtcNow - cached.RetrievedAt <= _freshness)
            {
                // A catalog restored from disk is already a valid live OpenRouter snapshot. Make
                // it available to synchronous provider refreshes before returning it, otherwise
                // they fall through to bundled rates until the next network refresh.
                ModelPricingCatalog.ApplyRemote(cached.Models);
                return Filter(cached, providerId);
            }

            foreach (var source in _sources)
            {
                ModelPricingSnapshot? fresh;
                try
                {
                    fresh = await source.FetchAsync(current is null ? null : new ModelCatalogRequest(current.ETag, current.LastModified, current), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or JsonException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
                {
                    fresh = null;
                }
                if (fresh is null || fresh.Models.Count > 10_000) continue;
                _snapshot = fresh;
                ModelPricingCatalog.ApplyRemote(fresh.Models);
                try
                {
                    await WriteAsync(fresh, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A read-only or locked cache directory must not discard a usable network result.
                }
                return Filter(fresh, providerId);
            }

            var fallback = current ?? new ModelPricingSnapshot(Array.Empty<ModelCatalogEntry>(), DateTimeOffset.MinValue, "bundled", true);
            var stale = fallback with { IsStale = true, Models = fallback.Models.Select(x => x with { IsStale = true }).ToArray() };
            _snapshot = stale;
            ModelPricingCatalog.ApplyRemote(stale.Models);
            return Filter(stale, providerId);
        }
        finally { _gate.Release(); }
    }

    public ModelPrice? ResolvePrice(string providerId, string modelId)
    {
        var normalized = Normalize(modelId);
        if (_overrides.TryGetValue(normalized, out var overrideValue))
        {
            if (overrideValue.Price is not null) return overrideValue.Price;
            if (overrideValue.CanonicalModel is { Length: > 0 } aliasTarget)
                return ModelPricingCatalog.TryResolve(providerId, aliasTarget);
        }
        var direct = ModelPricingCatalog.TryResolve(providerId, modelId);
        if (direct is not null) return direct;
        if (providerId.Equals(ProviderIds.Codex, StringComparison.OrdinalIgnoreCase) &&
            !modelId.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
            return ModelPricingCatalog.TryResolve(providerId, $"openai/{modelId}");
        return null;
    }

    private static IReadOnlyDictionary<string, ModelPricingOverride> ReadOverrides(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, ModelPricingOverride>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(path);
            var values = JsonSerializer.Deserialize<Dictionary<string, ModelPricingOverride>>(json, JsonOptions);
            return values?.ToDictionary(x => Normalize(x.Key), x => x.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ModelPricingOverride>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return new Dictionary<string, ModelPricingOverride>(StringComparer.OrdinalIgnoreCase); }
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static ModelPricingSnapshot Filter(ModelPricingSnapshot snapshot, string? providerId)
        => string.IsNullOrWhiteSpace(providerId)
            ? snapshot
            : snapshot with { Models = snapshot.Models.Where(x => x.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).ToArray() };

    private async Task<ModelPricingSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<ModelPricingSnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return null; }
    }

    private async Task WriteAsync(ModelPricingSnapshot snapshot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
