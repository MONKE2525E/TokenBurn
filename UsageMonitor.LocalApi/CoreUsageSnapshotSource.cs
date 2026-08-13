using UsageMonitor.Core;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace UsageMonitor.LocalApi;

/// <summary>
/// Adapter from the provider catalog/cache to the transport's redacted snapshot values.  It is kept in
/// LocalApi so Core does not depend on ASP.NET or the wire contract.
/// </summary>
public sealed class CoreUsageSnapshotSource : IUsageSnapshotSource
{
    private readonly IUsageProviderCatalog _catalog;
    private readonly IUsageCache? _cache;
    private readonly ProviderContext _context;
    // Most recent authentication/authorization failure per provider, recorded by the background
    // stale refresh so the UI can surface "needs re-authentication" on top of the last-good cached
    // bars instead of looking healthy forever. Static so the desktop source and the loopback API
    // host (which each construct their own CoreUsageSnapshotSource) observe the same failures.
    private static readonly ConcurrentDictionary<string, (ProviderSnapshot Snapshot, DateTimeOffset At)> _lastAuthFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AuthFailureVisibilityWindow = TimeSpan.FromMinutes(10);
    // One in-flight network refresh per provider, shared by every caller of this source (the
    // desktop refresh loop, API force requests from the popup/CLI, and cache-retry paths). A
    // duplicate refresh — a force landing while the scheduled refresh is running — joins the
    // shared run instead of hitting the provider twice. The shared run does not use a caller token
    // so one caller's cancellation cannot abort the work everyone else is waiting on, but it has
    // its own deadline so a provider cannot hold the dashboard in a loading state forever.
    private readonly ConcurrentDictionary<string, Task<ProviderSnapshot?>> _inFlightRefreshes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _providerRefreshTimeout;

    public CoreUsageSnapshotSource(IUsageProviderCatalog catalog, IUsageCache? cache = null,
        ProviderContext? context = null, TimeSpan? providerRefreshTimeout = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _cache = cache;
        _context = context ?? new ProviderContext();
        _providerRefreshTimeout = providerRefreshTimeout ?? TimeSpan.FromSeconds(30);
        if (_providerRefreshTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(providerRefreshTimeout));
    }

    public IReadOnlySet<string> KnownProviderIds
    {
        get
        {
            var ids = new HashSet<string>(_catalog.Providers.Select(p => p.Descriptor.Id), StringComparer.OrdinalIgnoreCase);
            if (ids.Contains("claude-code")) ids.Add("claude");
            return ids;
        }
    }

    public async Task<IReadOnlyList<UsageSnapshotData>> GetSnapshotsAsync(string? providerId, bool force,
        CancellationToken cancellationToken = default, string? refreshId = null)
    {
        // One correlation identifier per refresh operation. The logger is wrapped so provider and
        // scanner diagnostics inherit the id without every provider needing to pass it around.
        var id = string.IsNullOrWhiteSpace(refreshId) ? Guid.NewGuid().ToString("N")[..8] : refreshId;
        var context = _context with
        {
            Now = DateTimeOffset.UtcNow,
            ForceRefresh = force,
            RefreshId = id,
            Logger = CorrelatingDiagnosticsLogger.Wrap(_context.Logger, id)
        };

        var providers = _catalog.Providers.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var resolved = providerId.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "claude-code" : providerId;
            providers = providers.Where(p => string.Equals(p.Descriptor.Id, resolved, StringComparison.OrdinalIgnoreCase));
        }

        var tasks = providers.Select(p => ReadProviderAsync(p, context, cancellationToken));
        var snapshots = await Task.WhenAll(tasks).ConfigureAwait(false);
        return snapshots.Where(s => s is not null).Cast<UsageSnapshotData>().ToArray();
    }

    /// <summary>
    /// Joins any in-flight network refresh of this provider, or starts one. The shared run is
    /// independent of the caller's cancellation (see the field doc); the caller's own
    /// cancellation is honored by the await.
    /// </summary>
    private Task<ProviderSnapshot?> SharedRefreshAsync(IUsageProvider provider, ProviderContext context)
    {
        var key = provider.Descriptor.Id;
        while (true)
        {
            if (_inFlightRefreshes.TryGetValue(key, out var inFlight)) return inFlight;
            var completion = new TaskCompletionSource<ProviderSnapshot?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var shared = completion.Task;
            if (!_inFlightRefreshes.TryAdd(key, shared)) continue;
            _ = Task.Run(async () =>
            {
                ProviderSnapshot? result = null;
                var timeout = new CancellationTokenSource(_providerRefreshTimeout);
                var disposeTimeoutWhenRefreshCompletes = false;
                try
                {
                    // Run the provider invocation on its own worker so a synchronous local scanner
                    // cannot prevent the timeout from being observed. Providers that honor
                    // cancellation still stop promptly; the bounded await is the final safety net
                    // for a provider that does not.
                    var refreshTask = Task.Run(() => RefreshAsync(provider, context, timeout.Token));
                    try
                    {
                        result = await refreshTask.WaitAsync(_providerRefreshTimeout).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        if (exception is TimeoutException ||
                            exception is OperationCanceledException && timeout.IsCancellationRequested)
                        {
                            timeout.Cancel();
                            result = ProviderSnapshot.Error(provider.Descriptor,
                                "Provider refresh timed out.", ProviderErrorCategory.Network);
                            context.Logger?.Warning("Provider refresh timed out",
                                new Dictionary<string, object?>
                                {
                                    ["providerId"] = provider.Descriptor.Id,
                                    ["timeoutMs"] = _providerRefreshTimeout.TotalMilliseconds
                                });
                            disposeTimeoutWhenRefreshCompletes = true;
                            _ = refreshTask.ContinueWith(task =>
                            {
                                _ = task.Exception;
                                timeout.Dispose();
                            },
                                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                        }
                        else
                        {
                            result = ProviderSnapshot.Error(provider.Descriptor, exception,
                                ProviderErrorCategory.Other);
                            context.Logger?.Warning("Provider refresh failed", exception: exception);
                        }
                    }
                }
                finally
                {
                    if (!disposeTimeoutWhenRefreshCompletes) timeout.Dispose();
                    _inFlightRefreshes.TryRemove(new KeyValuePair<string, Task<ProviderSnapshot?>>(key, shared));
                    completion.TrySetResult(result);
                }
            });
            return shared;
        }
    }

    private async Task<UsageSnapshotData?> ReadProviderAsync(IUsageProvider provider, ProviderContext context,
        CancellationToken cancellationToken)
    {
        var key = $"provider:{provider.Descriptor.Id}";
        var stopwatch = Stopwatch.StartNew();
        var servedFrom = "network";
        try
        {
            ProviderSnapshot? snapshot;
            if (context.ForceRefresh || _cache is null)
            {
                snapshot = await SharedRefreshAsync(provider, context)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                var usedFallbackCache = false;
                if (snapshot is null && _cache is not null)
                {
                    snapshot = await _cache.ReadAsync<ProviderSnapshot>(key, cancellationToken).ConfigureAwait(false);
                    if (snapshot is { } cachedSnapshot && IsUsableCachedSnapshot(cachedSnapshot))
                    {
                        // A failed forced refresh used to return this cache entry as if it were fresh.
                        // Preserve the value, but mark it stale so the UI does not treat an expired
                        // pre-reset timestamp as a confirmed live quota or start a retry loop.
                        snapshot = cachedSnapshot with { Warning = "Refresh failed. Showing cached values." };
                        usedFallbackCache = true;
                        servedFrom = "cache-fallback";
                    }
                }
                if (snapshot is { } refreshedSnapshot && !IsUsableCachedSnapshot(refreshedSnapshot) && _cache is not null)
                {
                    // A manual refresh must never blank a last-good dashboard. Keep the cached
                    // limits visible, but preserve authentication failures as actionable errors.
                    var cached = await _cache.ReadAsync<ProviderSnapshot>(key, cancellationToken).ConfigureAwait(false);
                    if (cached is { } cachedSnapshot && IsUsableCachedSnapshot(cachedSnapshot))
                    {
                        var warning = refreshedSnapshot.Lines.FirstOrDefault(line => line.IsError)?.Text
                            ?? refreshedSnapshot.Warning
                            ?? "Refresh failed. Showing cached values.";
                        snapshot = WithRefreshFailure(cachedSnapshot, refreshedSnapshot, warning);
                        servedFrom = usedFallbackCache ? "cache-fallback" : "cache-repaired";
                    }
                }
                if (snapshot is not null && IsUsableCachedSnapshot(snapshot) && !usedFallbackCache && _cache is not null)
                    await _cache.WriteAsync(key, snapshot, snapshot.RefreshedAt, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var result = await _cache.GetAsync(key,
                    ct => RefreshAndCacheAsync(provider, key, context, ct), cancellationToken).ConfigureAwait(false);
                snapshot = result.Value;
                servedFrom = result.IsFromCache ? "cache" : "network";
                if (snapshot?.ErrorCategory == ProviderErrorCategory.RateLimited)
                {
                    // The cache coordinator has already started the one permitted stale refresh.
                    // Retrying synchronously here turns a provider 429 into two requests per API
                    // read, which is enough to keep Claude throttled indefinitely. Keep the explicit
                    // rate-limit badge visible and let the background attempt replace it only after a
                    // successful response.
                    return Convert(snapshot);
                }
                if (snapshot?.ErrorCategory is not null || snapshot is { } cachedSnapshot && !IsUsableCachedSnapshot(cachedSnapshot))
                {
                    // Older versions could have persisted an error envelope. Do not keep serving that
                    // fossilized failure for five minutes; retry once and only cache a successful value.
                    var cached = snapshot;
                    var refreshed = await SharedRefreshAsync(provider, context with { ForceRefresh = false })
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    servedFrom = "network";
                    if (refreshed is not null && IsUsableCachedSnapshot(refreshed))
                    {
                        snapshot = refreshed;
                        await _cache.WriteAsync(key, snapshot, snapshot.RefreshedAt, cancellationToken).ConfigureAwait(false);
                    }
                    else if (cached is { } staleSnapshot && IsUsableCachedSnapshot(staleSnapshot))
                    {
                        var warning = refreshed?.Lines.FirstOrDefault(line => line.IsError)?.Text
                            ?? refreshed?.Warning
                            ?? "Refresh failed. Showing cached values.";
                        snapshot = refreshed is null
                            ? staleSnapshot with { Warning = warning }
                            : WithRefreshFailure(staleSnapshot, refreshed, warning);
                        servedFrom = "cache-repaired";
                    }
                    else
                    {
                        snapshot = refreshed;
                    }
                }
            }

            // Surface a recent authentication failure on top of the last-good cached bars so the
            // dashboard shows the provider needs re-authentication instead of looking healthy
            // forever. The failure is recorded by the background stale refresh; the bars are kept
            // so the taskbar quota never disappears.
            if (snapshot is not null && IsUsableCachedSnapshot(snapshot) &&
                _lastAuthFailures.TryGetValue(provider.Descriptor.Id, out var failure) &&
                DateTimeOffset.UtcNow - failure.At <= AuthFailureVisibilityWindow)
            {
                var warning = failure.Snapshot.Lines.FirstOrDefault(line => line.IsError)?.Text
                    ?? failure.Snapshot.Warning
                    ?? "Sign-in expired. Re-authenticate to restore live quota.";
                snapshot = WithRefreshFailure(snapshot, failure.Snapshot, warning);
                servedFrom = "cache-auth-stale";
            }

            return snapshot is null
                ? new UsageSnapshotData(provider.Descriptor.Id, provider.Descriptor.DisplayName, null, [], DateTimeOffset.UtcNow)
                {
                    Error = "Provider refresh failed.",
                    ErrorCategory = ProviderErrorCategory.Other.ToString()
                }
                : Convert(snapshot);
        }
        finally
        {
            context.Logger?.Info("Provider read completed",
                new Dictionary<string, object?>
                {
                    ["providerId"] = provider.Descriptor.Id,
                    ["force"] = context.ForceRefresh,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    ["servedFrom"] = servedFrom
                });
        }
    }

    private async Task<ProviderSnapshot?> RefreshAndCacheAsync(IUsageProvider provider, string key,
        ProviderContext context, CancellationToken cancellationToken)
    {
        // Cold-cache and stale-cache refreshes must use the same bounded per-provider gate as
        // forced refreshes. A cache miss otherwise bypassed that gate and could leave both the
        // desktop host and popup waiting forever on a local scanner or provider request.
        var snapshot = await SharedRefreshAsync(provider, context with { ForceRefresh = false })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is not null && snapshot.ErrorCategory is null && _cache is not null)
            await _cache.WriteAsync(key, snapshot, snapshot.RefreshedAt, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async Task<ProviderSnapshot?> RefreshAsync(IUsageProvider provider, ProviderContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await provider.RefreshAsync(context, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                if (IsAuthFailure(snapshot))
                    _lastAuthFailures[provider.Descriptor.Id] = (snapshot, DateTimeOffset.UtcNow);
                else if (snapshot.ErrorCategory is null)
                    _lastAuthFailures.TryRemove(provider.Descriptor.Id, out _);
            }
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed refresh never blanks a previous cached value, but it must remain visible as
            // a redacted provider error. Returning null here used to turn real bugs into a vague
            // "refresh failed" warning and made the root cause impossible to diagnose.
            context.Logger?.Warning("Provider refresh failed", exception: ex);
            return ProviderSnapshot.Error(provider.Descriptor, ex, ProviderErrorCategory.Other);
        }
    }

    private static UsageSnapshotData Convert(ProviderSnapshot snapshot) => new(
        snapshot.ProviderId,
        snapshot.DisplayName,
        snapshot.Plan,
        snapshot.Lines.Select(Convert).ToArray(),
        snapshot.RefreshedAt)
    {
        Error = snapshot.ErrorCategory is not null
            ? SensitiveDataRedactor.Redact(snapshot.Lines.FirstOrDefault(line => line.IsError)?.Text ?? "Provider refresh failed.")
            : null,
        ErrorCategory = snapshot.ErrorCategory?.ToString(),
        Warning = snapshot.Warning is null ? null : SensitiveDataRedactor.Redact(snapshot.Warning),
        UsageHistory = snapshot.UsageHistory is null
            ? null
            : new UsageHistoryData(snapshot.UsageHistory.Points.Select(point =>
                new UsageHistoryPointData(point.Date, point.Tokens, point.CostUsd, point.Estimated)))
            {
                UnknownModels = snapshot.UsageHistory.UnknownModels,
                Breakdown = snapshot.UsageHistory.Breakdown.Select(point => new UsageBreakdownPointData(
                    point.Date, point.ProviderId, point.ModelId,
                    point.UncachedInputTokens, point.CachedInputTokens, point.CacheCreationTokens,
                    point.OutputTokens, point.ReasoningTokens, point.CostUsd,
                    point.CostBasis.ToString(), point.PricingBasis.ToString(), point.Estimated, point.CacheSavingsUsd)).ToArray()
            }
    };

    private static bool IsUsableCachedSnapshot(ProviderSnapshot snapshot) =>
        snapshot.ErrorCategory is null &&
        (!snapshot.ProviderId.Equals("claude-code", StringComparison.OrdinalIgnoreCase) ||
         snapshot.Lines.Any(line => line.Type == MetricLineType.Progress));

    private static bool IsAuthFailure(ProviderSnapshot snapshot) =>
        snapshot.ErrorCategory is ProviderErrorCategory.Authentication or ProviderErrorCategory.Authorization;

    private static ProviderSnapshot WithRefreshFailure(ProviderSnapshot cached,
        ProviderSnapshot failure, string message) => cached with
        {
            // Keep the cached bars, but carry the provider's actual failure category alongside
            // them. The UI can then distinguish signed-out, rate-limited, and network states
            // instead of collapsing every failure into a vague stale label.
            ErrorCategory = failure.ErrorCategory,
            Lines = cached.Lines
                .Where(line => !line.IsError)
                .Append(MetricLine.Badge(MetricLine.ErrorBadgeLabel, message, "#EF4444", state: MetricState.Error))
                .ToArray(),
            Warning = message
        };

    private static UsageMetricData Convert(MetricLine line) => line.Type switch
    {
        MetricLineType.Progress => new ProgressMetricData(
            line.Label, line.Used ?? 0, line.Limit ?? 0, FormatUnit(line.Format),
            line.ResetsAt, line.Period?.Ticks / TimeSpan.TicksPerMillisecond, line.ColorHex),
        MetricLineType.Text => new TextMetricData(line.Label, line.Text ?? string.Empty,
            line.ColorHex, line.Subtitle),
        MetricLineType.Values => new ValuesMetricData(line.Label,
            line.Values.Select(v => new ScalarValueData(v.Number, FormatUnit(v.Kind), v.Label, v.Estimated)),
            line.ColorHex, line.ExpiriesAt),
        MetricLineType.Badge => new BadgeMetricData(line.Label, line.Text ?? string.Empty,
            line.ColorHex, line.Subtitle),
        MetricLineType.Chart => new BarChartMetricData(line.Label,
            line.Points.Select(p => new ChartPointData(p.Label, p.Value, p.ValueLabel)), line.Subtitle),
        _ => new TextMetricData(line.Label, line.Text ?? string.Empty, line.ColorHex, line.Subtitle)
    };

    private static string FormatUnit(MetricKind? format) => format switch
    {
        MetricKind.Dollars => "usd",
        MetricKind.Count => "count",
        MetricKind.Duration => "duration",
        _ => "percent"
    };

}
