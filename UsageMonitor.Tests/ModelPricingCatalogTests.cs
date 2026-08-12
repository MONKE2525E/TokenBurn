using UsageMonitor.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageMonitor.Tests;

/// <summary>
/// Serializes every test that reads or mutates <see cref="ModelPricingCatalog"/>'s process-wide
/// static state. CachedModelCatalog writes into that static via ApplyRemote, and provider tests
/// resolve built-in/remote prices through TryResolve, so the mutators and readers must never run
/// concurrently under xUnit's per-class parallelization.
/// </summary>
[CollectionDefinition("model-pricing-static", DisableParallelization = true)]
public sealed class ModelPricingStaticCollection { }

[Collection("model-pricing-static")]
public sealed class ModelPricingCatalogTests
{
    [Fact]
    public void Known_model_families_use_conservative_rates()
    {
        var mini = Assert.IsType<ModelPrice>(ModelPricingCatalog.TryResolve("gpt-5-mini"));
        var claude = Assert.IsType<ModelPrice>(ModelPricingCatalog.TryResolve("claude-sonnet"));

        Assert.Equal(.25, mini.InputPerMillion);
        Assert.Equal(2, mini.OutputPerMillion);
        Assert.Equal(3, claude.InputPerMillion);
        Assert.Equal(15, claude.OutputPerMillion);
        Assert.Equal(3.75, claude.CacheCreationPerMillion);
    }

    [Fact]
    public void UnknownModelsStayExplicitlyUnpriced()
    {
        Assert.Null(ModelPricingCatalog.TryResolve("a-new-custom-model"));
    }

    [Fact]
    public async Task OpenRouterCatalogParsesLivePricingAndCacheWriteWithoutCredentials()
    {
        var http = new FakeHttpClient("{\"data\":[{\"id\":\"openai/gpt-test\",\"name\":\"GPT Test\",\"context_length\":128000,\"pricing\":{\"prompt\":\"0.000001\",\"input_cache_read\":\"0.0000001\",\"input_cache_write\":\"0.00000125\",\"completion\":\"0.000002\"}}]}");
        var source = new OpenRouterModelCatalogSource(http, new Uri("https://fixture/models"));
        var snapshot = await source.FetchAsync();
        var model = Assert.Single(snapshot!.Models);
        Assert.Equal("GPT Test", model.DisplayName);
        Assert.Equal(1d, model.Price!.InputPerMillion, 6);
        Assert.Equal(.1d, model.Price.CachedInputPerMillion, 6);
        Assert.Equal(1.25d, model.Price.CacheCreationPerMillion!.Value, 6);
        Assert.Equal(2d, model.Price.OutputPerMillion, 6);
        Assert.Equal(PricingBasis.PublicCatalog, model.Basis);
    }

    [Fact]
    public void KnownOpenCodeModelsHaveCashRateFallbacksButUnknownModelsStayUnpriced()
    {
        var deepseek = ModelPricingCatalog.TryResolve("deepseek-v4-flash");
        Assert.NotNull(deepseek);
        Assert.Equal(.14, deepseek!.InputPerMillion, 6);
        var free = ModelPricingCatalog.TryResolve("deepseek-v4-flash-free");
        Assert.NotNull(free);
        Assert.Equal(0, free!.InputPerMillion, 6);
        Assert.Null(ModelPricingCatalog.TryResolve("a-new-custom-model"));
    }

    [Fact]
    public async Task OpenCodeGoRatesArePinnedAgainstTheRemoteCatalog()
    {
        // The live OpenRouter catalog prices deepseek cache reads far higher than OpenCode Go
        // bills. The pin must win so OpenCode spend matches the OpenCode dashboard.
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = new ModelPricingSnapshot([
                new ModelCatalogEntry("deepseek", "deepseek/deepseek-v4-flash", "DeepSeek V4 Flash",
                    new ModelPrice(.14, .028, .28), null, DateTimeOffset.UtcNow, "openrouter", false,
                    PricingBasis.PublicCatalog)
            ], DateTimeOffset.UtcNow, "openrouter", false);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            };
            await File.WriteAllTextAsync(Path.Combine(root, "model-catalog.json"), JsonSerializer.Serialize(snapshot, options));

            var catalog = new CachedModelCatalog([], root);
            await catalog.GetAsync();

            var pinned = catalog.ResolvePrice("opencode-go", "deepseek-v4-flash");
            Assert.NotNull(pinned);
            Assert.Equal(.0028, pinned!.CachedInputPerMillion, 6);
            var free = catalog.ResolvePrice("opencode", "deepseek-v4-flash-free");
            Assert.NotNull(free);
            Assert.Equal(0, free!.InputPerMillion, 6);
        }
        finally
        {
            // ApplyRemote is a static, process-wide cache. Reset it so this fixture's entries
            // cannot leak into other tests via family matching.
            ModelPricingCatalog.ApplyRemote([]);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FreshCatalogRestoredFromDiskIsUsedBeforeAnyNetworkRefresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = new ModelPricingSnapshot([
                new ModelCatalogEntry("moonshotai", "moonshotai/new-live-model", "New Live Model",
                    new ModelPrice(1, .1, 2), null, DateTimeOffset.UtcNow, "openrouter", false,
                    PricingBasis.PublicCatalog)
            ], DateTimeOffset.UtcNow, "openrouter", false);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            };
            await File.WriteAllTextAsync(Path.Combine(root, "model-catalog.json"), JsonSerializer.Serialize(snapshot, options));

            var catalog = new CachedModelCatalog([], root);
            await catalog.GetAsync();

            var price = catalog.ResolvePrice("opencode-go", "new-live-model");
            Assert.NotNull(price);
            Assert.Equal(1, price!.InputPerMillion, 6);
            Assert.Equal(2, price.OutputPerMillion, 6);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeHttpClient(string body) : UsageMonitor.Core.Providers.IProviderHttpClient
    {
        public Task<UsageMonitor.Core.Providers.ProviderHttpResponse> SendAsync(HttpMethod method, Uri uri,
            IReadOnlyDictionary<string, string>? headers = null, string? bodyOverride = null,
            string? contentType = null, Uri? proxy = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new UsageMonitor.Core.Providers.ProviderHttpResponse(200,
                new Dictionary<string, string>(), body));
    }
}
