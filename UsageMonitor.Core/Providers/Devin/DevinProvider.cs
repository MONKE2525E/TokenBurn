namespace UsageMonitor.Core.Providers.Devin;

public sealed class DevinProvider : IUsageProvider
{
    private static readonly ProviderDescriptor Provider = new(
        ProviderIds.Devin, "Devin", "devin", [new ProviderLink("Devin", "https://devin.ai")]);

    public ProviderDescriptor Descriptor => Provider;

    public Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEVIN_API_KEY"));
        return Task.FromResult(configured
            ? ProviderSnapshot.Error(Provider,
                "Devin credentials were detected, but no stable supported quota endpoint is configured.",
                ProviderErrorCategory.Unsupported)
            : ProviderSnapshot.Error(Provider, "Devin was not configured on this Windows account.", ProviderErrorCategory.NotInstalled));
    }
}
