using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageMonitor.Core.Providers.Antigravity;

/// <summary>
/// Reads Antigravity CLI response-token counts from its local conversation databases.
/// Antigravity exposes subscription quota, not billable prices. The cost values here are therefore
/// an API-equivalent estimate based on the per-generation model and response tokens, not a Google
/// AI Pro invoice. Antigravity does not persist input or cache-token counts in this schema.
/// </summary>
public sealed class AntigravityCliUsageScanner
{
    private static readonly Regex ModelPattern = new(@"(?i)(?:gemini|claude|gpt)-[a-z0-9._:-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Func<string?> _dataDirectoryOverride;
    private readonly Func<string> _userProfile;
    private readonly TimeZoneInfo _localTimeZone;

    public AntigravityCliUsageScanner(Func<string?>? dataDirectoryOverride = null, Func<string>? userProfile = null,
        TimeZoneInfo? localTimeZone = null)
    {
        _dataDirectoryOverride = dataDirectoryOverride ?? (() => Environment.GetEnvironmentVariable("AGY_DATA_DIR"));
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
    }

    public ProviderUsageHistory Scan(DateTimeOffset now, IModelCatalog? catalog = null,
        CancellationToken cancellationToken = default)
    {
        var databases = DiscoverDatabases();
        if (databases.Count == 0) return new ProviderUsageHistory(Array.Empty<UsageHistoryPoint>());

        // The 30-day window shown by the ring and breakdown table is day-granular, so the cutoff
        // is the first instant of the local day 29 days back instead of the `now - 29d` instant.
        // An instant cutoff truncates the boundary day and makes Antigravity disagree with every
        // other provider.
        var cutoffDay = IncrementalHistoryScan.SinceDate(now, _localTimeZone).AddDays(-29);
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(
            IncrementalHistoryScan.UtcSecondsAtLocalMidnight(cutoffDay, _localTimeZone));
        var rows = new List<AntigravityCliUsageRow>();
        foreach (var database in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                var generationModels = ReadGenerationModels(connection);
                using var command = connection.CreateCommand();
                // NULL-payload steps advance the generation pairing too: a step without a payload
                // still has a gen_metadata row, so skipping it here would shift every later model
                // attribution by one.
                command.CommandText = "SELECT step_payload FROM steps ORDER BY idx";
                using var reader = command.ExecuteReader();
                var generationIndex = 0;
                while (reader.Read())
                {
                    // Both tables are ordered by idx and generations are recorded regardless of
                    // age, so the pairing must advance for every step row in raw order. Advancing
                    // only for rows inside the scan window shifted every later model attribution
                    // whenever a pre-window step was skipped.
                    var modelId = generationIndex < generationModels.Count ? generationModels[generationIndex] : null;
                    generationIndex++;
                    if (reader.IsDBNull(0)) continue;
                    var payload = reader.GetFieldValue<byte[]>(0);
                    if (!TryReadUsage(payload, out var timestamp, out var tokens) || timestamp < cutoff || tokens <= 0) continue;
                    rows.Add(new AntigravityCliUsageRow(timestamp, tokens, modelId));
                }
            }
            catch (SqliteException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var priced = rows.Select(row => Price(row, catalog)).ToArray();
        // Bucket by the local calendar day so the dashboard's local "Today" selector
        // matches. UTC bucketing pushes evening usage into tomorrow's date.
        var points = priced
            .GroupBy(row => IncrementalHistoryScan.DayOf(row.Row.Timestamp, _localTimeZone))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var tokens = group.Sum(row => row.Row.Tokens);
                return new UsageHistoryPoint(group.Key, tokens, group.Sum(row => row.CostUsd), true);
            })
            .ToArray();
        var breakdown = priced
            .GroupBy(row => new { Date = IncrementalHistoryScan.DayOf(row.Row.Timestamp, _localTimeZone), row.Row.ModelId, row.CostBasis })
            .OrderBy(group => group.Key.Date)
            .ThenBy(group => group.Key.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UsageBreakdownPoint(group.Key.Date, ProviderIds.Antigravity, group.Key.ModelId,
                0, 0, 0, group.Sum(row => row.Row.Tokens), 0, group.Sum(row => row.CostUsd),
                group.Key.CostBasis, group.First().PricingBasis, true))
            .ToArray();
        var unknownModels = priced.Where(row => row.CostBasis == UsageCostBasis.Unpriced && row.Row.ModelId is not null)
            .Select(row => row.Row.ModelId!).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ProviderUsageHistory(points) { Breakdown = breakdown, UnknownModels = unknownModels };
    }

    private static IReadOnlyList<string?> ReadGenerationModels(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM gen_metadata ORDER BY idx";
        using var reader = command.ExecuteReader();
        var models = new List<string?>();
        while (reader.Read())
        {
            var data = reader.IsDBNull(0) ? Array.Empty<byte>() : reader.GetFieldValue<byte[]>(0);
            models.Add(ModelPattern.Match(Encoding.UTF8.GetString(data)).Value is { Length: > 0 } model ? model : null);
        }
        return models;
    }

    private static AntigravityPricedUsage Price(AntigravityCliUsageRow row, IModelCatalog? catalog)
    {
        var pricing = row.ModelId is null ? null : catalog?.ResolvePrice(ProviderIds.Antigravity, row.ModelId) ??
            ModelPricingCatalog.TryResolve(ProviderIds.Antigravity, row.ModelId);
        return pricing is null
            ? new AntigravityPricedUsage(row, 0, UsageCostBasis.Unpriced, PricingBasis.Unknown)
            : new AntigravityPricedUsage(row, pricing.Estimate(0, 0, row.Tokens),
                UsageCostBasis.CatalogEstimated, PricingBasis.PublicCatalog);
    }

    public static bool TryReadUsage(byte[] payload, out DateTimeOffset timestamp, out long tokens)
    {
        timestamp = default;
        tokens = 0;
        if (!TryGetNestedVarint(payload, [5, 1, 1], out var timestampSeconds) ||
            !TryGetNestedVarint(payload, [5, 9, 9], out var tokenCount) ||
            timestampSeconds < 1_000_000_000UL || timestampSeconds > 4_000_000_000UL || tokenCount == 0 ||
            tokenCount > (ulong)long.MaxValue)
            return false;

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds((long)timestampSeconds);
            tokens = (long)tokenCount;
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private IReadOnlyList<string> DiscoverDatabases()
    {
        var configured = _dataDirectoryOverride()?.Trim();
        var cliRoot = Path.Combine(_userProfile(), ".gemini");
        // The agy CLI's server reads conversations from `.gemini/antigravity/conversations` on
        // current Windows builds (observed in the CLI's own log), while older layouts and the
        // `AGY_DATA_DIR` override use `.gemini/antigravity-cli/conversations`. Use the first
        // candidate that actually contains databases: conversation rows carry no message identity,
        // so scanning two layouts that both contain data could double-count the same sessions.
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(Path.Combine(Environment.ExpandEnvironmentVariables(configured), "conversations"));
        candidates.Add(Path.Combine(cliRoot, "antigravity", "conversations"));
        candidates.Add(Path.Combine(cliRoot, "antigravity-cli", "conversations"));

        foreach (var directory in candidates)
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                var databases = Directory.EnumerateFiles(directory, "*.db", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (databases.Length > 0) return databases;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Array.Empty<string>();
    }

    private static bool TryGetNestedVarint(byte[] data, int[] path, out ulong value, int index = 0)
    {
        value = 0;
        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out var key) || key == 0) return false;
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (wire == 0)
            {
                if (field == path[index] && index == path.Length - 1)
                {
                    if (!TryReadVarint(data, ref offset, out value)) return false;
                    return true;
                }
                if (!TryReadVarint(data, ref offset, out _)) return false;
                continue;
            }

            if (wire == 2)
            {
                if (!TryReadVarint(data, ref offset, out var length) || length > int.MaxValue || offset + (long)length > data.Length)
                    return false;
                if (field == path[index] && index < path.Length - 1)
                {
                    var nested = data.AsSpan(offset, (int)length).ToArray();
                    if (TryGetNestedVarint(nested, path, out value, index + 1)) return true;
                }
                offset += (int)length;
                continue;
            }

            var bytes = wire == 1 ? 8 : wire == 5 ? 4 : -1;
            if (bytes < 0 || offset + bytes > data.Length) return false;
            offset += bytes;
        }
        return false;
    }

    private static bool TryReadVarint(byte[] data, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (offset < data.Length && shift <= 63)
        {
            var current = data[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}

public sealed record AntigravityCliUsageRow(DateTimeOffset Timestamp, long Tokens, string? ModelId = null);

internal sealed record AntigravityPricedUsage(AntigravityCliUsageRow Row, double CostUsd,
    UsageCostBasis CostBasis, PricingBasis PricingBasis);
