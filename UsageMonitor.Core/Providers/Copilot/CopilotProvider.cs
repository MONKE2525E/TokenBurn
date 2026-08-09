namespace UsageMonitor.Core.Providers.Copilot;

public sealed class CopilotProvider : IUsageProvider
{
    private static readonly ProviderDescriptor Provider = new(
        ProviderIds.Copilot, "Copilot", "copilot", [new ProviderLink("GitHub Copilot", "https://github.com/features/copilot")]);
    private readonly IProviderFileSystem _files;

    public CopilotProvider(IProviderFileSystem? files = null) => _files = files ?? new LocalProviderFileSystem();
    public ProviderDescriptor Descriptor => Provider;

    public Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "globalStorage", "github.copilot-chat"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "github.copilot-chat")
        };
        var found = roots.Any(root => _files.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any())
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        return Task.FromResult(found
            ? ProviderSnapshot.Error(Provider,
                "Copilot was detected, but account-level AI credit usage requires a GitHub billing scope that is not configured.",
                ProviderErrorCategory.Unsupported)
            : ProviderSnapshot.Error(Provider, "GitHub Copilot was not detected on this Windows account.", ProviderErrorCategory.NotConfigured));
    }
}
