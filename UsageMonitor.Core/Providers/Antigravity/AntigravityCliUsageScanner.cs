using Microsoft.Data.Sqlite;

namespace UsageMonitor.Core.Providers.Antigravity;

/// <summary>
/// Reads Antigravity CLI response-token counts from its local conversation databases.
/// Antigravity exposes subscription quota, not billable prices. The cost values here are therefore
/// an API-equivalent estimate based on response tokens, not a Google AI Pro invoice.
/// </summary>
public sealed class AntigravityCliUsageScanner
{
    // Google lists Gemini 3.5 Flash Standard output at $9 per million tokens. Antigravity's local
    // records expose response-token counts but not the actual subscription accounting price.
    private const double EstimatedOutputCostPerMillionTokens = 9d;
    private readonly Func<string?> _dataDirectoryOverride;
    private readonly Func<string> _userProfile;

    public AntigravityCliUsageScanner(Func<string?>? dataDirectoryOverride = null, Func<string>? userProfile = null)
    {
        _dataDirectoryOverride = dataDirectoryOverride ?? (() => Environment.GetEnvironmentVariable("AGY_DATA_DIR"));
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public ProviderUsageHistory Scan(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var databases = DiscoverDatabases();
        if (databases.Count == 0) return new ProviderUsageHistory(Array.Empty<UsageHistoryPoint>());

        var cutoff = now.ToUniversalTime().AddDays(-29);
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
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT step_payload FROM steps WHERE step_payload IS NOT NULL";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    var payload = reader.GetFieldValue<byte[]>(0);
                    if (!TryReadUsage(payload, out var timestamp, out var tokens) || timestamp < cutoff || tokens <= 0) continue;
                    rows.Add(new AntigravityCliUsageRow(timestamp, tokens));
                }
            }
            catch (SqliteException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var points = rows
            .GroupBy(row => DateOnly.FromDateTime(row.Timestamp.UtcDateTime))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var tokens = group.Sum(row => row.Tokens);
                return new UsageHistoryPoint(group.Key, tokens,
                    tokens / 1_000_000d * EstimatedOutputCostPerMillionTokens, true);
            })
            .ToArray();
        return new ProviderUsageHistory(points);
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
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_userProfile(), ".gemini", "antigravity-cli", "conversations")
            : Path.Combine(Environment.ExpandEnvironmentVariables(configured), "conversations");
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(directory, "*.db", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
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

public sealed record AntigravityCliUsageRow(DateTimeOffset Timestamp, long Tokens);
