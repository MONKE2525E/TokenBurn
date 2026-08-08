using UsageMonitor.Core;

namespace UsageMonitor.Desktop;

internal static class SettingsMigration
{
    public static void Normalize(UserSettings settings)
    {
        settings.DisabledProviders = (settings.DisabledProviders ?? [])
            .Select(ProviderCatalog.NormalizeId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        settings.StarredMetrics = (settings.StarredMetrics ?? [])
            .Select(NormalizeMetricKey)
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        settings.SpendMetric = NormalizeSpendMetric(settings.SpendMetric);
    }

    public static string NormalizeSpendMetric(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "tokens" => "tokens",
            "cost-mtok" or "cost/mtok" or "cost per million tokens" => "cost-mtok",
            _ => "cost"
        };

    private static string NormalizeMetricKey(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;
        var separator = trimmed.IndexOf(':');
        if (separator > 0)
        {
            var providerId = ProviderCatalog.NormalizeId(trimmed[..separator]);
            return providerId.Length == 0
                ? trimmed.ToLowerInvariant()
                : $"{providerId}:{trimmed[(separator + 1)..].Trim().ToLowerInvariant()}";
        }

        foreach (var descriptor in ProviderCatalog.DefaultDescriptors)
        {
            var prefix = descriptor.DisplayName + " ";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return $"{descriptor.Id}:{trimmed[prefix.Length..].Trim().ToLowerInvariant()}";
        }

        if (trimmed.Equals("Antigravity", StringComparison.OrdinalIgnoreCase))
            return $"{ProviderIds.Antigravity}:session";

        return trimmed.ToLowerInvariant();
    }
}
