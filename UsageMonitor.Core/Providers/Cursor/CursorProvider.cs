namespace UsageMonitor.Core.Providers.Cursor;

public sealed class CursorProvider : IUsageProvider
{
    private static readonly ProviderDescriptor Provider = new(
        ProviderIds.Cursor, "Cursor", "cursor", [new ProviderLink("Cursor", "https://cursor.com")]);
    private readonly IProviderFileSystem _files;

    public CursorProvider(IProviderFileSystem? files = null) => _files = files ?? new LocalProviderFileSystem();
    public ProviderDescriptor Descriptor => Provider;

    public Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cursor", "User", "globalStorage")
        };
        var found = roots.Any(root => _files.FileExists(Path.Combine(root, "state.vscdb")));
        return Task.FromResult(found
            ? ProviderSnapshot.Error(Provider,
                "Cursor was detected, but its private usage service is not available through a stable public contract.",
                ProviderErrorCategory.Unsupported)
            : ProviderSnapshot.Error(Provider, "Cursor was not detected on this Windows account.", ProviderErrorCategory.NotInstalled));
    }
}
