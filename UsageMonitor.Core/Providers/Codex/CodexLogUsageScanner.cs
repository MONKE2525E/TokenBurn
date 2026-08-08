using System.Globalization;
using System.Text.Json;

namespace UsageMonitor.Core.Providers.Codex;

public sealed class CodexLogUsageScanner
{
    private readonly IProviderFileSystem _files;
    public CodexLogUsageScanner(IProviderFileSystem? files = null) => _files = files ?? new LocalProviderFileSystem();

    public static ProviderUsageHistory ScanDirectory(string codexHome, DateTimeOffset since) => new CodexLogUsageScanner().Scan(codexHome, since);

    public ProviderUsageHistory Scan(string codexHome, DateTimeOffset since)
    {
        var totals = new Dictionary<DateOnly, (double Tokens, double Cost)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in DiscoverSessionFiles(codexHome))
        {
            string? model = null;
            RawUsage? previousTotals = null;
            var sawSessionMeta = false;
            var replayActive = false;
            DateTimeOffset? replayCreatedAt = null;
            // Session logs are append-only JSONL and can grow very large. Read one record at a
            // time so local spend refreshes remain bounded by the current record, not file size.
            foreach (var raw in _files.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!raw.Contains("token_count", StringComparison.OrdinalIgnoreCase) &&
                    !raw.Contains("session_meta", StringComparison.OrdinalIgnoreCase) &&
                    !raw.Contains("task_started", StringComparison.OrdinalIgnoreCase)) continue;
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
                var day = DateOnly.FromDateTime(timestamp.Value.UtcDateTime);
                var cost = EstimateCost(eventModel, Math.Max(0, input - cached), cached, output + reasoning);
                if (totals.TryGetValue(day, out var prior)) totals[day] = (prior.Tokens + tokens, prior.Cost + cost);
                else totals[day] = (tokens, cost);
            }
        }
        return new ProviderUsageHistory(totals.OrderBy(p => p.Key).Select(p => new UsageHistoryPoint(p.Key, p.Value.Tokens, p.Value.Cost, true)).ToArray());
    }

    private IEnumerable<string> DiscoverSessionFiles(string codexHome)
    {
        // Match OpenUsage's native discovery: active sessions win over archived copies with the
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

    private static double EstimateCost(string model, double input, double cached, double output)
    {
        var pricing = ModelPricingCatalog.Resolve(model);
        return pricing.Estimate(input, cached, output);
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
