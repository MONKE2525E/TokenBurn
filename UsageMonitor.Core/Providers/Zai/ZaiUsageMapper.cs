using UsageMonitor.Core.Providers;

namespace UsageMonitor.Core.Providers.Zai;

/// <summary>Normalized values from Z.ai's GLM Coding Plan quota endpoints.</summary>
public sealed record ZaiMappedUsage(string? Plan, IReadOnlyList<MetricLine> Lines);

/// <summary>
/// Maps the quota payload used by Z.ai's own Coding Plan dashboard. The endpoints are not public
/// product APIs, so this parser is deliberately strict about values and deliberately tolerant of
/// unknown future windows. A malformed recognized meter is an error, never a fabricated zero.
/// </summary>
public static class ZaiUsageMapper
{
    public static readonly Uri SubscriptionUri = new("https://api.z.ai/api/biz/subscription/list");
    public static readonly Uri QuotaUri = new("https://api.z.ai/api/monitor/usage/quota/limit");
    public static readonly TimeSpan MonthlyPeriod = TimeSpan.FromDays(30);

    public static ZaiMappedUsage Map(ProviderHttpResponse quotaResponse, ProviderHttpResponse? subscriptionResponse,
        DateTimeOffset now)
    {
        if (quotaResponse.StatusCode is 401 or 403)
            throw new ZaiAuthenticationException("Z.ai API key is invalid or expired.");
        if (quotaResponse.StatusCode == 429)
            throw new ZaiRequestException(quotaResponse.StatusCode);
        if (quotaResponse.StatusCode < 200 || quotaResponse.StatusCode >= 300)
            throw new ZaiRequestException(quotaResponse.StatusCode);
        if (IsNoCodingPlan(quotaResponse.Body))
            throw new ZaiNoCodingPlanException();

        var plan = subscriptionResponse is { StatusCode: >= 200 and < 300 }
            ? PlanName(subscriptionResponse.Body)
            : null;
        return new ZaiMappedUsage(plan, MapQuota(quotaResponse.Body, now));
    }

    public static bool IsNoCodingPlan(string body)
    {
        using var document = ProviderJson.Parse(body);
        if (document is null) return false;
        var root = document.RootElement;
        var success = ProviderJson.Bool(ProviderJson.Property(root, "success"));
        var message = ProviderJson.String(ProviderJson.Property(root, "msg", "message"));
        return success == false && message?.Contains("coding plan", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string? PlanName(string body)
    {
        using var document = ProviderJson.Parse(body);
        if (document is null) return null;
        var data = ProviderJson.Array(ProviderJson.Property(document.RootElement, "data"));
        if (data is not { } list) return null;
        foreach (var entry in list.EnumerateArray())
        {
            var name = ProviderJson.String(ProviderJson.Property(entry, "productName", "product_name"));
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        return null;
    }

    public static IReadOnlyList<MetricLine> MapQuota(string body, DateTimeOffset now)
    {
        using var document = ProviderJson.Parse(body)
            ?? throw new ZaiParseException("Z.ai returned invalid quota JSON.");
        var root = document.RootElement;
        var container = ProviderJson.Object(ProviderJson.Property(root, "data")) ?? root;
        var limits = ProviderJson.Array(ProviderJson.Property(container, "limits"));
        if (limits is not { } array)
            throw new ZaiParseException("Z.ai quota response was missing its limits array.");
        if (array.GetArrayLength() == 0)
            return new[] { MetricLine.NoUsageData };

        MetricLine? session = null;
        TimeSpan sessionWindow = TimeSpan.MaxValue;
        MetricLine? weekly = null;
        TimeSpan weeklyWindow = TimeSpan.Zero;
        MetricLine? searches = null;
        var recognized = false;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var type = ProviderJson.String(ProviderJson.Property(entry, "type", "name"));
            if (string.Equals(type, "TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                var duration = TokenWindow(entry);
                if (duration is null) continue;
                recognized = true;
                var line = PercentLine(entry, duration.Value, now);
                if (duration.Value < TimeSpan.FromDays(1))
                {
                    if (duration.Value < sessionWindow) { session = line; sessionWindow = duration.Value; }
                }
                else if (duration.Value > weeklyWindow)
                {
                    weekly = line;
                    weeklyWindow = duration.Value;
                }
            }
            else if (string.Equals(type, "TIME_LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                recognized = true;
                searches = WebSearchLine(entry, now);
            }
        }

        if (session is null && weekly is null && searches is null)
            return recognized ? throw new ZaiParseException("Z.ai quota values were not valid.") : new[] { MetricLine.NoUsageData };

        var lines = new List<MetricLine>(3);
        if (session is not null) lines.Add(session);
        if (weekly is not null) lines.Add(weekly);
        if (searches is not null) lines.Add(searches);
        return lines;
    }

    private static TimeSpan? TokenWindow(System.Text.Json.JsonElement entry)
    {
        var unit = ProviderJson.Number(ProviderJson.Property(entry, "unit"));
        var count = ProviderJson.Number(ProviderJson.Property(entry, "number"));
        if (unit is not { } unitValue || count is not { } countValue ||
            !double.IsFinite(unitValue) || !double.IsFinite(countValue) || countValue <= 0)
            throw new ZaiParseException("Z.ai token quota window was malformed.");
        var unitDuration = unitValue switch
        {
            3 => TimeSpan.FromHours(1),
            4 => TimeSpan.FromDays(1),
            5 => MonthlyPeriod,
            6 => TimeSpan.FromDays(7),
            _ => (TimeSpan?)null
        };
        if (unitDuration is null) return null;
        var ticks = unitDuration.Value.Ticks * countValue;
        if (!double.IsFinite(ticks) || ticks < TimeSpan.TicksPerMillisecond || ticks > TimeSpan.MaxValue.Ticks)
            throw new ZaiParseException("Z.ai token quota window was out of range.");
        return TimeSpan.FromTicks((long)ticks);
    }

    private static MetricLine PercentLine(System.Text.Json.JsonElement entry, TimeSpan window, DateTimeOffset now)
    {
        var raw = ProviderJson.Number(ProviderJson.Property(entry, "percentage", "percent"));
        if (raw is not { } percentage || !double.IsFinite(percentage))
            throw new ZaiParseException("Z.ai token quota percentage was missing or invalid.");
        var reset = ProviderJson.Date(ProviderJson.Property(entry, "nextResetTime", "next_reset_time"));
        var label = window < TimeSpan.FromDays(1) ? "Session" : "Weekly";
        return MetricLine.Progress(label, Math.Clamp(percentage, 0, 100), 100, MetricKind.Percent,
            resetsAt: reset, period: window, state: null);
    }

    private static MetricLine WebSearchLine(System.Text.Json.JsonElement entry, DateTimeOffset now)
    {
        var used = ProviderJson.Number(ProviderJson.Property(entry, "currentValue", "current_value"));
        var limit = ProviderJson.Number(ProviderJson.Property(entry, "usage", "limit"));
        if (used is not { } usedValue || limit is not { } limitValue ||
            !double.IsFinite(usedValue) || !double.IsFinite(limitValue) || usedValue < 0 || limitValue < 0)
            throw new ZaiParseException("Z.ai web-search quota values were missing or invalid.");
        var reset = ProviderJson.Date(ProviderJson.Property(entry, "nextResetTime", "next_reset_time"));
        return MetricLine.Progress("Web Searches", usedValue, limitValue, MetricKind.Count,
            resetsAt: reset, period: MonthlyPeriod);
    }
}

public sealed class ZaiAuthenticationException(string message) : Exception(message);
public sealed class ZaiNoCodingPlanException() : Exception("No active GLM Coding Plan. Subscribe at z.ai/subscribe to see usage.");
public sealed class ZaiRequestException(int statusCode) : Exception($"Z.ai quota request failed ({statusCode}).")
{
    public int StatusCode { get; } = statusCode;
}
public sealed class ZaiParseException(string message) : Exception(message);
