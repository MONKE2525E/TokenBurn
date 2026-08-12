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
    private readonly TimeZoneInfo _localTimeZone;
    public CodexLogUsageScanner(IProviderFileSystem? files = null, IModelCatalog? catalog = null,
        TimeZoneInfo? localTimeZone = null)
    {
        _files = files ?? new LocalProviderFileSystem();
        _catalog = catalog ?? new CachedModelCatalog();
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
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
        var files = DiscoverSessionFiles(codexHome).ToArray();
        return IncrementalHistoryScan.Run("codex", files, historyStoreDirectory, since, ParseSessionFile,
            afterMerge: (merged, scanReport) =>
            {
                var totals = merged.Points.ToDictionary(p => p.Date, p => (Tokens: p.Tokens, Cost: p.CostUsd));
                var breakdown = merged.Breakdown.ToDictionary(p => (p.Date, Model: p.ModelId ?? string.Empty, p.CostBasis), p => p);
                var unknownModels = new HashSet<string>(merged.UnknownModels, StringComparer.OrdinalIgnoreCase);
                ScanDiagnosticsDatabase(codexHome, since, totals, breakdown, unknownModels, scanReport);
                return new ProviderUsageHistory(totals.OrderBy(p => p.Key)
                    .Select(p => new UsageHistoryPoint(p.Key, p.Value.Tokens, p.Value.Cost, true)).ToArray())
                {
                    UnknownModels = unknownModels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Breakdown = breakdown.Values
                        .OrderBy(point => point.Date)
                        .ThenBy(point => point.ModelId, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
            },
            report, _localTimeZone);
    }

    private SourceHistoryContribution ParseSessionFile(string path, DateTimeOffset since, DateOnly sinceDate, HistoryScanReport report)
    {
        // Capture the fingerprint before reading. If the file is appended to mid-parse the stored
        // fingerprint will not match the file's final state, so the next scan re-parses it instead
        // of permanently missing the appended records.
        var fingerprint = HistorySourceFingerprint.Of(path);
        var aggregator = new HistoryAggregator(ProviderIds.Codex);
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

            // Seed cumulative baselines with records whose local day precedes the window. Without
            // this, the first total-only event inside the window looks like the entire lifetime
            // total. The window is day-granular: records on the boundary day itself are part of the
            // window even before the `since` instant, matching what an earlier parse cached.
            if (IncrementalHistoryScan.DayOf(timestamp.Value, _localTimeZone) < sinceDate)
            {
                if (cumulative is not null) previousTotals = cumulative;
                continue;
            }

            var usage = last is { } lastElement
                ? ParseUsage(lastElement)
                : cumulative?.Subtract(previousTotals);
            if (usage is null) continue;
            if (cumulative is not null) previousTotals = cumulative;
            // Corrupt or hostile records can carry NaN/Infinity (string-typed "1e999") or negative
            // components. Drop the record when anything is non-finite before clamping negatives to
            // zero, so one bad line can neither poison the day totals nor the cumulative baseline.
            if (!double.IsFinite(usage.Input) || !double.IsFinite(usage.Cached) ||
                !double.IsFinite(usage.Output) || !double.IsFinite(usage.Reasoning) ||
                !double.IsFinite(usage.Total)) continue;
            usage = usage with
            {
                Input = Math.Max(0, usage.Input),
                Cached = Math.Max(0, usage.Cached),
                Output = Math.Max(0, usage.Output),
                Reasoning = Math.Max(0, usage.Reasoning),
                Total = Math.Max(0, usage.Total)
            };
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
            report.Track(timestamp);
            // The spend ring is selected by the user's local calendar day. Codex emits UTC
            // timestamps, so grouping by UTC makes evening usage appear under tomorrow on
            // west-of-UTC machines and makes Today/Yesterday look empty.
            var day = IncrementalHistoryScan.DayOf(timestamp.Value, _localTimeZone);
            var pricing = _catalog.ResolvePrice(ProviderIds.Codex, eventModel);
            var cost = pricing?.Estimate(Math.Max(0, input - cached), cached, output + reasoning);
            var cacheSavings = pricing is null ? 0 : cached / 1_000_000d * Math.Max(0, pricing.InputPerMillion - pricing.CachedInputPerMillion);
            if (cost is null) aggregator.AddUnknownModel(eventModel);
            var knownCost = cost ?? 0;
            var basis = cost is null ? UsageCostBasis.Unpriced : UsageCostBasis.CatalogEstimated;
            aggregator.Add(day, eventModel, basis,
                basis == UsageCostBasis.CatalogEstimated ? PricingBasis.LocalEstimate : PricingBasis.Unknown,
                tokens: tokens,
                uncachedInput: Math.Max(0, input - cached),
                cachedInput: cached,
                cacheCreation: 0,
                output: output,
                reasoning: reasoning,
                cost: knownCost,
                cacheSavings: cacheSavings,
                estimated: true);
        }
        return aggregator.ToContribution(fingerprint);
    }

    private void ScanDiagnosticsDatabase(string codexHome, DateTimeOffset since,
        IDictionary<DateOnly, (double Tokens, double Cost)> totals,
        IDictionary<(DateOnly Date, string Model, UsageCostBasis Basis), UsageBreakdownPoint> breakdown,
        ISet<string> unknownModels, HistoryScanReport? report = null)
    {
        // Newer Codex builds stopped writing event_msg/token_count records to session JSONL.
        // Their authoritative per-turn usage is now emitted to the local logs SQLite database.
        // Keep this fallback local and read-only so older JSONL history remains supported.
        if (string.IsNullOrWhiteSpace(codexHome) || !Directory.Exists(codexHome)) return;
        string[] databases;
        try
        {
            databases = Directory.EnumerateFiles(codexHome, "logs_*.sqlite", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var cutoff = since.ToUniversalTime().ToUnixTimeSeconds();
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
                    var match = DiagnosticsUsagePattern.Match(reader.GetString(1));
                    if (!match.Success || !double.TryParse(match.Groups["tokens"].Value,
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) || tokens <= 0 ||
                        // The row is narrowed to long below; absurd counts from corrupt rows would
                        // wrap the cast and poison the day total instead of being skipped.
                        tokens > long.MaxValue) continue;
                    if (report is not null) report.RowsRead++;
                    var modelMatch = DiagnosticsModelPattern.Match(reader.GetString(1));
                    if (report is not null) report.Track(DateTimeOffset.FromUnixTimeSeconds(timestamp));
                    rows.Add((DateTimeOffset.FromUnixTimeSeconds(timestamp), (long)tokens,
                        modelMatch.Success ? modelMatch.Groups["model"].Value : "gpt-5"));
                }
            }
            catch (SqliteException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
            ProviderJson.NumberOrZero(element, "input_tokens", "prompt_tokens", "input"),
            ProviderJson.NumberOrZero(element, "cached_input_tokens", "cache_read_input_tokens", "cached_tokens"),
            ProviderJson.NumberOrZero(element, "output_tokens", "completion_tokens", "output"),
            ProviderJson.NumberOrZero(element, "reasoning_output_tokens", "reasoning_tokens"),
            ProviderJson.NumberOrZero(element, "total_tokens"));

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
