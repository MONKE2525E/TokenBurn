using System.Text.Json;

namespace UsageMonitor.Core.Providers.Grok;

/// <summary>
/// Maps the Grok CLI proxy's billing response (`GET /billing?format=credits`) into normalized
/// quota lines. The backend reports both a newer credits shape (`creditUsagePercent`,
/// `currentPeriod`) and deprecated fields (`monthlyLimit`/`used`); consumers prefer the new shape
/// and derive a percent from the deprecated fields when the new one is absent.
/// </summary>
internal static class GrokBillingMapper
{
    public sealed record GrokBillingResult(string Plan, IReadOnlyList<MetricLine> Lines);

    public static GrokBillingResult? Map(string body)
    {
        using var document = ProviderJson.Parse(body);
        if (document is null) return null;
        var root = document.RootElement;
        var plan = ProviderJson.String(ProviderJson.Property(root, "subscriptionTier"));
        if (string.IsNullOrWhiteSpace(plan)) plan = "Grok Build";
        var config = ProviderJson.Object(ProviderJson.Property(root, "config"));
        if (config is null)
            return new GrokBillingResult(plan,
                [MetricLine.Badge("Status", "No quota data", "#A3A3A3", state: MetricState.Unknown)]);

        var periodType = ReadString(config.Value, "currentPeriod", "type");
        var periodEnd = ReadDate(config.Value, "currentPeriod", "end");
        var label = PeriodLabel(periodType);

        var lines = new List<MetricLine>();
        var usagePercent = ProviderJson.Number(ProviderJson.Property(config.Value, "creditUsagePercent"));
        if (usagePercent is null)
        {
            var used = CentValue(config.Value, "used");
            var limit = CentValue(config.Value, "monthlyLimit");
            if (used is { } usedValue && limit is { } limitValue && limitValue > 0)
                usagePercent = usedValue / limitValue * 100;
        }

        if (usagePercent is { } pct && pct >= 0)
        {
            lines.Add(MetricLine.Progress(label, Math.Min(pct, 100), 100, MetricKind.Percent,
                resetsAt: periodEnd));
        }
        else
        {
            lines.Add(MetricLine.Badge("Status", "No quota data", "#A3A3A3", state: MetricState.Unknown));
        }

        // Prepaid (purchased) credits: a balance in USD cents, shown only when non-zero.
        if (CentValue(config.Value, "prepaidBalance") is { } prepaid && prepaid > 0)
        {
            lines.Add(MetricLine.ValuesLine("Credits",
                [new MetricValue(prepaid / 100.0, MetricKind.Dollars, "balance")]));
        }

        // Legacy pay-as-you-go on-demand billing, only when the backend enables it.
        var onDemandEnabled = ProviderJson.Bool(ProviderJson.Property(root, "onDemandEnabled")) ?? false;
        if (onDemandEnabled && CentValue(config.Value, "onDemandCap") is { } cap && cap > 0)
        {
            var used = CentValue(config.Value, "onDemandUsed") ?? 0;
            lines.Add(MetricLine.Progress("On-demand", Math.Min(used, cap), cap, MetricKind.Dollars,
                resetsAt: periodEnd));
        }

        // Some proxy revisions expose reset credits separately from the shared usage pool. Do not
        // manufacture a zero when the field is absent: the current billing response has no
        // banked-reset concept, while future revisions may provide one under either casing.
        var bankedResets = CountValue(ProviderJson.Property(config.Value,
            "bankedResets", "banked_reset_count", "rateLimitResetCredits", "rate_limit_reset_credits"));
        if (bankedResets is null)
            bankedResets = CountValue(ProviderJson.Property(root,
                "bankedResets", "banked_reset_count", "rateLimitResetCredits", "rate_limit_reset_credits"));
        if (bankedResets is >= 0)
            lines.Add(MetricLine.ValuesLine("Banked resets",
                [new MetricValue(bankedResets.Value, MetricKind.Count, "available")]));

        return new GrokBillingResult(plan, lines);
    }

    private static double? CountValue(JsonElement? value)
    {
        if (value is null) return null;
        var nested = ProviderJson.Object(value);
        return nested is { } objectValue
            ? ProviderJson.Number(ProviderJson.Property(objectValue, "available_count", "availableCount", "remaining", "count", "available", "val"))
            : ProviderJson.Number(value);
    }

    private static double? CentValue(JsonElement config, string name)
    {
        var cent = ProviderJson.Object(ProviderJson.Property(config, name));
        return cent is not null ? ProviderJson.Number(ProviderJson.Property(cent.Value, "val")) : null;
    }

    private static string? ReadString(JsonElement config, string section, string field)
    {
        var nested = ProviderJson.Object(ProviderJson.Property(config, section));
        return nested is not null ? ProviderJson.String(ProviderJson.Property(nested.Value, field)) : null;
    }

    private static DateTimeOffset? ReadDate(JsonElement config, string section, string field)
    {
        var nested = ProviderJson.Object(ProviderJson.Property(config, section));
        return nested is not null ? ProviderJson.Date(ProviderJson.Property(nested.Value, field)) : null;
    }

    private static string PeriodLabel(string? periodType)
    {
        if (string.IsNullOrWhiteSpace(periodType)) return "Usage";
        if (periodType.Contains("WEEKLY", StringComparison.OrdinalIgnoreCase)) return "Weekly limit";
        if (periodType.Contains("MONTHLY", StringComparison.OrdinalIgnoreCase)) return "Monthly limit";
        return "Usage";
    }
}
