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
    private readonly IModelCatalog? _catalog;

    public OpenCodeProvider(OpenCodeUsageScanner? scanner = null, IModelCatalog? catalog = null)
    {
        _scanner = scanner ?? new OpenCodeUsageScanner();
        _catalog = catalog;
    }

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
                    ProviderErrorCategory.NotInstalled));
            }

            var history = BuildHistory(scan.Rows, context.Now, context.ModelCatalog ?? _catalog);
            var lines = new List<MetricLine>();
            var goRows = scan.Rows.Where(row => row.ProviderId.Equals("opencode-go", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (scan.HasGoCredential || goRows.Length > 0)
                AddGoMeters(lines, goRows, scan.FirstGoUsageAt, context.Now, context.ModelCatalog ?? _catalog);

            if (history.Points.Count > 0)
            {
                lines.Add(MetricLine.ValuesLine("Last 90 days",
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

            context.Logger?.Info("OpenCode history scanned",
                new Dictionary<string, object?>
                {
                    ["databasePaths"] = scan.DatabasePaths.Count,
                    ["rows"] = scan.Rows.Count,
                    ["hasGoCredential"] = scan.HasGoCredential,
                    ["historyPoints"] = history.Points.Count,
                    ["historyCostUsd"] = history.TotalCostUsd,
                    ["unknownModels"] = history.UnknownModels.Count
                });

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

    private ProviderUsageHistory BuildHistory(IReadOnlyList<OpenCodeUsageRow> rows, DateTimeOffset now,
        IModelCatalog? catalog)
    {
        var today = _scanner.LocalDate(now);
        var since = today.AddDays(-(_scanner.HistoryDays - 1));
        var included = rows
            .Select(row => (Usage: Price(row, catalog), Date: _scanner.LocalDate(row.Timestamp)))
            .Where(item => item.Date >= since && item.Date <= today)
            .ToArray();

        var totals = included
            .GroupBy(item => item.Date)
            .OrderBy(group => group.Key)
            .Select(group => new UsageHistoryPoint(group.Key,
                group.Sum(item => item.Usage.Row.Tokens), group.Sum(item => item.Usage.CostUsd),
                group.Any(item => item.Usage.Estimated)))
            .ToArray();

        var breakdown = included
            .GroupBy(item => new
            {
                item.Date,
                ProviderId = DisplayProvider(item.Usage.Row.ProviderId),
                item.Usage.Row.ModelId,
                item.Usage.CostBasis
            })
            .OrderBy(group => group.Key.Date)
            .ThenBy(group => group.Key.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UsageBreakdownPoint(
                group.Key.Date,
                group.Key.ProviderId,
                group.Key.ModelId,
                group.Sum(item => item.Usage.Row.InputTokens),
                group.Sum(item => item.Usage.Row.CacheReadTokens),
                group.Sum(item => item.Usage.Row.CacheWriteTokens),
                group.Sum(item => item.Usage.Row.OutputTokens),
                group.Sum(item => item.Usage.Row.ReasoningTokens),
                group.Sum(item => item.Usage.CostUsd),
                group.Key.CostBasis,
                group.First().Usage.PricingBasis,
                group.Any(item => item.Usage.Estimated),
                group.Sum(item => item.Usage.CacheSavingsUsd)))
            .ToArray();

        var unknownModels = included
            .Where(item => item.Usage.CostBasis == UsageCostBasis.Unpriced)
            .Select(item => FormatModel(item.Usage.Row.ProviderId, item.Usage.Row.ModelId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProviderUsageHistory(totals)
        {
            UnknownModels = unknownModels,
            Breakdown = breakdown
        };
    }

    private static string DisplayProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return "unknown";
        // The opencode-go hosted gateway and the open-source opencode runtime are the same
        // product. Keep one canonical provider identity in the breakdown so the dashboard does
        // not render two "OpenCode" series.
        return providerId.Equals("opencode-go", StringComparison.OrdinalIgnoreCase)
            ? ProviderIds.OpenCode
            : providerId;
    }

    private static string FormatModel(string providerId, string? modelId)
    {
        var provider = DisplayProvider(providerId);
        return string.IsNullOrWhiteSpace(modelId) ? provider : $"{provider}/{modelId}";
    }

    private static OpenCodePricedUsage Price(OpenCodeUsageRow row, IModelCatalog? catalog)
    {
        // OpenCode persists its own per-message cost, but for some models (notably
        // opencode-go/deepseek-v4-flash) that local estimate is exactly half the current market
        // rate, under-reporting spend by 2x against the OpenCode billing dashboard. Prefer the
        // model catalog so costs match the bill; keep the persisted cost only when the model has
        // no known price.
        var pricing = row.ModelId is null
            ? null
            : catalog?.ResolvePrice(row.ProviderId, row.ModelId) ??
              ModelPricingCatalog.TryResolve(row.ProviderId, row.ModelId);
        var hasComponentTokens = row.InputTokens > 0 || row.CacheReadTokens > 0 ||
                                 row.CacheWriteTokens > 0 || row.OutputTokens > 0 || row.ReasoningTokens > 0;
        if (pricing is null || !hasComponentTokens)
        {
            // Without token components there is nothing to price, so a persisted cost is the
            // only meaningful value.
            if (row.CostUsd > 0)
                return new OpenCodePricedUsage(row, row.CostUsd, UsageCostBasis.ProviderReported,
                    PricingBasis.ProviderCredits, false, 0);
            return new OpenCodePricedUsage(row, 0, UsageCostBasis.Unpriced, PricingBasis.Unknown, true, 0);
        }

        var estimatedCost = pricing.Estimate(row.InputTokens, row.CacheReadTokens,
            row.OutputTokens + row.ReasoningTokens, row.CacheWriteTokens);
        var cacheSavings = row.CacheReadTokens / 1_000_000d *
            Math.Max(0, pricing.InputPerMillion - pricing.CachedInputPerMillion);
        return new OpenCodePricedUsage(row, estimatedCost, UsageCostBasis.CatalogEstimated,
            PricingBasis.LocalEstimate, true, cacheSavings);
    }

    private static void AddGoMeters(ICollection<MetricLine> lines, IReadOnlyList<OpenCodeUsageRow> goRows,
        DateTimeOffset? firstGoUsageAt, DateTimeOffset now, IModelCatalog? catalog)
    {
        const double sessionCap = 12;
        const double weeklyCap = 30;
        const double monthlyCap = 60;
        // The meters price rows from the catalog exactly like the history total does. OpenCode's
        // persisted per-message cost is unreliable (half the market rate for some models), so
        // mixing it into the quota bars while the history uses the catalog rate made the meters
        // disagree with both the history and the OpenCode Go billing dashboard.
        double SessionCost(OpenCodeUsageRow row) => Price(row, catalog).CostUsd;
        var nowUtc = now.ToUniversalTime();
        var sessionStart = nowUtc.AddHours(-5);
        var currentSession = goRows.Where(row => row.Timestamp >= sessionStart && row.Timestamp < nowUtc).ToArray();
        // The 5-hour session window boundary is derived from the first message in the current
        // window. With no usage at all there is no boundary to report: a perpetual "resets in
        // five hours" countdown would be fabricated data, so the meter gets no reset time.
        var sessionResetsAt = currentSession.Length == 0
            ? (DateTimeOffset?)null
            : currentSession.Min(row => row.Timestamp).AddHours(5);

        var weekStart = StartOfUtcWeek(nowUtc);
        var weekEnd = weekStart.AddDays(7);
        var monthStart = AnchoredMonthStart(nowUtc, firstGoUsageAt);
        var monthEnd = AnchoredMonthStart(monthStart.AddMonths(1), firstGoUsageAt);

        lines.Add(MetricLine.Progress("Session", currentSession.Sum(SessionCost), sessionCap, MetricKind.Dollars,
            sessionResetsAt, TimeSpan.FromHours(5)));
        lines.Add(MetricLine.Progress("Weekly", SumBetween(goRows, weekStart, weekEnd, catalog), weeklyCap, MetricKind.Dollars,
            weekEnd, TimeSpan.FromDays(7)));
        lines.Add(MetricLine.Progress("Monthly", SumBetween(goRows, monthStart, monthEnd, catalog), monthlyCap, MetricKind.Dollars,
            monthEnd, monthEnd - monthStart));
    }

    private static double SumBetween(IEnumerable<OpenCodeUsageRow> rows, DateTimeOffset start, DateTimeOffset end,
        IModelCatalog? catalog) =>
        rows.Where(row => row.Timestamp >= start && row.Timestamp < end)
            .Sum(row => Price(row, catalog).CostUsd);

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

public enum OpenCodeCostStatus
{
    ProviderReported,
    Unpriced
}

public sealed record OpenCodeUsageRow(
    DateTimeOffset Timestamp,
    double CostUsd,
    double Tokens,
    string ProviderId,
    string? ModelId = null,
    double InputTokens = 0,
    double CacheReadTokens = 0,
    double CacheWriteTokens = 0,
    double OutputTokens = 0,
    double ReasoningTokens = 0,
    OpenCodeCostStatus CostStatus = OpenCodeCostStatus.ProviderReported,
    string? MessageId = null);

public sealed record OpenCodeUsageScan(bool HasDatabase, bool HasGoCredential,
    DateTimeOffset? FirstGoUsageAt, IReadOnlyList<OpenCodeUsageRow> Rows)
{
    public IReadOnlyList<string> DatabasePaths { get; init; } = Array.Empty<string>();
}

internal sealed record OpenCodePricedUsage(
    OpenCodeUsageRow Row,
    double CostUsd,
    UsageCostBasis CostBasis,
    PricingBasis PricingBasis,
    bool Estimated,
    double CacheSavingsUsd);

public sealed class OpenCodeUsageScanner
{
    private const string GoProviderId = "opencode-go";
    private readonly Func<string?> _dataDirectoryOverride;
    private readonly Func<string?> _xdgDataHome;
    private readonly Func<string> _userProfile;
    private readonly IOpenCodeDatabaseLocator _databaseLocator;
    private readonly TimeZoneInfo _localTimeZone;

    public OpenCodeUsageScanner(
        Func<string?>? dataDirectoryOverride = null,
        Func<string?>? xdgDataHome = null,
        Func<string>? userProfile = null,
        Func<string?>? localAppData = null,
        IOpenCodeDatabaseLocator? databaseLocator = null,
        TimeZoneInfo? localTimeZone = null,
        int historyDays = 90)
    {
        if (historyDays <= 0) throw new ArgumentOutOfRangeException(nameof(historyDays));
        _dataDirectoryOverride = dataDirectoryOverride ?? (() => Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR"));
        _xdgDataHome = xdgDataHome ?? (() => Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var explicitDirectory = Trim(_dataDirectoryOverride());
        _databaseLocator = databaseLocator ?? new OpenCodeDatabaseLocator(
            () => explicitDirectory,
            _xdgDataHome,
            _userProfile,
            localAppData,
            includeDefaultLocations: true);
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        HistoryDays = historyDays;
    }

    public int HistoryDays { get; }
    public TimeZoneInfo LocalTimeZone => _localTimeZone;

    public DateOnly LocalDate(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, _localTimeZone).Date);

    public OpenCodeUsageScan Scan(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var discovery = _databaseLocator.Discover();
        var auth = discovery.AuthPaths.Any(ReadGoCredential);
        if (discovery.DatabasePaths.Count == 0)
            return new OpenCodeUsageScan(false, auth, null, Array.Empty<OpenCodeUsageRow>());

        var cutoff = now.ToUniversalTime().AddDays(-HistoryDays).ToUnixTimeMilliseconds();
        var rows = new List<OpenCodeUsageRow>();
        // A message can appear in more than one discovered database (release-channel copies,
        // portable installs alongside default locations). Deduplicate by the message id the
        // database itself persists so overlapping copies are never double-counted.
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? firstGoUsageAt = null;
        var successfulReads = 0;
        var unsupportedSchemas = 0;
        foreach (var path in discovery.DatabasePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString());
                connection.Open();
                var table = DiscoverMessageTable(connection);
                foreach (var row in ReadRows(connection, table, cutoff))
                {
                    if (row.MessageId is { Length: > 0 } id && !seenMessageIds.Add(id)) continue;
                    rows.Add(row);
                }
                var candidate = ReadFirstGoUsage(connection, table);
                if (candidate is not null && (firstGoUsageAt is null || candidate < firstGoUsageAt)) firstGoUsageAt = candidate;
                successfulReads++;
            }
            catch (OpenCodeDatabaseException)
            {
                unsupportedSchemas++;
            }
            catch (SqliteException)
            {
                // A second release-channel database may be locked or stale. Only fail when every
                // local database is unreadable, otherwise use the healthy history we did obtain.
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (successfulReads == 0)
        {
            var message = unsupportedSchemas > 0
                ? "OpenCode databases do not expose a supported message schema."
                : "OpenCode databases could not be read.";
            throw new OpenCodeDatabaseException(message);
        }

        return new OpenCodeUsageScan(true, auth, firstGoUsageAt, rows)
        {
            DatabasePaths = discovery.DatabasePaths
        };
    }

    private static MessageTableDescriptor DiscoverMessageTable(SqliteConnection connection)
    {
        var tables = ReadTables(connection);
        foreach (var tableName in new[] { "message", "session_message" })
        {
            if (!tables.Contains(tableName)) continue;
            var columns = ReadColumns(connection, tableName);
            if (columns.Contains("time_created") && columns.Contains("data"))
                return new MessageTableDescriptor(tableName, columns.Contains("id"));
        }

        throw new OpenCodeDatabaseException("OpenCode message table schema is unsupported.");
    }

    private static HashSet<string> ReadTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && !reader.IsDBNull(0)) tables.Add(reader.GetString(0));
        return tables;
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && !reader.IsDBNull(1)) columns.Add(reader.GetString(1));
        return columns;
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

    private static IEnumerable<OpenCodeUsageRow> ReadRows(
        SqliteConnection connection, MessageTableDescriptor table, long cutoff)
    {
        // The message id lives in the table's own id column (OpenCode omits "id" from the data
        // JSON), with json_extract(data,'$.id') kept as a fallback for sanitized or legacy copies.
        var idColumn = table.HasIdColumn ? $", {QuoteIdentifier(table.Name)}.id" : string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT time_created,
                   json_extract(data, '$.cost'),
                   json_extract(data, '$.tokens.total'),
                   json_extract(data, '$.tokens.input'),
                   json_extract(data, '$.tokens.cache.read'),
                   json_extract(data, '$.tokens.cache.write'),
                   json_extract(data, '$.tokens.output'),
                   json_extract(data, '$.tokens.reasoning'),
                   json_extract(data, '$.providerID'),
                   json_extract(data, '$.modelID'),
                   json_extract(data, '$.id')
                   {idColumn}
            FROM {QuoteIdentifier(table.Name)}
            WHERE time_created >= $cutoff
              AND json_valid(data)
              AND json_extract(data, '$.role') = 'assistant';
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!TryNumber(reader.GetValue(0), out var timestamp) || !double.IsFinite(timestamp)) continue;
            if (!TryTimestamp(timestamp, out var createdAt)) continue;

            var hasCost = TryNumber(reader.GetValue(1), out var cost);
            var hasTotal = TryNumber(reader.GetValue(2), out var total);
            var hasInput = TryNumber(reader.GetValue(3), out var input);
            var hasCacheRead = TryNumber(reader.GetValue(4), out var cacheRead);
            var hasCacheWrite = TryNumber(reader.GetValue(5), out var cacheWrite);
            var hasOutput = TryNumber(reader.GetValue(6), out var output);
            var hasReasoning = TryNumber(reader.GetValue(7), out var reasoning);
            if (!AreNonNegative(input, cacheRead, cacheWrite, output, reasoning)) continue;

            var componentTotal = input + cacheRead + cacheWrite + output + reasoning;
            if (!hasTotal) total = componentTotal;
            if (!double.IsFinite(total) || total < 0) continue;
            if (!hasCost) cost = 0;
            if (!double.IsFinite(cost) || cost < 0) continue;
            if (!hasCost && total <= 0) continue;
            if (hasCost && cost <= 0 && total <= 0) continue;

            var providerId = ReadText(reader.GetValue(8)) ?? string.Empty;
            var modelId = ReadText(reader.GetValue(9));
            var dataId = ReadText(reader.GetValue(10));
            var tableId = table.HasIdColumn ? ReadText(reader.GetValue(11)) : null;
            var messageId = !string.IsNullOrWhiteSpace(tableId) ? tableId : dataId;
            yield return new OpenCodeUsageRow(
                createdAt,
                cost,
                total,
                providerId,
                modelId,
                input,
                cacheRead,
                cacheWrite,
                output,
                reasoning,
                hasCost ? OpenCodeCostStatus.ProviderReported : OpenCodeCostStatus.Unpriced,
                messageId);
        }
    }

    private static DateTimeOffset? ReadFirstGoUsage(SqliteConnection connection, MessageTableDescriptor table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT MIN(time_created)
            FROM {QuoteIdentifier(table.Name)}
            WHERE json_valid(data)
              AND json_extract(data, '$.role') = 'assistant'
              AND json_extract(data, '$.providerID') = 'opencode-go';
            """;
        var value = command.ExecuteScalar();
        return TryNumber(value, out var timestamp) && TryTimestamp(timestamp, out var createdAt)
            ? createdAt
            : null;
    }

    private static bool TryTimestamp(double value, out DateTimeOffset timestamp)
    {
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)value));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
        catch (OverflowException)
        {
            timestamp = default;
            return false;
        }
    }

    private static bool AreNonNegative(params double[] values) =>
        values.All(value => double.IsFinite(value) && value >= 0);

    private static string? ReadText(object? value) =>
        value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

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

    private sealed record MessageTableDescriptor(string Name, bool HasIdColumn);
}

public sealed class OpenCodeDatabaseException : Exception
{
    public OpenCodeDatabaseException() { }
    public OpenCodeDatabaseException(string message) : base(message) { }
}

public sealed class OpenCodeAuthException : Exception;
