using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace UsageMonitor.Core.Providers.OpenCode;

/// <summary>
/// Windows-native OpenCode history reader. OpenCode persists authoritative per-message costs in
/// its own SQLite database, so this provider is local-only and never needs an API key from us.
/// </summary>
public sealed class OpenCodeProvider : IUsageProvider
{
    private static readonly ProviderDescriptor Provider = new(
        ProviderIds.OpenCode,
        "OpenCode",
        "opencode",
        [new ProviderLink("Dashboard", "https://opencode.ai/auth")]);

    private readonly OpenCodeUsageScanner _scanner;

    public OpenCodeProvider(OpenCodeUsageScanner? scanner = null) => _scanner = scanner ?? new OpenCodeUsageScanner();

    public ProviderDescriptor Descriptor => Provider;

    public Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var scan = _scanner.Scan(context.Now, cancellationToken);
            if (!scan.HasDatabase && !scan.HasGoCredential)
            {
                return Task.FromResult(ProviderSnapshot.Error(Provider,
                    "OpenCode was not found. Use OpenCode locally or sign in to OpenCode Go first.",
                    ProviderErrorCategory.NotConfigured));
            }

            var history = BuildHistory(scan.Rows, context.Now);
            var lines = new List<MetricLine>();
            var goRows = scan.Rows.Where(row => row.ProviderId.Equals("opencode-go", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (scan.HasGoCredential || goRows.Length > 0)
                AddGoMeters(lines, goRows, scan.FirstGoUsageAt, context.Now);

            if (history.Points.Count > 0)
            {
                lines.Add(MetricLine.ValuesLine("Last 30 days",
                [
                    new MetricValue(history.TotalCostUsd, MetricKind.Dollars, "local history"),
                    new MetricValue(history.TotalTokens, MetricKind.Count, "tokens")
                ]));
                lines.Add(MetricLine.Chart("Usage Trend", history.Points.Select(point =>
                    new MetricChartPoint(point.CostUsd, point.Date.ToString("MMM d", CultureInfo.InvariantCulture)))));
            }
            else
            {
                lines.Add(MetricLine.Badge("Status", "No hosted OpenCode usage in local logs.", "#A3A3A3", state: MetricState.Unknown));
            }

            return Task.FromResult(ProviderSnapshot.Success(Provider, lines,
                scan.HasGoCredential || goRows.Length > 0 ? "Go" : null, context.Now, history));
        }
        catch (OpenCodeDatabaseException)
        {
            return Task.FromResult(ProviderSnapshot.Error(Provider,
                "OpenCode's local database could not be read. Quit OpenCode and refresh.",
                ProviderErrorCategory.Parse));
        }
        catch (OpenCodeAuthException)
        {
            return Task.FromResult(ProviderSnapshot.Error(Provider,
                "OpenCode's auth.json could not be read. Sign in to OpenCode Go again.",
                ProviderErrorCategory.Parse));
        }
    }

    private static ProviderUsageHistory BuildHistory(IReadOnlyList<OpenCodeUsageRow> rows, DateTimeOffset now)
    {
        var since = now.UtcDateTime.Date.AddDays(-29);
        var totals = rows
            .Where(row => row.Timestamp.UtcDateTime.Date >= since)
            .GroupBy(row => DateOnly.FromDateTime(row.Timestamp.UtcDateTime))
            .OrderBy(group => group.Key)
            .Select(group => new UsageHistoryPoint(group.Key,
                group.Sum(row => row.Tokens), group.Sum(row => row.CostUsd), false))
            .ToArray();
        return new ProviderUsageHistory(totals);
    }

    private static void AddGoMeters(ICollection<MetricLine> lines, IReadOnlyList<OpenCodeUsageRow> goRows,
        DateTimeOffset? firstGoUsageAt, DateTimeOffset now)
    {
        const double sessionCap = 12;
        const double weeklyCap = 30;
        const double monthlyCap = 60;
        var nowUtc = now.ToUniversalTime();
        var sessionStart = nowUtc.AddHours(-5);
        var currentSession = goRows.Where(row => row.Timestamp >= sessionStart && row.Timestamp < nowUtc).ToArray();
        var sessionResetsAt = (currentSession.Length == 0 ? nowUtc : currentSession.Min(row => row.Timestamp)).AddHours(5);

        var weekStart = StartOfUtcWeek(nowUtc);
        var weekEnd = weekStart.AddDays(7);
        var monthStart = AnchoredMonthStart(nowUtc, firstGoUsageAt);
        var monthEnd = AnchoredMonthStart(monthStart.AddMonths(1), firstGoUsageAt);

        lines.Add(MetricLine.Progress("Session", currentSession.Sum(row => row.CostUsd), sessionCap, MetricKind.Dollars,
            sessionResetsAt, TimeSpan.FromHours(5)));
        lines.Add(MetricLine.Progress("Weekly", SumBetween(goRows, weekStart, weekEnd), weeklyCap, MetricKind.Dollars,
            weekEnd, TimeSpan.FromDays(7)));
        lines.Add(MetricLine.Progress("Monthly", SumBetween(goRows, monthStart, monthEnd), monthlyCap, MetricKind.Dollars,
            monthEnd, monthEnd - monthStart));
    }

    private static double SumBetween(IEnumerable<OpenCodeUsageRow> rows, DateTimeOffset start, DateTimeOffset end) =>
        Math.Round(rows.Where(row => row.Timestamp >= start && row.Timestamp < end).Sum(row => row.CostUsd), 4);

    private static DateTimeOffset StartOfUtcWeek(DateTimeOffset value)
    {
        var day = value.UtcDateTime.Date;
        var daysSinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return new DateTimeOffset(day.AddDays(-daysSinceMonday), TimeSpan.Zero);
    }

    private static DateTimeOffset AnchoredMonthStart(DateTimeOffset value, DateTimeOffset? anchor)
    {
        var utc = value.ToUniversalTime();
        var anchorUtc = anchor?.ToUniversalTime();
        var day = Math.Min(anchorUtc?.Day ?? 1, DateTime.DaysInMonth(utc.Year, utc.Month));
        var start = new DateTimeOffset(utc.Year, utc.Month, day,
            anchorUtc?.Hour ?? 0, anchorUtc?.Minute ?? 0, anchorUtc?.Second ?? 0, TimeSpan.Zero);
        return start > utc ? AnchoredMonthStart(utc.AddMonths(-1), anchor) : start;
    }
}

public sealed record OpenCodeUsageRow(DateTimeOffset Timestamp, double CostUsd, double Tokens, string ProviderId);

public sealed record OpenCodeUsageScan(bool HasDatabase, bool HasGoCredential,
    DateTimeOffset? FirstGoUsageAt, IReadOnlyList<OpenCodeUsageRow> Rows);

public sealed class OpenCodeUsageScanner
{
    private const string GoProviderId = "opencode-go";
    private readonly Func<string?> _dataDirectoryOverride;
    private readonly Func<string?> _xdgDataHome;
    private readonly Func<string> _userProfile;

    public OpenCodeUsageScanner(Func<string?>? dataDirectoryOverride = null, Func<string?>? xdgDataHome = null,
        Func<string>? userProfile = null)
    {
        _dataDirectoryOverride = dataDirectoryOverride ?? (() => Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR"));
        _xdgDataHome = xdgDataHome ?? (() => Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public OpenCodeUsageScan Scan(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var directory = ResolveDataDirectory();
        var auth = ReadGoCredential(Path.Combine(directory, "auth.json"));
        var databases = DiscoverDatabases(directory);
        if (databases.Count == 0) return new OpenCodeUsageScan(false, auth, null, Array.Empty<OpenCodeUsageRow>());

        var cutoff = now.ToUniversalTime().AddDays(-30).ToUnixTimeMilliseconds();
        var rows = new List<OpenCodeUsageRow>();
        DateTimeOffset? firstGoUsageAt = null;
        var successfulReads = 0;
        foreach (var path in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    // The scanner opens an OpenCode-owned database for a single refresh only.
                    // Returning that handle to a pool can keep the .db locked on Windows after
                    // the refresh has completed, disrupting OpenCode and cleanup on test hosts.
                    Pooling = false
                }.ToString());
                connection.Open();
                rows.AddRange(ReadRows(connection, cutoff));
                var candidate = ReadFirstGoUsage(connection);
                if (candidate is not null && (firstGoUsageAt is null || candidate < firstGoUsageAt)) firstGoUsageAt = candidate;
                successfulReads++;
            }
            catch (SqliteException)
            {
                // A second release-channel database may be locked or stale. Only fail when every
                // local database is unreadable, otherwise use the healthy history we did obtain.
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (successfulReads == 0) throw new OpenCodeDatabaseException();
        return new OpenCodeUsageScan(true, auth, firstGoUsageAt, rows);
    }

    private string ResolveDataDirectory()
    {
        var explicitPath = Trim(_dataDirectoryOverride());
        if (explicitPath is not null) return Environment.ExpandEnvironmentVariables(explicitPath);
        var xdg = Trim(_xdgDataHome());
        if (xdg is not null) return Path.Combine(Environment.ExpandEnvironmentVariables(xdg), "opencode");
        return Path.Combine(_userProfile(), ".local", "share", "opencode");
    }

    private static IReadOnlyList<string> DiscoverDatabases(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(directory, "opencode*.db", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IOException) { throw new OpenCodeDatabaseException(); }
        catch (UnauthorizedAccessException) { throw new OpenCodeDatabaseException(); }
    }

    private static bool ReadGoCredential(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(GoProviderId, out var entry) &&
                   entry.ValueKind == JsonValueKind.Object &&
                   entry.TryGetProperty("key", out var key) &&
                   key.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(key.GetString());
        }
        catch (IOException) { throw new OpenCodeAuthException(); }
        catch (UnauthorizedAccessException) { throw new OpenCodeAuthException(); }
        catch (JsonException) { throw new OpenCodeAuthException(); }
    }

    private static IEnumerable<OpenCodeUsageRow> ReadRows(SqliteConnection connection, long cutoff)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time_created, json_extract(data, '$.cost'),
                   COALESCE(json_extract(data, '$.tokens.total'), 0), json_extract(data, '$.providerID')
            FROM message
            WHERE time_created >= $cutoff
              AND json_valid(data)
              AND json_extract(data, '$.role') = 'assistant'
              AND json_extract(data, '$.providerID') IN ('opencode-go', 'opencode')
              AND json_type(data, '$.cost') IN ('integer', 'real');
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!TryNumber(reader.GetValue(0), out var timestamp) || !TryNumber(reader.GetValue(1), out var cost) ||
                !TryNumber(reader.GetValue(2), out var tokens) || reader.IsDBNull(3)) continue;
            if (!double.IsFinite(timestamp) || !double.IsFinite(cost) || cost < 0 || !double.IsFinite(tokens) || tokens < 0) continue;
            var providerId = reader.GetString(3);
            if (string.IsNullOrWhiteSpace(providerId)) continue;
            yield return new OpenCodeUsageRow(DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp), cost, tokens, providerId);
        }
    }

    private static DateTimeOffset? ReadFirstGoUsage(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(time_created)
            FROM message
            WHERE json_valid(data)
              AND json_extract(data, '$.role') = 'assistant'
              AND json_extract(data, '$.providerID') = 'opencode-go'
              AND json_type(data, '$.cost') IN ('integer', 'real');
            """;
        var value = command.ExecuteScalar();
        return TryNumber(value, out var timestamp) && double.IsFinite(timestamp)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp)
            : null;
    }

    private static bool TryNumber(object? value, out double number)
    {
        try
        {
            number = value switch
            {
                null or DBNull => 0,
                double doubleValue => doubleValue,
                float floatValue => floatValue,
                decimal decimalValue => (double)decimalValue,
                long longValue => longValue,
                int intValue => intValue,
                string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => Convert.ToDouble(value, CultureInfo.InvariantCulture)
            };
            return value is not null and not DBNull;
        }
        catch (FormatException) { number = 0; return false; }
        catch (InvalidCastException) { number = 0; return false; }
        catch (OverflowException) { number = 0; return false; }
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

public sealed class OpenCodeDatabaseException : Exception;
public sealed class OpenCodeAuthException : Exception;
