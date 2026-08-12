using System.Diagnostics;
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
    /// <summary>Boundary day the sources were parse-time filtered with. A later value than the
    /// current window boundary means the window moved backward and the cached contributions are
    /// missing days, so the index must be rebuilt.</summary>
    public DateOnly SinceDate { get; set; }
    /// <summary>Timezone id the contributions were day-bucketed with. A change invalidates every
    /// day label, so the index must be rebuilt.</summary>
    public string TimeZoneId { get; set; } = string.Empty;
    public Dictionary<string, SourceHistoryContribution> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Loads/saves the per-provider history index. A missing, corrupt, or older-version index falls
/// back to a full rescan of every source, so totals can never silently drift from the real logs.
/// </summary>
public sealed class ProviderHistoryIndexStore
{
    // Version 2: sources are parse-time filtered at local-day granularity (and the index records
    // the window boundary day and bucketing timezone). Version 1 indices were filtered at the
    // `since` instant, so reusing them would silently disagree with fresh parses on the boundary
    // day; they are rejected and rebuilt.
    public const int Version = 2;
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

    public void Track(DateTimeOffset? timestamp)
    {
        if (timestamp is not { } value) return;
        if (OldestRecord is null || value < OldestRecord) OldestRecord = value;
        if (NewestRecord is null || value > NewestRecord) NewestRecord = value;
    }
}

/// <summary>
/// Shared orchestration behind every incremental history scan: load the persisted index, reset it
/// when the pricing catalog changed, drop sources that disappeared, skip sources whose fingerprint
/// is unchanged, parse the rest, merge, run the provider-specific post-merge step, and persist.
/// The sliding window is applied at local-day granularity everywhere: the merge drops points whose
/// local day is before the cutoff day, and <paramref name="parseFile"/> must apply the identical
/// day predicate so a freshly parsed file and a cached contribution can never disagree about which
/// records belong to the boundary day.
/// </summary>
public static class IncrementalHistoryScan
{
    public static ProviderUsageHistory Run(
        string providerKey,
        IReadOnlyList<string> files,
        string? historyStoreDirectory,
        DateTimeOffset since,
        Func<string, DateTimeOffset, DateOnly, HistoryScanReport, SourceHistoryContribution> parseFile,
        Func<ProviderUsageHistory, HistoryScanReport, ProviderUsageHistory>? afterMerge,
        Action<HistoryScanReport>? report,
        TimeZoneInfo? localTimeZone = null)
    {
        var timeZone = localTimeZone ?? TimeZoneInfo.Local;
        var sinceDate = SinceDate(since, timeZone);
        var stopwatch = Stopwatch.StartNew();
        var scanReport = new HistoryScanReport();
        var store = string.IsNullOrWhiteSpace(historyStoreDirectory)
            ? null
            : new ProviderHistoryIndexStore(historyStoreDirectory);
        var index = store?.TryLoad(providerKey) ?? new ProviderHistoryIndex();
        if (store is not null)
        {
            var catalogFingerprint = HistorySourceFingerprint.Catalog(UsageMonitorPaths.Current.PricingDirectory);
            if (index.CatalogFingerprint.Length > 0 && index.CatalogFingerprint != catalogFingerprint)
                index = new ProviderHistoryIndex { CatalogFingerprint = catalogFingerprint };
            // Contributions are parse-time filtered at the then-current window boundary, so a
            // window that moved backward (clock rollback, shortened interval) leaves cached
            // contributions permanently missing the days that re-entered the window. A timezone
            // change re-labels every day, so bucketed points from the old zone must be rebuilt.
            // The empty-string sentinel keeps freshly created indices (and any future format that
            // omits the field) compatible.
            if (index.SinceDate > sinceDate ||
                (index.TimeZoneId.Length > 0 && !string.Equals(index.TimeZoneId, timeZone.Id, StringComparison.Ordinal)))
                index = new ProviderHistoryIndex { CatalogFingerprint = catalogFingerprint };
            index.CatalogFingerprint = catalogFingerprint;
            index.SinceDate = sinceDate;
            index.TimeZoneId = timeZone.Id;
        }

        scanReport.FilesDiscovered = files.Count;
        var knownPaths = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var stalePath in index.Sources.Keys.Where(path => !knownPaths.Contains(path)).ToArray())
            index.Sources.Remove(stalePath);

        foreach (var path in files)
        {
            var fingerprint = store is null ? string.Empty : HistorySourceFingerprint.Of(path);
            if (index.Sources.TryGetValue(path, out var existing) && existing.Fingerprint == fingerprint)
            {
                scanReport.FilesUnchanged++;
                continue;
            }
            scanReport.FilesChanged++;
            index.Sources[path] = parseFile(path, since, sinceDate, scanReport);
        }

        var merged = HistoryIndexMerge.Merge(index.Sources.Values, sinceDate: sinceDate);
        if (afterMerge is not null) merged = afterMerge(merged, scanReport);
        if (store is not null) store.Save(providerKey, index);
        scanReport.Milliseconds = stopwatch.ElapsedMilliseconds;
        report?.Invoke(scanReport);
        return merged;
    }

    /// <summary>The local calendar day a timestamp falls into for the configured timezone.</summary>
    public static DateOnly DayOf(DateTimeOffset value, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, timeZone).Date);

    /// <summary>Start of the local day that contains <paramref name="since"/> (the window boundary day).</summary>
    public static DateOnly SinceDate(DateTimeOffset since, TimeZoneInfo timeZone) =>
        DayOf(since, timeZone);

    /// <summary>
    /// The UTC instant of the first moment of <paramref name="day"/> in the local timezone, used by
    /// SQL-side sources that cannot filter by day without an explicit cutoff. DST transitions at
    /// local midnight are handled: on fall-back days the earlier (repeated) midnight wins so the
    /// whole boundary day is inside the window, and on spring-forward days the cutoff is the first
    /// valid local time after the gap so the previous day cannot leak in.
    /// </summary>
    public static long UtcSecondsAtLocalMidnight(DateOnly day, TimeZoneInfo timeZone)
    {
        var localMidnight = day.ToDateTime(TimeOnly.MinValue);
        // Spring-forward at midnight (e.g. America/Havana): local midnight never occurs, so the
        // day's first instant is the first valid local time after the gap.
        while (timeZone.IsInvalidTime(localMidnight)) localMidnight = localMidnight.AddHours(1);
        // Fall-back with midnight repeated (e.g. America/Havana): the larger offset is the earlier
        // instant, which keeps the whole repeated first hour inside the window.
        var offset = timeZone.IsAmbiguousTime(localMidnight)
            ? timeZone.GetAmbiguousTimeOffsets(localMidnight).Max()
            : timeZone.GetUtcOffset(localMidnight);
        return new DateTimeOffset(localMidnight, offset).ToUnixTimeSeconds();
    }
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
