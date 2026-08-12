using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UsageMonitor.Core;

namespace UsageMonitor.LocalApi;

public sealed record UsageApiResponse(int StatusCode, string Body)
{
    public static UsageApiResponse Json(int statusCode, object value) =>
        new(statusCode, JsonSerializer.Serialize(value, UsageApiJson.Options));

    public static UsageApiResponse Error(int statusCode, string code) =>
        Json(statusCode, new { error = code });
}

/// <summary>Pure route selection and serialization shared by the HTTP host and command line.</summary>
public sealed class UsageApiService
{
    private readonly IUsageSnapshotSource _source;
    private readonly UsageApiOptions _options;
    private readonly SemaphoreSlim _concurrency;

    public UsageApiService(IUsageSnapshotSource? source = null, UsageApiOptions? options = null)
    {
        _source = source ?? new EmptyUsageSnapshotSource();
        _options = options ?? new UsageApiOptions();
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRequests));
    }

    public async Task<UsageApiResponse> HandleAsync(string method, string path, bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            return new UsageApiResponse(204, string.Empty);
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return UsageApiResponse.Error(405, "method_not_allowed");
        if (!await _concurrency.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            return UsageApiResponse.Error(503, "server_busy");

        try
        {
            var route = (path ?? string.Empty).Split('?', 2)[0]
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (route.Length < 2 || !string.Equals(route[0], "v1", StringComparison.OrdinalIgnoreCase))
                return UsageApiResponse.Error(404, "not_found");
            var kind = route[1].ToLowerInvariant();
            if (kind is not ("limits" or "usage") || route.Length > 3)
                return UsageApiResponse.Error(404, "not_found");

            string? providerId = null;
            if (route.Length == 3)
            {
                try
                {
                    // The host already hands over a decoded path; decoding a second time only
                    // unescapes percent signs that survived Kestrel (e.g. a literal %25) and can
                    // throw on malformed escapes. Malformed input is a client error, never a 500.
                    providerId = Uri.UnescapeDataString(route[2]);
                }
                catch (UriFormatException)
                {
                    return UsageApiResponse.Error(400, "bad_request");
                }
                if (providerId is null || !_source.KnownProviderIds.Contains(providerId))
                    return UsageApiResponse.Error(404, "provider_not_found");
            }

            var snapshots = await _source.GetSnapshotsAsync(providerId, force, cancellationToken)
                .ConfigureAwait(false);
            return kind == "usage"
                ? UsageApiResponse.Json(200, snapshots.Select(ToLegacySnapshot).ToArray())
                : UsageApiResponse.Json(200, ToLimitsEnvelope(snapshots));
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public string SerializeLimits(IReadOnlyList<UsageSnapshotData> snapshots, DateTimeOffset? generatedAt = null) =>
        JsonSerializer.Serialize(ToLimitsEnvelope(snapshots, generatedAt), UsageApiJson.Options);

    private LimitsEnvelope ToLimitsEnvelope(IReadOnlyList<UsageSnapshotData> snapshots, DateTimeOffset? generatedAt = null)
    {
        var now = generatedAt ?? DateTimeOffset.UtcNow;
        var providers = snapshots.ToDictionary(s => s.ProviderId, s => ToLimitsProvider(s, now), StringComparer.OrdinalIgnoreCase);
        var errors = snapshots.Where(s => !string.IsNullOrWhiteSpace(s.Error))
            .Select(s => new LimitsError(s.ProviderId, SensitiveDataRedactor.Redact(s.Error!))).ToArray();
        return new LimitsEnvelope("openusage.limits.v1", now, providers, errors);
    }

    private LimitsProvider ToLimitsProvider(UsageSnapshotData snapshot, DateTimeOffset generatedAt)
    {
        var expiresAt = snapshot.FetchedAt + _options.SnapshotFreshness;
        var resources = new Dictionary<string, LimitsResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var progress in snapshot.Lines.OfType<ProgressMetricData>())
        {
            var key = StableResourceKey(snapshot.ProviderId, progress.Label);
            var used = Math.Max(0, progress.Used);
            var limit = Math.Max(0, progress.Limit);
            resources[key] = new LimitsResource("consumption", progress.Unit, used, null, limit,
                Math.Max(0, limit - used), limit > 0 ? used / limit : null, progress.ResetsAt,
                progress.PeriodDurationMs is { } ms ? ms / 1000d : null, null, null);
        }
        foreach (var values in snapshot.Lines.OfType<ValuesMetricData>())
        {
            var value = values.Values.FirstOrDefault();
            if (value is null) continue;
            resources[StableResourceKey(snapshot.ProviderId, values.Label)] = new LimitsResource(
                "balance", value.Unit, null, value.Number, null, null, null, null, null,
                values.ExpiresAt is { Count: > 0 } ? values.ExpiresAt : null,
                value.Estimated ? true : null);
        }
        return new LimitsProvider(snapshot.DisplayName, snapshot.Plan, snapshot.FetchedAt, expiresAt,
            generatedAt >= expiresAt, resources);
    }

    private static string StableResourceKey(string providerId, string label)
    {
        var slug = new string(label.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        return slug switch
        {
            "spark_weekly" => "sparkWeekly",
            "extra_usage" => "extraUsage",
            "web_searches" => "webSearches",
            _ => slug.Length == 0 ? providerId : slug
        };
    }

    private static LegacySnapshot ToLegacySnapshot(UsageSnapshotData snapshot) => new(
        snapshot.ProviderId, snapshot.DisplayName, snapshot.Plan,
        snapshot.Lines.Select(metric => metric is BadgeMetricData badge &&
                string.Equals(badge.Label, "Error", StringComparison.OrdinalIgnoreCase)
            ? badge with { Text = SensitiveDataRedactor.Redact(badge.Text) }
            : metric).Select(ToLegacyLine).ToArray(),
        snapshot.FetchedAt,
        snapshot.UsageHistory,
        snapshot.Error is null ? null : SensitiveDataRedactor.Redact(snapshot.Error),
        snapshot.Warning is null ? null : SensitiveDataRedactor.Redact(snapshot.Warning));

    private static LegacyLine ToLegacyLine(UsageMetricData metric) => metric switch
    {
        ProgressMetricData p => new("progress", p.Label, null, null, p.Used, p.Limit,
            new LegacyFormat(string.Equals(p.Unit, "usd", StringComparison.OrdinalIgnoreCase) ? "dollars" : p.Unit),
            p.ResetsAt, p.PeriodDurationMs, p.Color, null, null, null),
        TextMetricData t => new("text", t.Label, t.Value, null, null, null, null, null, null,
            t.Color, t.Subtitle, null, null),
        ValuesMetricData v => new("text", v.Label, string.Join(" · ", v.Values.Select(FormatValue)), null,
            null, null, null, v.ExpiresAt is { Count: > 0 } ? v.ExpiresAt[0] : null,
            null, v.Color, null, null, null),
        BadgeMetricData b => new("badge", b.Label, null, b.Text, null, null, null, null, null,
            b.Color, b.Subtitle, null, null),
        BarChartMetricData c => new("barChart", c.Label, null, null, null, null, null, null, null,
            null, null, c.Points, c.Note),
        _ => new("text", metric.Label, string.Empty, null, null, null, null, null, null, null, null, null, null)
    };

    private static string FormatValue(ScalarValueData value)
    {
        // A non-finite metric must never surface as a literal "NaN"/"Infinity" string on the wire.
        if (!double.IsFinite(value.Number)) return string.Empty;
        return value.Unit switch
        {
            "usd" or "dollars" => "$" + value.Number.ToString("0.00", CultureInfo.InvariantCulture),
            _ => value.Number.ToString("0.##", CultureInfo.InvariantCulture)
        };
    }

    private sealed record LegacySnapshot(
        [property: JsonPropertyName("providerId")] string ProviderId, string DisplayName, string? Plan,
        IReadOnlyList<LegacyLine> Lines, DateTimeOffset FetchedAt, UsageHistoryData? UsageHistory,
        string? Error = null, string? Warning = null);

    private sealed record LegacyFormat([property: JsonPropertyName("kind")] string Kind);

    private sealed record LegacyLine(
        string Type, string Label, string? Value, string? Text, double? Used, double? Limit,
        LegacyFormat? Format, DateTimeOffset? ResetsAt, long? PeriodDurationMs, string? Color,
        string? Subtitle, IReadOnlyList<ChartPointData>? Points, string? Note);

    private sealed record LimitsEnvelope(
        string Schema, DateTimeOffset GeneratedAt,
        IReadOnlyDictionary<string, LimitsProvider> Providers, IReadOnlyList<LimitsError> Errors);

    private sealed record LimitsProvider(
        string DisplayName, string? Plan, DateTimeOffset FetchedAt, DateTimeOffset ExpiresAt,
        bool Stale, IReadOnlyDictionary<string, LimitsResource> Resources);

    private sealed record LimitsResource(
        string Kind, string Unit, double? Used, double? Available, double? Limit,
        double? Remaining, double? Utilization, DateTimeOffset? ResetsAt, double? WindowSeconds,
        IReadOnlyList<DateTimeOffset>? ExpiresAt, bool? Estimated);

    private sealed record LimitsError([property: JsonPropertyName("providerId")] string ProviderId, string Message);
}

internal static class UsageApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters =
        {
            // A non-finite metric (NaN/Infinity leaking from a provider or a division) must never
            // be emitted as a JSON string that strict consumers reject, nor silently as a healthy
            // number. Absent (null) keeps the field omitted by WhenWritingNull.
            new FiniteDoubleConverter(),
            new FiniteNullableDoubleConverter()
        }
    };

    private sealed class FiniteDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
    }

    private sealed class FiniteNullableDoubleConverter : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value is not { } number) writer.WriteNullValue();
            else if (double.IsFinite(number)) writer.WriteNumberValue(number);
            else writer.WriteNullValue();
        }
    }
}
