namespace UsageMonitor.Core;

/// <summary>Inputs shared by providers for a refresh operation.</summary>
public sealed record ProviderContext
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    public Uri? Proxy { get; init; }
    public ISecretStore? Secrets { get; init; }
    public IDiagnosticsLogger? Logger { get; init; }
    public IModelCatalog? ModelCatalog { get; init; }
    public string? ConfigDirectory { get; init; }
    public bool ForceRefresh { get; init; }
}

public sealed record ModelCatalogRequest(
    string? ETag = null,
    string? LastModified = null,
    ModelPricingSnapshot? CachedSnapshot = null);

public interface IUsageProvider
{
    ProviderDescriptor Descriptor { get; }
    Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default);
}

public interface IUsageProviderCatalog
{
    IReadOnlyList<IUsageProvider> Providers { get; }
    IUsageProvider? Find(string providerId);
}

public interface IStatusSurface
{
    string Id { get; }
    bool IsVisible { get; }
    Task ShowAsync(CancellationToken cancellationToken = default);
    Task HideAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(IReadOnlyList<ProviderSnapshot> snapshots, CancellationToken cancellationToken = default);
}

/// <summary>Results returned by a stale-while-revalidate cache read.</summary>
public sealed record CacheReadResult<T>(
    T? Value,
    DateTimeOffset? StoredAt,
    bool IsFromCache,
    bool IsStale,
    bool RefreshStarted,
    Exception? RefreshError = null)
{
    public bool HasValue => Value is not null;
}

public interface IUsageCache
{
    TimeSpan Freshness { get; }
    Task<CacheReadResult<T>> GetAsync<T>(string key,
        Func<CancellationToken, Task<T?>> refresh,
        CancellationToken cancellationToken = default);
    Task<T?> ReadAsync<T>(string key, CancellationToken cancellationToken = default);
    Task WriteAsync<T>(string key, T value, DateTimeOffset? storedAt = null,
        CancellationToken cancellationToken = default);
    Task InvalidateAsync(string key, CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string secret, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public interface IDiagnosticsLogger
{
    void Debug(string message, IReadOnlyDictionary<string, object?>? data = null);
    void Info(string message, IReadOnlyDictionary<string, object?>? data = null);
    void Warning(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null);
    void Error(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null);
}

public interface IMonitorPlacementService
{
    string PrimaryMonitorId { get; }
    IReadOnlyList<MonitorDescriptor> GetMonitors();
    string ResolveMonitor(string? requestedId);
}

public sealed record MonitorDescriptor(
    string Id,
    string DisplayName,
    bool IsPrimary,
    int BoundsLeft,
    int BoundsTop,
    int BoundsWidth,
    int BoundsHeight,
    double DpiScaleX = 1,
    double DpiScaleY = 1);
