using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace UsageMonitor.Core.Providers.Codex;

public sealed class CodexLogUsageScanner
{
    private static readonly Regex DiagnosticsUsagePattern = new(
        @"\btotal_usage_tokens=(?<tokens>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DiagnosticsModelPattern = new(
        @"\bmodel=(?<model>[^\s}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IProviderFileSystem _files;
    private readonly IModelCatalog _catalog;
    public CodexLogUsageScanner(IProviderFileSystem? files = null, IModelCatalog? catalog = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _catalog = catalog ?? new CachedModelCatalog();
    }

    public static ProviderUsageHistory ScanDirectory(string codexHome, DateTimeOffset since) => new CodexLogUsageScanner().Scan(codexHome, since);

    public ProviderUsageHistory Scan(string codexHome, DateTimeOffset since)
        => Scan(codexHome, since, null, null);

    /// <summary>
    /// Incremental history scan. When <paramref name="historyStoreDirectory"/> is provided, files
    /// whose length and last-write time are unchanged since the previous scan are skipped and their
    /// previously computed contribution is reused, so a refresh re-parses only what actually
    /// changed. Without a store directory (CLI, tests) every file is parsed every time.
    /// </summary>
    public ProviderUsageHistory Scan(string codexHome, DateTimeOffset since,
        string? historyStoreDirectory, Action<HistoryScanReport>? report)
    {
        var stopwatch = Stopwatch.StartNew();
        var scanReport = new HistoryScanReport();
        var store = string.IsNullOrWhiteSpace(historyStoreDirectory)
            ? null
            : new ProviderHistoryIndexStore(historyStoreDirectory);
        var index = store?.TryLoad("codex") ?? new ProviderHistoryIndex();
        if (store is not null)
        {
            var catalogFingerprint = HistorySourceFingerprint.Catalog(UsageMonitorPaths.Current.PricingDirectory);
            if (index.CatalogFingerprint.Length > 0 && index.CatalogFingerprint != catalogFingerprint)
                index = new ProviderHistoryIndex { CatalogFingerprint = catalogFingerprint };
            index.CatalogFingerprint = catalogFingerprint;
        }

        var files = DiscoverSessionFiles(codexHome).ToArray();
        scanReport.FilesDiscovered = files.Length;
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
            index.Sources[path] = ParseSessionFile(path, since, scanReport);
        }

        var merged = HistoryIndexMerge.Merge(index.Sources.Values, sinceDate: DateOnly.FromDateTime(since.LocalDateTime));
        var totals = merged.Points.ToDictionary(p => p.Date, p => (Tokens: p.Tokens, Cost: p.CostUsd));
        var breakdown = merged.Breakdown.ToDictionary(p => (p.Date, Model: p.ModelId ?? string.Empty, p.CostBasis), p => p);
        var unknownModels = new HashSet<string>(merged.UnknownModels, StringComparer.OrdinalIgnoreCase);
        ScanDiagnosticsDatabase(codexHome, since, totals, breakdown, unknownModels, scanReport);
        if (store is not null) store.Save("codex", index);
        scanReport.Milliseconds = stopwatch.ElapsedMilliseconds;
        report?.Invoke(scanReport);
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

    private SourceHistoryContribution ParseSessionFile(string path, DateTimeOffset since, HistoryScanReport report)
    {
        // Capture the fingerprint before reading. If the file is appended to mid-parse the stored
        // fingerprint will not match the file's final state, so the next scan re-parses it instead
        // of permanently missing the appended records.
        var fingerprint = HistorySourceFingerprint.Of(path);
        var totals = new Dictionary<DateOnly, (double Tokens, double Cost)>();
        var breakdown = new Dictionary<(DateOnly Date, string Model, UsageCostBasis Basis), UsageBreakdownPoint>();
        var unknownModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? model = null;
        RawUsage? previousTotals = null;
        var sawSessionMeta = false;
        var replayActive = false;
        DateTimeOffset? replayCreatedAt = null;
        // Session logs are append-only JSONL and can grow very large. Read one record at a
        // time so local spend refreshes remain bounded by the current record, not file size.
        foreach (var raw in _files.ReadLinesContaining(path,
                     "token_count", "session_meta", "task_started", "turn_context"))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var doc = ProviderJson.Parse(raw.Trim());
            if (doc is null) continue;
            var root = doc.RootElement;
            var type = ProviderJson.String(ProviderJson.Property(root, "type"));
            var payload = ProviderJson.Object(ProviderJson.Property(root, "payload")) ?? root;

            // Child sessions replay their parent's complete token history at spawn. The replay
            // can span many seconds, so timestamp heuristics are unsafe. Gate it until the
            // first task_started whose started_at belongs to the child, while still seeding the
            // cumulative baseline so total-only records after the gate become true deltas.
            if (string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase) && !sawSessionMeta)
            {
                sawSessionMeta = true;
                if (IsChildSessionMeta(payload))
                {
                    replayActive = true;
                    replayCreatedAt = ProviderJson.Date(ProviderJson.Property(root, "timestamp", "created_at"));
                }
                continue;
            }
            if (string.Equals(type, "turn_context", StringComparison.OrdinalIgnoreCase))
                model = ProviderJson.String(ProviderJson.Property(payload, "model", "model_name")) ?? model;

            var eventType = ProviderJson.String(ProviderJson.Property(payload, "type"));
            if (replayActive && string.Equals(eventType, "task_started", StringComparison.OrdinalIgnoreCase))
            {
                var startedAt = ProviderJson.Number(ProviderJson.Property(payload, "started_at"));
                var threshold = replayCreatedAt?.ToUnixTimeSeconds()
                    ?? ProviderJson.Date(ProviderJson.Property(root, "timestamp", "created_at"))?.ToUnixTimeSeconds();
                if (startedAt is { } started && threshold is { } gate && started >= gate)
                    replayActive = false;
                continue;
            }
            if (!string.Equals(eventType, "token_count", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "token_count", StringComparison.OrdinalIgnoreCase)) continue;
            var timestamp = ProviderJson.Date(ProviderJson.Property(root, "timestamp", "created_at"));
            if (timestamp is null) continue;
            var info = ProviderJson.Object(ProviderJson.Property(payload, "info")) ?? payload;
            var last = ProviderJson.Object(ProviderJson.Property(info, "last_token_usage", "usage"));
            var totalsJson = ProviderJson.Object(ProviderJson.Property(info, "total_token_usage", "totals"));
            var cumulative = totalsJson is { } totalsElement ? ParseUsage(totalsElement) : null;
            // Codex frequently re-emits an unchanged cumulative snapshot with a new timestamp.
            // Counting that line is the exact failure mode that turns a normal month into a
            // four-digit estimate.
            if (cumulative is not null && previousTotals is not null && cumulative.EqualCounts(previousTotals))
                continue;

            if (replayActive)
            {
                if (cumulative is not null) previousTotals = cumulative;
                continue;
            }

            // Seed cumulative baselines with pre-window records. Without this, the first
            // total-only event inside the 30-day window looks like the entire lifetime total.
            if (timestamp < since)
            {
                if (cumulative is not null) previousTotals = cumulative;
                continue;
            }

            var usage = last is { } lastElement
                ? ParseUsage(lastElement)
                : cumulative?.Subtract(previousTotals);
            if (usage is null) continue;
            if (cumulative is not null) previousTotals = cumulative;
            usage = usage with { Cached = Math.Min(usage.Cached, usage.Input) };
            if (usage.Total <= 0) usage = usage with { Total = usage.Input + usage.Output + usage.Reasoning };
            if (usage.Total <= 0) continue;
            var input = usage.Input;
            var cached = usage.Cached;
            var output = usage.Output;
            var reasoning = usage.Reasoning;
            var tokens = usage.Total;
            var eventModel = ProviderJson.String(ProviderJson.Property(payload, "model", "model_name")) ?? model ?? "gpt-5";
            var key = $"{timestamp:O}|{eventModel}|{input}|{cached}|{output}|{reasoning}|{tokens}";
            if (!seen.Add(key)) continue;
            report.RowsRead++;
            TrackRecord(report, timestamp);
            // The spend ring is selected by the user's local calendar day. Codex emits UTC
            // timestamps, so grouping by UTC makes evening usage appear under tomorrow on
            // west-of-UTC machines and makes Today/Yesterday look empty.
            var day = DateOnly.FromDateTime(timestamp.Value.LocalDateTime);
            var pricing = _catalog.ResolvePrice(ProviderIds.Codex, eventModel);
            var cost = pricing?.Estimate(Math.Max(0, input - cached), cached, output + reasoning);
            var cacheSavings = pricing is null ? 0 : cached / 1_000_000d * Math.Max(0, pricing.InputPerMillion - pricing.CachedInputPerMillion);
            if (cost is null) unknownModels.Add(eventModel);
            var knownCost = cost ?? 0;
            if (totals.TryGetValue(day, out var prior)) totals[day] = (prior.Tokens + tokens, prior.Cost + knownCost);
            else totals[day] = (tokens, knownCost);
            var basis = cost is null ? UsageCostBasis.Unpriced : UsageCostBasis.CatalogEstimated;
            var breakdownKey = (day, eventModel, basis);
            if (breakdown.TryGetValue(breakdownKey, out var existing))
            {
                breakdown[breakdownKey] = existing with
                {
                    UncachedInputTokens = existing.UncachedInputTokens + Math.Max(0, input - cached),
                    CachedInputTokens = existing.CachedInputTokens + cached,
                    OutputTokens = existing.OutputTokens + output,
                    ReasoningTokens = existing.ReasoningTokens + reasoning,
                    CostUsd = existing.CostUsd + knownCost,
                    CacheSavingsUsd = existing.CacheSavingsUsd + cacheSavings
                };
            }
            else
            {
                breakdown[breakdownKey] = new UsageBreakdownPoint(day, ProviderIds.Codex, eventModel,
                    Math.Max(0, input - cached), cached, 0, output, reasoning, knownCost, basis,
                    basis == UsageCostBasis.CatalogEstimated ? PricingBasis.LocalEstimate : PricingBasis.Unknown, true, cacheSavings);
            }
        }
        return new SourceHistoryContribution
        {
            Fingerprint = fingerprint,
            Points = totals.OrderBy(p => p.Key).Select(p => new UsageHistoryPoint(p.Key, p.Value.Tokens, p.Value.Cost, true)).ToList(),
            Breakdown = breakdown.Values.OrderBy(point => point.Date).ThenBy(point => point.ModelId, StringComparer.OrdinalIgnoreCase).ToList(),
            UnknownModels = unknownModels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static void TrackRecord(HistoryScanReport report, DateTimeOffset? timestamp)
    {
        if (timestamp is not { } value) return;
        if (report.OldestRecord is null || value < report.OldestRecord) report.OldestRecord = value;
        if (report.NewestRecord is null || value > report.NewestRecord) report.NewestRecord = value;
    }

    private void ScanDiagnosticsDatabase(string codexHome, DateTimeOffset since,
        IDictionary<DateOnly, (double Tokens, double Cost)> totals,
        IDictionary<(DateOnly Date, string Model, UsageCostBasis Basis), UsageBreakdownPoint> breakdown,
        ISet<string> unknownModels, HistoryScanReport? report = null)
    {
        // Newer Codex builds emit authoritative per-turn usage to the local logs SQLite database,
        // while older sessions keep JSONL token_count records (and current builds can re-log both).
        // The day-level source preference below keeps the two sources from double-counting the
        // same turns; the JSONL reader stays supported for installs without a logs database.
        if (string.IsNullOrWhiteSpace(codexHome) || !Directory.Exists(codexHome)) return;
        string[] databases;
        try
        {
            databases = Directory.EnumerateFiles(codexHome, "logs_*.sqlite", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        // The window is applied at local-day granularity everywhere else in the pipeline, so the
        // SQL cutoff is local midnight on the boundary day instead of the `since` instant.
        var cutoff = IncrementalHistoryScan.UtcSecondsAtLocalMidnight(
            IncrementalHistoryScan.SinceDate(since, _localTimeZone), _localTimeZone);
        var rows = new List<(DateTimeOffset Timestamp, long Tokens, string Model)>();
        foreach (var database in databases)
        {
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = database,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT ts, feedback_log_body
                    FROM logs
                    WHERE ts >= $cutoff
                      AND target = 'codex_core::session::turn'
                      AND feedback_log_body LIKE '%post sampling token usage%';
                    """;
                command.Parameters.AddWithValue("$cutoff", cutoff);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                    if (!long.TryParse(reader.GetValue(0).ToString(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var timestamp)) continue;
                    // Sanity window shared with the Antigravity scanner (2001-09-09..2096-08-08).
                    // A corrupt or absurd row must not kill the whole scan with an
                    // ArgumentOutOfRangeException or fabricate far-future spend points.
                    if (timestamp < 1_000_000_000 || timestamp > 4_000_000_000) continue;
                    var match = DiagnosticsUsagePattern.Match(reader.GetString(1));
                    if (!match.Success || !double.TryParse(match.Groups["tokens"].Value,
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) || tokens <= 0) continue;
                    if (report is not null) report.RowsRead++;
                    var modelMatch = DiagnosticsModelPattern.Match(reader.GetString(1));
                    rows.Add((DateTimeOffset.FromUnixTimeSeconds(timestamp), (long)tokens,
                        modelMatch.Success ? modelMatch.Groups["model"].Value : "gpt-5"));
                }
            }
            catch (SqliteException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (rows.Count == 0) return;

        // The logs database is the authoritative per-turn usage source for the days it covers.
        // The same turns are also re-logged as JSONL token_count records, so counting both would
        // double the day. Drop the JSONL-derived contribution for every covered day before adding
        // these rows. The rule runs at merge time, so fresh parses and cached index contributions
        // apply it identically for a given database state.
        var coveredDays = rows.Select(row => IncrementalHistoryScan.DayOf(row.Timestamp, _localTimeZone)).ToHashSet();
        foreach (var day in coveredDays)
        {
            totals.Remove(day);
            foreach (var key in breakdown.Keys.Where(key => key.Date == day).ToArray())
                breakdown.Remove(key);
        }

        foreach (var (timestamp, tokens, model) in rows)
        {
            var pricing = _catalog.ResolvePrice(ProviderIds.Codex, model);
            var cost = pricing is null
                ? 0
                : pricing.Estimate(0, 0, tokens);
            if (pricing is null) unknownModels.Add(model);
            var day = IncrementalHistoryScan.DayOf(timestamp, _localTimeZone);
            totals.TryGetValue(day, out var prior);
            totals[day] = (prior.Tokens + tokens, prior.Cost + cost);
            var basis = pricing is null ? UsageCostBasis.Unpriced : UsageCostBasis.CoarseEstimate;
            var key = (day, model, basis);
            if (breakdown.TryGetValue(key, out var existing))
            {
                breakdown[key] = existing with { OutputTokens = existing.OutputTokens + tokens, CostUsd = existing.CostUsd + cost };
            }
            else
            {
                breakdown[key] = new UsageBreakdownPoint(day, ProviderIds.Codex, model,
                    0, 0, 0, tokens, 0, cost, basis,
                    pricing is null ? PricingBasis.Unknown : PricingBasis.LocalEstimate, true);
            }
        }
    }

    private IEnumerable<string> DiscoverSessionFiles(string codexHome)
    {
        // Match the native discovery order: active sessions win over archived copies with the
        // same relative path. A direct-root fallback keeps sanitized fixtures and older installs
        // working when neither canonical directory exists.
        var activeRoot = Path.Combine(codexHome, "sessions");
        var archivedRoot = Path.Combine(codexHome, "archived_sessions");
        var files = new List<string>();
        var relative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { activeRoot, archivedRoot })
        {
            foreach (var path in _files.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var key = NormalizeRelativePath(root, path);
                if (relative.Add(key)) files.Add(path);
            }
        }
        return files.Count > 0
            ? files
            : _files.EnumerateFiles(codexHome, "*.jsonl", SearchOption.AllDirectories);
    }

    private static string NormalizeRelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static RawUsage ParseUsage(JsonElement element)
        => new(
            Number(element, "input_tokens", "prompt_tokens", "input"),
            Number(element, "cached_input_tokens", "cache_read_input_tokens", "cached_tokens"),
            Number(element, "output_tokens", "completion_tokens", "output"),
            Number(element, "reasoning_output_tokens", "reasoning_tokens"),
            Number(element, "total_tokens"));

    private static bool IsChildSessionMeta(JsonElement payload)
    {
        static bool HasValue(JsonElement? value)
            => value is { } present && present.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null &&
               (present.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(present.GetString()));

        if (HasValue(ProviderJson.Property(payload, "forked_from_id", "parent_thread_id"))) return true;
        if (string.Equals(ProviderJson.String(ProviderJson.Property(payload, "thread_source")), "subagent", StringComparison.OrdinalIgnoreCase)) return true;
        var source = ProviderJson.Object(ProviderJson.Property(payload, "source"));
        return source is { } sourceValue && HasValue(ProviderJson.Property(sourceValue, "subagent"));
    }

    private static double Number(JsonElement element, params string[] names) => ProviderJson.Number(ProviderJson.Property(element, names)) ?? 0;

    private double? EstimateCost(string model, double input, double cached, double output)
    {
        var pricing = _catalog.ResolvePrice(ProviderIds.Codex, model);
        return pricing?.Estimate(input, cached, output) ?? 0;
    }

    private sealed record RawUsage(double Input, double Cached, double Output, double Reasoning, double Total)
    {
        public bool EqualCounts(RawUsage other)
            => Input == other.Input && Cached == other.Cached && Output == other.Output &&
               Reasoning == other.Reasoning && Total == other.Total;

        public RawUsage Subtract(RawUsage? previous)
            => new(Math.Max(0, Input - (previous?.Input ?? 0)),
                Math.Max(0, Cached - (previous?.Cached ?? 0)),
                Math.Max(0, Output - (previous?.Output ?? 0)),
                Math.Max(0, Reasoning - (previous?.Reasoning ?? 0)),
                Math.Max(0, Total - (previous?.Total ?? 0)));
    }
}
