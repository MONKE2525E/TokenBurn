using System.Text.Json;

namespace UsageMonitor.Core.Providers.Antigravity;

public sealed record AntigravityMappedUsage(string? Plan, IReadOnlyList<MetricLine> Lines);

public sealed record AntigravityModelConfig(string Label, string? ModelId, double RemainingFraction, DateTimeOffset? ResetAt);

/// <summary>Pure response mapping ported from OpenUsage's Antigravity pool logic.</summary>
public static class AntigravityUsageMapper
{
    private static readonly (string Id, string Label, TimeSpan Period)[] SummaryBuckets =
    [
        ("gemini-5h", "Session", TimeSpan.FromHours(5)),
        ("gemini-weekly", "Weekly", TimeSpan.FromDays(7)),
        ("3p-5h", "Claude", TimeSpan.FromHours(5)),
        ("3p-weekly", "Claude Weekly", TimeSpan.FromDays(7))
    ];

    private static readonly HashSet<string> ModelBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "MODEL_CHAT_20706", "MODEL_CHAT_23310", "MODEL_GOOGLE_GEMINI_2_5_FLASH",
        "MODEL_GOOGLE_GEMINI_2_5_FLASH_THINKING", "MODEL_GOOGLE_GEMINI_2_5_FLASH_LITE",
        "MODEL_GOOGLE_GEMINI_2_5_PRO", "MODEL_PLACEHOLDER_M19", "MODEL_PLACEHOLDER_M9", "MODEL_PLACEHOLDER_M12"
    };

    public static IReadOnlyList<MetricLine>? ParseQuotaSummary(string body)
    {
        using var document = ProviderJson.Parse(body);
        if (document is null) return null;
        var root = document.RootElement;
        var response = ProviderJson.Object(ProviderJson.Property(root, "response"));
        var groups = ProviderJson.Array(ProviderJson.Property(response ?? root, "groups"));
        if (groups is not { } groupArray) return null;

        var lines = new List<MetricLine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groupArray.EnumerateArray())
        {
            var buckets = ProviderJson.Array(ProviderJson.Property(group, "buckets"));
            if (buckets is not { } bucketArray) continue;
            foreach (var bucket in bucketArray.EnumerateArray())
            {
                var id = ProviderJson.String(ProviderJson.Property(bucket, "bucketId", "bucket_id"));
                var spec = SummaryBuckets.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(spec.Id) || !seen.Add(spec.Id)) continue;
                var fraction = RemainingFraction(bucket);
                if (fraction is null) continue;
                lines.Add(ToLine(spec.Label, fraction.Value, ProviderJson.Date(ProviderJson.Property(bucket, "resetTime", "reset_time")), spec.Period));
            }
        }
        return lines;
    }

    public static string? ParsePlan(string body)
    {
        using var document = ProviderJson.Parse(body);
        if (document is null) return null;
        var root = document.RootElement;
        var paid = ProviderJson.Object(ProviderJson.Property(root, "paidTier", "paid_tier"));
        var current = ProviderJson.Object(ProviderJson.Property(root, "currentTier", "current_tier"));
        var raw = ProviderJson.String(ProviderJson.Property(paid ?? current ?? root, "name", "planName", "plan_name"));
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim();
        foreach (var tier in new[] { "Ultra", "Pro", "Free" })
            if (normalized.Contains(tier, StringComparison.OrdinalIgnoreCase)) return tier;
        if (normalized.StartsWith("Google AI ", StringComparison.OrdinalIgnoreCase)) normalized = normalized[10..];
        return normalized;
    }

    public static IReadOnlyList<MetricLine> ParseAvailableModels(string body)
    {
        using var document = ProviderJson.Parse(body);
        if (document is null) return Array.Empty<MetricLine>();
        var models = ProviderJson.Object(ProviderJson.Property(document.RootElement, "models"));
        if (models is not { } modelObject) return Array.Empty<MetricLine>();
        var configs = new List<AntigravityModelConfig>();
        foreach (var property in modelObject.EnumerateObject())
        {
            var model = property.Value;
            if (ProviderJson.Bool(ProviderJson.Property(model, "isInternal", "is_internal")) == true) continue;
            var label = ProviderJson.String(ProviderJson.Property(model, "displayName", "display_name", "label"));
            if (string.IsNullOrWhiteSpace(label)) continue;
            var quota = ProviderJson.Object(ProviderJson.Property(model, "quotaInfo", "quota_info"));
            var remaining = RemainingFraction(quota ?? model);
            // A model can be listed before its quota is provisioned. Treat that as unknown and
            // omit it rather than manufacturing a 100% used meter from a missing value.
            if (remaining is null) continue;
            configs.Add(new AntigravityModelConfig(label.Trim(),
                ProviderJson.String(ProviderJson.Property(model, "model")) ?? property.Name,
                remaining.Value,
                ProviderJson.Date(ProviderJson.Property(quota ?? model, "resetTime", "reset_time"))));
        }
        return BuildLines(configs);
    }

    public static IReadOnlyList<MetricLine> ParseQuotaBuckets(string body)
    {
        using var document = ProviderJson.Parse(body);
        if (document is null) return Array.Empty<MetricLine>();
        var buckets = ProviderJson.Array(ProviderJson.Property(document.RootElement, "buckets"));
        if (buckets is not { } array) return Array.Empty<MetricLine>();
        var configs = new List<AntigravityModelConfig>();
        foreach (var bucket in array.EnumerateArray())
        {
            var remaining = RemainingFraction(bucket);
            if (remaining is null) continue;
            configs.Add(new AntigravityModelConfig(
                ProviderJson.String(ProviderJson.Property(bucket, "modelId", "model_id")) ?? string.Empty,
                ProviderJson.String(ProviderJson.Property(bucket, "modelId", "model_id")),
                remaining.Value,
                ProviderJson.Date(ProviderJson.Property(bucket, "resetTime", "reset_time"))));
        }
        return BuildLines(configs);
    }

    public static IReadOnlyList<MetricLine> BuildLines(IEnumerable<AntigravityModelConfig> configs)
    {
        var pools = new Dictionary<string, (double Fraction, DateTimeOffset? Reset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            var label = NormalizeLabel(config.Label);
            if (label.Length == 0 || (config.ModelId is not null && ModelBlacklist.Contains(config.ModelId))) continue;
            var pool = label.Contains("gemini", StringComparison.OrdinalIgnoreCase) ? "Session" : "Claude";
            if (!pools.TryGetValue(pool, out var current) || config.RemainingFraction < current.Fraction)
                pools[pool] = (Math.Clamp(config.RemainingFraction, 0, 1), config.ResetAt);
        }
        return pools.OrderBy(x => x.Key == "Session" ? 0 : 1)
            .Select(x => ToLine(x.Key, x.Value.Fraction, x.Value.Reset, TimeSpan.FromHours(5)))
            .ToArray();
    }

    public static double? RemainingFraction(JsonElement element)
    {
        var raw = ProviderJson.Number(ProviderJson.Property(element, "remainingFraction", "remaining_fraction", "remaining"));
        if (raw is { } fraction && double.IsFinite(fraction))
            return fraction > 1 ? fraction / 100d : fraction;
        var used = ProviderJson.Number(ProviderJson.Property(element, "usedPercent", "used_percent", "percentUsed"));
        return used is { } percent && double.IsFinite(percent) ? 1d - Math.Clamp(percent / 100d, 0d, 1d) : null;
    }

    private static string NormalizeLabel(string label)
    {
        var value = label.Trim();
        var open = value.LastIndexOf('(');
        if (open > 0 && value.EndsWith(')')) value = value[..open].TrimEnd();
        return value;
    }

    private static MetricLine ToLine(string label, double remaining, DateTimeOffset? reset, TimeSpan period) =>
        MetricLine.Progress(label, Math.Round((1d - Math.Clamp(remaining, 0, 1)) * 100d), 100, MetricKind.Percent, reset, period);
}
