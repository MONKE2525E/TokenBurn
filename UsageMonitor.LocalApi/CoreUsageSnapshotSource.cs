using UsageMonitor.Core;
using System.Text.RegularExpressions;

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

    public CoreUsageSnapshotSource(IUsageProviderCatalog catalog, IUsageCache? cache = null,
        ProviderContext? context = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _cache = cache;
        _context = context ?? new ProviderContext();
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
        CancellationToken cancellationToken = default)
    {
        var providers = _catalog.Providers.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var resolved = providerId.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "claude-code" : providerId;
            providers = providers.Where(p => string.Equals(p.Descriptor.Id, resolved, StringComparison.OrdinalIgnoreCase));
        }

        var tasks = providers.Select(p => ReadProviderAsync(p, force, cancellationToken));
        var snapshots = await Task.WhenAll(tasks).ConfigureAwait(false);
        return snapshots.Where(s => s is not null).Cast<UsageSnapshotData>().ToArray();
    }

    private async Task<UsageSnapshotData?> ReadProviderAsync(IUsageProvider provider, bool force,
        CancellationToken cancellationToken)
    {
        var key = $"provider:{provider.Descriptor.Id}";
        ProviderSnapshot? snapshot;
        if (force || _cache is null)
        {
            snapshot = await RefreshAsync(provider, force, cancellationToken).ConfigureAwait(false);
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
                }
            }
            if (snapshot is not null && IsUsableCachedSnapshot(snapshot) && !usedFallbackCache && _cache is not null)
                await _cache.WriteAsync(key, snapshot, snapshot.RefreshedAt, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var result = await _cache.GetAsync(key,
                ct => RefreshAndCacheAsync(provider, key, ct), cancellationToken).ConfigureAwait(false);
            snapshot = result.Value;
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
                var refreshed = await RefreshAsync(provider, false, cancellationToken).ConfigureAwait(false);
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
                }
                else
                {
                    snapshot = refreshed;
                }
            }
        }

        return snapshot is null
            ? new UsageSnapshotData(provider.Descriptor.Id, provider.Descriptor.DisplayName, null, [], DateTimeOffset.UtcNow)
            {
                Error = "Provider refresh failed.",
                ErrorCategory = ProviderErrorCategory.Other.ToString()
            }
            : Convert(snapshot);
    }

    private async Task<ProviderSnapshot?> RefreshAndCacheAsync(IUsageProvider provider, string key,
        CancellationToken cancellationToken)
    {
        var snapshot = await RefreshAsync(provider, false, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null && snapshot.ErrorCategory is null && _cache is not null)
            await _cache.WriteAsync(key, snapshot, snapshot.RefreshedAt, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async Task<ProviderSnapshot?> RefreshAsync(IUsageProvider provider, bool force,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = _context with { Now = DateTimeOffset.UtcNow, ForceRefresh = force };
            return await provider.RefreshAsync(context, cancellationToken).ConfigureAwait(false);
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
            _context.Logger?.Warning("Provider refresh failed", exception: ex);
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
            ? RedactError(snapshot.Lines.FirstOrDefault(line => line.IsError)?.Text ?? "Provider refresh failed.")
            : null,
        ErrorCategory = snapshot.ErrorCategory?.ToString(),
        Warning = snapshot.Warning is null ? null : RedactError(snapshot.Warning),
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

    private static string RedactError(string message) => Regex.Replace(message,
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", "[redacted-email]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
