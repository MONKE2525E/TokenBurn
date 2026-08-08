using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageMonitor.Core;

/// <summary>
/// A small process-safe JSON cache. Values are written to a sibling temporary file and atomically
/// moved into place, so a crash cannot leave a half-written snapshot. Reads older than Freshness are
/// returned immediately while one background refresh is scheduled per key.
/// </summary>
public sealed class JsonFileUsageCache : IUsageCache, IDisposable
{
    private sealed record CacheEnvelope<T>(int Version, DateTimeOffset StoredAt, T Value);

    private readonly string _directory;
    private readonly IClock _clock;
    private readonly IDiagnosticsLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _refreshes = new(StringComparer.Ordinal);

    public JsonFileUsageCache(string? directory = null, TimeSpan? freshness = null,
        IClock? clock = null, IDiagnosticsLogger? logger = null)
    {
        _directory = Path.GetFullPath(directory ?? UsageMonitorPaths.Current.CacheDirectory);
        Freshness = freshness ?? TimeSpan.FromMinutes(5);
        if (Freshness < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(freshness));
        _clock = clock ?? new SystemClock();
        _logger = logger ?? NullDiagnosticsLogger.Instance;
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public TimeSpan Freshness { get; }

    public async Task<CacheReadResult<T>> GetAsync<T>(string key,
        Func<CancellationToken, Task<T?>> refresh,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(refresh);
        var envelope = await ReadEnvelopeAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (envelope is not null)
        {
            var stale = _clock.UtcNow - envelope.StoredAt > Freshness;
            if (!stale)
            {
                return new CacheReadResult<T>(envelope.Value, envelope.StoredAt, true, false, false);
            }

            var refreshStarted = StartRefresh(key, refresh);
            return new CacheReadResult<T>(envelope.Value, envelope.StoredAt, true, true, refreshStarted);
        }

        try
        {
            var value = await refresh(cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                if (ShouldPersist(value))
                    await WriteAsync(key, value, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                return new CacheReadResult<T>(value, _clock.UtcNow, false, false, false);
            }

            return new CacheReadResult<T>(default, null, false, false, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning("Cache refresh failed", new Dictionary<string, object?> { ["cacheKey"] = LogKey(key) }, ex);
            return new CacheReadResult<T>(default, null, false, false, false, ex);
        }
    }

    public async Task<T?> ReadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var envelope = await ReadEnvelopeAsync<T>(key, cancellationToken).ConfigureAwait(false);
        return envelope is null ? default : envelope.Value;
    }

    public async Task WriteAsync<T>(string key, T value, DateTimeOffset? storedAt = null,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        Directory.CreateDirectory(_directory);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = new CacheEnvelope<T>(1, storedAt ?? _clock.UtcNow, value);
            var json = JsonSerializer.Serialize(envelope, _jsonOptions);
            var finalPath = GetPath(key);
            var temporaryPath = finalPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, finalPath, true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { TryDelete(GetPath(key)); }
        finally { gate.Release(); }
    }

    private bool StartRefresh<T>(string key, Func<CancellationToken, Task<T?>> refresh)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_refreshes.TryAdd(key, completion.Task)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                var value = await refresh(CancellationToken.None).ConfigureAwait(false);
                if (value is not null && ShouldPersist(value))
                    await WriteAsync(key, value, _clock.UtcNow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning("Background cache refresh failed", new Dictionary<string, object?> { ["cacheKey"] = LogKey(key) }, ex);
            }
            finally
            {
                _refreshes.TryRemove(new KeyValuePair<string, Task>(key, completion.Task));
                completion.TrySetResult();
            }
        });
        return true;
    }

    private async Task<CacheEnvelope<T>?> ReadEnvelopeAsync<T>(string key, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<CacheEnvelope<T>>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warning("Cache entry could not be read", new Dictionary<string, object?> { ["cacheKey"] = LogKey(key) }, ex);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_directory, hash + ".json");
    }

    private static string LogKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();

    /// <summary>
    /// Provider errors are observations, not cache values. Persisting an authentication or rate-limit
    /// envelope would replace the last good limits and make every later launch look permanently broken.
    /// Keep the generic cache behavior unchanged for ordinary values used by the core tests and CLI.
    /// </summary>
    private static bool ShouldPersist<T>(T value) => value switch
    {
        ProviderSnapshot snapshot => snapshot.ErrorCategory is null,
        _ => true
    };

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A cache key is required.", nameof(key));
        if (key.Length > 512) throw new ArgumentException("Cache keys are limited to 512 characters.", nameof(key));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        foreach (var gate in _locks.Values) gate.Dispose();
        _locks.Clear();
    }
}
