using System.Collections.Concurrent;
using System.Diagnostics;
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
    // One in-flight refresh per key. The task carries the refresh outcome so waiters share it
    // instead of re-hitting the provider: the cold-miss winner sets the produced value (which may
    // be a non-persisted provider error), and background stale refreshes set null (their value is
    // persisted to disk, so waiters re-read the file).
    private readonly ConcurrentDictionary<string, Task<object?>> _refreshes = new(StringComparer.Ordinal);

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

        // Cold miss: two concurrent readers (taskbar, popup, CLI) must not both hit the provider
        // for the same empty cache key. The first caller registers itself in the refresh map and
        // runs the refresh with no caller token (the shared refresh must not be aborted by one
        // waiter's short timeout); every other caller waits for it and shares its outcome. If the
        // winner produced no value, waiters re-read once before concluding the same.
        while (true)
        {
            if (_refreshes.TryGetValue(key, out var inFlight))
            {
                object? shared;
                try
                {
                    shared = await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new CacheReadResult<T>(default, null, false, false, false);
                }
                if (shared is { } value)
                {
                    if (shared is not T typed)
                        return new CacheReadResult<T>(default, null, false, false, false);
                    return new CacheReadResult<T>(typed, _clock.UtcNow, false, false, false);
                }
                envelope = await ReadEnvelopeAsync<T>(key, cancellationToken).ConfigureAwait(false);
                if (envelope is not null)
                    return new CacheReadResult<T>(envelope.Value, envelope.StoredAt, true, false, false);
                // The winner already ran the provider refresh and produced nothing; re-running it
                // here would serialize one provider hit per waiter instead of sharing the outcome.
                return new CacheReadResult<T>(default, null, false, false, false);
            }

            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_refreshes.TryAdd(key, completion.Task)) continue;
            object? produced = null;
            try
            {
                var value = await refresh(CancellationToken.None).ConfigureAwait(false);
                produced = value;
                if (value is not null)
                {
                    if (ShouldPersist(value))
                        // The persistence write is part of the shared refresh outcome: like the
                        // refresh itself it must not be aborted by an individual caller's
                        // cancellation, or waiters would receive the value while the winner throws.
                        await WriteAsync(key, value, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    return new CacheReadResult<T>(value, _clock.UtcNow, false, false, false);
                }

                return new CacheReadResult<T>(default, null, false, false, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning("Cache refresh failed", new Dictionary<string, object?> { ["cacheKey"] = LogKey(key) }, ex);
                return new CacheReadResult<T>(default, null, false, false, false, ex);
            }
            finally
            {
                // Publish the shared outcome before removing the in-flight entry: a reader arriving
                // between the two operations would otherwise miss the entry and start a duplicate
                // refresh of the work that just completed.
                completion.TrySetResult(produced);
                _refreshes.TryRemove(new KeyValuePair<string, Task<object?>>(key, completion.Task));
            }
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
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_refreshes.TryAdd(key, completion.Task)) return false;
        _ = Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var value = await refresh(CancellationToken.None).ConfigureAwait(false);
                if (value is not null && ShouldPersist(value))
                    await WriteAsync(key, value, _clock.UtcNow).ConfigureAwait(false);
                _logger.Debug("Background cache refresh completed",
                    new Dictionary<string, object?> { ["cacheKey"] = LogKey(key), ["elapsedMs"] = stopwatch.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                _logger.Warning("Background cache refresh failed", new Dictionary<string, object?> { ["cacheKey"] = LogKey(key) }, ex);
            }
            finally
            {
                // null marks "persisted to disk / nothing to share"; cold-miss waiters re-read.
                _refreshes.TryRemove(new KeyValuePair<string, Task<object?>>(key, completion.Task));
                completion.TrySetResult(null);
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
