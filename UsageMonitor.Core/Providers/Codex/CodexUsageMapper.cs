using System.Globalization;
using System.Text.Json;

namespace UsageMonitor.Core.Providers.Codex;

public sealed record CodexMappedUsage(string? Plan, IReadOnlyList<MetricLine> Lines);

public static class CodexUsageMapper
{
    public static CodexMappedUsage Map(ProviderHttpResponse response, DateTimeOffset now)
    {
        if (response.StatusCode is 401 or 403) throw new CodexAuthenticationException("Codex session expired.");
        if (response.StatusCode < 200 || response.StatusCode >= 300) throw new CodexRequestException(response.StatusCode);
        using var document = ProviderJson.Parse(response.Body) ?? throw new CodexParseException("Codex returned invalid usage JSON.");
        var root = document.RootElement;
        var lines = new List<MetricLine>();
        var rate = ProviderJson.Object(ProviderJson.Property(root, "rate_limit", "rateLimit"));
        lines.AddRange(ClassifiedWindowLines(
            rate,
            ("Session", "Weekly"),
            ParseHeaderPercent(response.Header("x-codex-primary-used-percent")),
            ParseHeaderPercent(response.Header("x-codex-secondary-used-percent")),
            now));

        var additional = ProviderJson.Array(ProviderJson.Property(root, "additional_rate_limits", "additionalRateLimits"));
        if (additional is { } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var name = ProviderJson.String(ProviderJson.Property(item, "name", "model", "label", "limit_name", "metered_feature"));
                var child = ProviderJson.Object(ProviderJson.Property(item, "rate_limit", "rateLimit")) ?? item;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Contains("spark", StringComparison.OrdinalIgnoreCase))
                {
                    lines.AddRange(ClassifiedWindowLines(child, ("Spark", "Spark Weekly"), null, null, now));
                }
                else
                {
                    lines.AddRange(ClassifiedWindowLines(child, (name!, $"{name} Weekly"), null, null, now));
                }
            }
        }

        var credits = ProviderJson.Number(ProviderJson.Property(root, "credits", "credit_balance"));
        if (credits is null && ProviderJson.Object(ProviderJson.Property(root, "credits")) is { } creditObject)
            credits = ProviderJson.Number(ProviderJson.Property(creditObject, "balance", "remaining", "amount"));
        var remaining = ProviderJson.Number(ProviderJson.Property(root, "rate_limit_reset_credits", "rateLimitResetCredits", "resets_remaining"));
        if (remaining is null && ProviderJson.Object(ProviderJson.Property(root, "rate_limit_reset_credits")) is { } creditsObject)
            remaining = ProviderJson.Number(ProviderJson.Property(creditsObject, "remaining", "count", "available"));
        if (remaining is not null)
            lines.Add(MetricLine.ValuesLine("Rate Limit Resets", new[] { new MetricValue(remaining.Value, MetricKind.Count, "available") }));
        if (credits is not null)
            lines.Add(MetricLine.ValuesLine("Credits", new[] { new MetricValue(credits.Value, MetricKind.Dollars) }));

        return new CodexMappedUsage(
            FormatPlan(ProviderJson.String(ProviderJson.Property(root, "plan_type", "planType", "plan"))),
            lines);
    }

    private sealed record WindowCandidate(JsonElement? Window, double? HeaderUsed, bool FallbackWeekly);

    private enum WindowKind
    {
        Session,
        Weekly
    }

    private static IReadOnlyList<MetricLine> ClassifiedWindowLines(
        JsonElement? rate,
        (string Session, string Weekly) labels,
        double? primaryHeaderUsed,
        double? secondaryHeaderUsed,
        DateTimeOffset now)
    {
        var candidates = new List<WindowCandidate>();
        if (rate is { } rateObject)
        {
            candidates.Add(CreateCandidate(ProviderJson.Object(ProviderJson.Property(rateObject, "primary_window", "primaryWindow")), primaryHeaderUsed, fallbackWeekly: false));
            candidates.Add(CreateCandidate(ProviderJson.Object(ProviderJson.Property(rateObject, "secondary_window", "secondaryWindow")), secondaryHeaderUsed, fallbackWeekly: true));
        }
        else
        {
            if (primaryHeaderUsed is not null) candidates.Add(new WindowCandidate(null, primaryHeaderUsed, false));
            if (secondaryHeaderUsed is not null) candidates.Add(new WindowCandidate(null, secondaryHeaderUsed, true));
        }

        return new MetricLine?[]
        {
            ClassifiedWindowLine(WindowKind.Session, labels.Session, candidates, now),
            ClassifiedWindowLine(WindowKind.Weekly, labels.Weekly, candidates, now)
        }.OfType<MetricLine>().ToArray();
    }

    private static WindowCandidate CreateCandidate(JsonElement? window, double? headerUsed, bool fallbackWeekly)
        => new(window, headerUsed, fallbackWeekly);

    private static MetricLine? ClassifiedWindowLine(WindowKind kind, string label, IReadOnlyList<WindowCandidate> candidates, DateTimeOffset now)
    {
        var exact = candidates.FirstOrDefault(candidate => ExactKind(candidate.Window) == kind);
        var fallback = candidates.FirstOrDefault(candidate => ExactKind(candidate.Window) is null &&
            candidate.FallbackWeekly == (kind == WindowKind.Weekly));
        var candidate = exact ?? fallback;
        if (candidate is null) return null;

        var used = candidate.Window is { } window
            ? ProviderJson.Number(ProviderJson.Property(window, "used_percent", "usedPercent", "utilization"))
            : null;
        used ??= candidate.HeaderUsed;
        if (used is null || double.IsNaN(used.Value) || double.IsInfinity(used.Value)) return null;

        var reset = candidate.Window is { } resetWindow
            ? ProviderJson.Date(ProviderJson.Property(resetWindow, "reset_at", "resetAt", "resets_at"))
            : null;
        var periodSeconds = candidate.Window is { } periodWindow
            ? ProviderJson.Number(ProviderJson.Property(periodWindow, "limit_window_seconds", "period_seconds", "period"))
            : null;
        var defaultPeriod = kind == WindowKind.Session ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
        var period = periodSeconds is > 0 ? TimeSpan.FromSeconds(periodSeconds.Value) : defaultPeriod;
        return MetricLine.Progress(label, used.Value, 100, MetricKind.Percent, reset, period);
    }

    private static WindowKind? ExactKind(JsonElement? window)
    {
        if (window is not { } value) return null;
        var seconds = ProviderJson.Number(ProviderJson.Property(value, "limit_window_seconds", "period_seconds", "period"));
        if (seconds is null) return null;
        if (Math.Abs(seconds.Value - TimeSpan.FromHours(5).TotalSeconds) < 1) return WindowKind.Session;
        if (Math.Abs(seconds.Value - TimeSpan.FromDays(7).TotalSeconds) < 1) return WindowKind.Weekly;
        return null;
    }

    private static string? FormatPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return null;
        return string.Join(' ', plan!.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static double? ParseHeaderPercent(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}

public sealed class CodexAuthenticationException(string message) : Exception(message);
public sealed class CodexRequestException(int statusCode) : Exception($"Codex usage request failed ({statusCode}).") { public int StatusCode { get; } = statusCode; }
public sealed class CodexParseException(string message) : Exception(message);
