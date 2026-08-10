using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageMonitor.Core.Providers;

/// <summary>
/// The normalized, day-bucketed usage a single history source file contributed to the current
/// 90-day window. Storing the already-computed aggregate per source lets a refresh skip parsing
/// unchanged files entirely and only re-parse the files that actually changed.
/// </summary>
public sealed class SourceHistoryContribution
{
    public string Fingerprint { get; set; } = string.Empty;
    public List<UsageHistoryPoint> Points { get; set; } = new();
    public List<UsageBreakdownPoint> Breakdown { get; set; } = new();
    public List<string> UnknownModels { get; set; } = new();
}

/// <summary>
/// Persisted per-provider history index used to make refreshes incremental. An unchanged source
/// (same file length and last-write time) is skipped and its previously computed contribution is
/// reused, so a normal refresh re-parses only the files/databases that actually changed.
/// </summary>
public sealed class ProviderHistoryIndex
{
    public int Version { get; set; } = ProviderHistoryIndexStore.Version;
    public DateTimeOffset StoredAt { get; set; }
    /// <summary>Fingerprint of the pricing catalog when the index was built.</summary>
    public string CatalogFingerprint { get; set; } = string.Empty;
    public Dictionary<string, SourceHistoryContribution> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Loads/saves the per-provider history index. A missing, corrupt, or older-version index falls
/// back to a full rescan of every source, so totals can never silently drift from the real logs.
/// </summary>
public sealed class ProviderHistoryIndexStore
{
    public const int Version = 1;
    private readonly string _directory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public ProviderHistoryIndexStore(string directory)
    {
        _directory = directory;
    }

    public string PathFor(string providerKey) => Path.Combine(_directory, $"history-{providerKey}.json");

    public ProviderHistoryIndex? TryLoad(string providerKey)
    {
        var path = PathFor(providerKey);
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var index = JsonSerializer.Deserialize<ProviderHistoryIndex>(stream, JsonOptions);
            return index is { } loaded && loaded.Version == Version ? loaded : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string providerKey, ProviderHistoryIndex index)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var final = PathFor(providerKey);
            var temporary = final + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(index, JsonOptions));
            File.Move(temporary, final, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or locked cache directory must not break a refresh; the next scan
            // simply re-parses everything.
        }
    }
}

/// <summary>Computes the file fingerprint used to detect unchanged history sources.</summary>
public static class HistorySourceFingerprint
{
    /// <summary>
    /// Returns a fingerprint derived from file length and last-write time. An empty fingerprint
    /// means the file could not be inspected and is always treated as changed.
    /// </summary>
    public static string Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Fingerprint of the pricing catalog files that affect cost estimation. When the catalog is
    /// updated the history index is rebuilt so already-parsed records are re-priced with the new
    /// rates instead of keeping stale estimates forever.
    /// </summary>
    public static string Catalog(string pricingDirectory)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var name in new[] { "model-catalog.json", "model-overrides.json" })
        {
            var path = Path.Combine(pricingDirectory, name);
            if (!File.Exists(path)) continue;
            try
            {
                var info = new FileInfo(path);
                builder.Append(name).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
        return builder.ToString();
    }
}

/// <summary>Counts what a history scan actually inspected versus reused.</summary>
public sealed class HistoryScanReport
{
    public long FilesDiscovered { get; set; }
    public long FilesUnchanged { get; set; }
    public long FilesChanged { get; set; }
    public long RowsRead { get; set; }
    public long Milliseconds { get; set; }
    public DateTimeOffset? OldestRecord { get; set; }
    public DateTimeOffset? NewestRecord { get; set; }

    public long FilesParsed => FilesChanged;
    public long FilesReused => FilesUnchanged;
}

/// <summary>
/// Sums per-source contributions into a single windowed history. The sliding 90-day window is
/// applied at day granularity: a point whose day falls outside the window is dropped, matching the
/// day-level bucketing the scanners already produce.
/// </summary>
public static class HistoryIndexMerge
{
    public static ProviderUsageHistory Merge(
        IEnumerable<SourceHistoryContribution> contributions,
        DateOnly? sinceDate = null)
    {
        var totals = new Dictionary<DateOnly, (double Tokens, double Cost)>();
        var breakdown = new Dictionary<(DateOnly Date, string Model, UsageCostBasis Basis), UsageBreakdownPoint>();
        var unknownModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in contributions)
        {
            foreach (var point in contribution.Points)
            {
                if (sinceDate is { } since && point.Date < since) continue;
                totals.TryGetValue(point.Date, out var prior);
                totals[point.Date] = (prior.Tokens + point.Tokens, prior.Cost + point.CostUsd);
            }
            foreach (var point in contribution.Breakdown)
            {
                if (sinceDate is { } since && point.Date < since) continue;
                var key = (point.Date, point.ModelId ?? string.Empty, point.CostBasis);
                if (breakdown.TryGetValue(key, out var existing))
                    breakdown[key] = Sum(existing, point);
                else
                    breakdown[key] = point;
            }
            foreach (var model in contribution.UnknownModels) unknownModels.Add(model);
        }
        return new ProviderUsageHistory(totals.OrderBy(p => p.Key)
            .Select(p => new UsageHistoryPoint(p.Key, p.Value.Tokens, p.Value.Cost, true)).ToArray())
        {
            UnknownModels = unknownModels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            Breakdown = breakdown.Values
                .OrderBy(point => point.Date)
                .ThenBy(point => point.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static UsageBreakdownPoint Sum(UsageBreakdownPoint a, UsageBreakdownPoint b) => new(
        a.Date,
        a.ProviderId,
        a.ModelId,
        a.UncachedInputTokens + b.UncachedInputTokens,
        a.CachedInputTokens + b.CachedInputTokens,
        a.CacheCreationTokens + b.CacheCreationTokens,
        a.OutputTokens + b.OutputTokens,
        a.ReasoningTokens + b.ReasoningTokens,
        a.CostUsd + b.CostUsd,
        a.CostBasis,
        a.PricingBasis,
        a.Estimated || b.Estimated,
        a.CacheSavingsUsd + b.CacheSavingsUsd);
}
