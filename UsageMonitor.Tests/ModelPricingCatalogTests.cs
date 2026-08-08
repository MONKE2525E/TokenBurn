using UsageMonitor.Core;

namespace UsageMonitor.Tests;

public sealed class ModelPricingCatalogTests
{
    [Fact]
    public void Known_model_families_use_conservative_rates()
    {
        var mini = ModelPricingCatalog.Resolve("gpt-5-mini");
        var claude = ModelPricingCatalog.Resolve("claude-sonnet");

        Assert.Equal(.25, mini.InputPerMillion);
        Assert.Equal(2, mini.OutputPerMillion);
        Assert.Equal(3, claude.InputPerMillion);
        Assert.Equal(15, claude.OutputPerMillion);
        Assert.Equal(3.75, claude.CacheCreationPerMillion);
    }

    [Fact]
    public void Pricing_is_local_only_and_does_not_require_a_provider_key()
    {
        var unknown = ModelPricingCatalog.Resolve("a-new-custom-model");

        Assert.True(unknown.InputPerMillion > 0);
        Assert.True(unknown.OutputPerMillion > 0);
    }
}
