namespace UsageMonitor.Core.Providers.Claude;

public sealed record ClaudeMappedUsage(string? Plan, IReadOnlyList<MetricLine> Lines, string? Warning = null);

public static class ClaudeUsageMapper
{
    public static ClaudeMappedUsage MapUsageResponse(ProviderHttpResponse response, ClaudeOAuth credentials, DateTimeOffset now) => Map(response, credentials, now);

    public static ClaudeMappedUsage Map(ProviderHttpResponse response, ClaudeOAuth credentials, DateTimeOffset now)
    {
        if (response.StatusCode is 401 or 403) throw new ClaudeAuthenticationException("Claude session expired. Run `claude auth login`, then refresh.");
        if (response.StatusCode == 429) return RateLimited(credentials, ParseRetryAfterSeconds(response.Header("retry-after"), now));
        if (response.StatusCode < 200 || response.StatusCode >= 300)
            throw new ClaudeRequestException(response.StatusCode, ParseRetryAfterSeconds(response.Header("retry-after"), now));
        using var document = ProviderJson.Parse(response.Body) ?? throw new ClaudeParseException("Claude returned invalid usage JSON.");
        var root = document.RootElement;
        var lines = new List<MetricLine>();
        AppendWindow(lines, root, "five_hour", "Session", TimeSpan.FromHours(5));
        AppendWindow(lines, root, "seven_day", "Weekly", TimeSpan.FromDays(7));
        AppendWindow(lines, root, "seven_day_sonnet", "Sonnet", TimeSpan.FromDays(7));
        var limits = ProviderJson.Array(ProviderJson.Property(root, "limits"));
        if (limits is { } entries)
        {
            foreach (var item in entries.EnumerateArray())
            {
                if (!string.Equals(ProviderJson.String(ProviderJson.Property(item, "kind")), "weekly_scoped", StringComparison.OrdinalIgnoreCase)) continue;
                var scope = ProviderJson.Object(ProviderJson.Property(item, "scope"));
                var model = scope is { } scopeElement && ProviderJson.Object(ProviderJson.Property(scopeElement, "model")) is { } modelObj
                    ? ProviderJson.String(ProviderJson.Property(modelObj, "display_name", "name")) : null;
                var percent = ProviderJson.Number(ProviderJson.Property(item, "percent", "utilization"));
                if (percent is not null && !string.IsNullOrWhiteSpace(model))
                    lines.Add(MetricLine.Progress(model!, percent.Value, 100, MetricKind.Percent, ProviderJson.Date(ProviderJson.Property(item, "resets_at", "reset_at")), TimeSpan.FromDays(7)));
            }
        }
        var extra = ProviderJson.Object(ProviderJson.Property(root, "extra_usage", "extraUsage"));
        if (extra is { } extraObj && ProviderJson.Bool(ProviderJson.Property(extraObj, "is_enabled", "enabled")) == true)
        {
            var usedCents = ProviderJson.Number(ProviderJson.Property(extraObj, "used_credits", "used_cents", "used")) ?? 0;
            var limitCents = ProviderJson.Number(ProviderJson.Property(extraObj, "monthly_limit", "limit_cents", "limit"));
            var used = usedCents > 1000 ? usedCents / 100 : usedCents;
            var limit = limitCents is > 1000 ? limitCents.Value / 100 : limitCents;
            if (limit is > 0) lines.Add(MetricLine.Progress("Extra usage spent", used, limit.Value, MetricKind.Dollars));
            else if (used > 0) lines.Add(MetricLine.ValuesLine("Extra usage spent", new[] { new MetricValue(used, MetricKind.Dollars) }));
        }
        return new ClaudeMappedUsage(FormatPlan(credentials.SubscriptionType, credentials.RateLimitTier), lines);
    }

    public static ClaudeMappedUsage RateLimited(ClaudeOAuth credentials, int? retryAfterSeconds) => new(
        FormatPlan(credentials.SubscriptionType, credentials.RateLimitTier),
        new[] { MetricLine.Badge("Status", retryAfterSeconds is { } seconds ? $"Rate limited, retry in ~{FormatMinutes(seconds)}" : "Rate limited, try again later", "#F59E0B"), MetricLine.TextLine("Note", "Live usage rate limited - data may be stale") },
        retryAfterSeconds is { } retry
            ? $"Claude live updates are rate limited by Anthropic. Retry in ~{FormatMinutes(retry)}."
            : "Claude live updates are rate limited by Anthropic. Manual refreshes may extend the cooldown.");

    private static void AppendWindow(List<MetricLine> lines, System.Text.Json.JsonElement root, string key, string label, TimeSpan period)
    {
        var value = ProviderJson.Object(ProviderJson.Property(root, key));
        if (value is null) return;
        var used = ProviderJson.Number(ProviderJson.Property(value.Value, "utilization", "used_percent", "usedPercent"));
        if (used is null) return;
        lines.Add(MetricLine.Progress(label, used.Value, 100, MetricKind.Percent, ProviderJson.Date(ProviderJson.Property(value.Value, "resets_at", "reset_at")), period));
    }

    public static int? ParseRetryAfterSeconds(string? value, DateTimeOffset now)
    {
        if (int.TryParse(value, out var seconds) && seconds >= 0) return seconds;
        return DateTimeOffset.TryParse(value, out var date) ? Math.Max(0, (int)Math.Ceiling((date.ToUniversalTime() - now).TotalSeconds)) : null;
    }

    public static string? FormatPlan(string? subscription, string? tier)
    {
        if (string.IsNullOrWhiteSpace(subscription)) return null;
        var plan = string.Join(' ', subscription!.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + x[1..].ToLowerInvariant()));
        if (!string.IsNullOrWhiteSpace(tier))
        {
            var suffix = tier!.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.EndsWith('x'));
            if (suffix is not null) plan += " " + suffix;
        }
        return plan;
    }

    private static string FormatMinutes(int seconds) => seconds <= 0 ? "now" : $"{Math.Ceiling(seconds / 60d):0}m";
}

public sealed class ClaudeAuthenticationException(string message) : Exception(message);
public sealed class ClaudeRequestException(int statusCode, int? retryAfterSeconds = null) : Exception($"Claude usage request failed ({statusCode}).")
{
    public int StatusCode { get; } = statusCode;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
public sealed class ClaudeParseException(string message) : Exception(message);
