using System.Globalization;

namespace UsageMonitor.Core.Providers.OpenRouter;

/// <summary>Normalized values returned by OpenRouter's credits endpoint.</summary>
public sealed record OpenRouterMappedUsage(
    double TotalCredits,
    double TotalUsage,
    double Balance,
    IReadOnlyList<MetricLine> Lines);

/// <summary>Maps the management-key-only GET /api/v1/credits response.</summary>
public static class OpenRouterUsageMapper
{
    public static OpenRouterMappedUsage MapCreditsResponse(ProviderHttpResponse response, DateTimeOffset now) => Map(response, now);

    public static OpenRouterMappedUsage Map(ProviderHttpResponse response, DateTimeOffset now)
    {
        if (response.StatusCode is 401)
            throw new OpenRouterAuthenticationException("OpenRouter API key is invalid or expired.");
        if (response.StatusCode is 403)
            throw new OpenRouterAuthorizationException("OpenRouter credits require a management key. Add a management key in Settings.");
        if (response.StatusCode is 429)
            throw new OpenRouterRequestException(response.StatusCode);
        if (response.StatusCode < 200 || response.StatusCode >= 300)
            throw new OpenRouterRequestException(response.StatusCode);

        using var document = ProviderJson.Parse(response.Body)
            ?? throw new OpenRouterParseException("OpenRouter returned invalid credits JSON.");
        var root = document.RootElement;
        var data = ProviderJson.Object(ProviderJson.Property(root, "data")) ?? root;
        var totalCredits = ProviderJson.Number(ProviderJson.Property(data, "total_credits", "totalCredits"));
        var totalUsage = ProviderJson.Number(ProviderJson.Property(data, "total_usage", "totalUsage"));
        if (totalCredits is not { } credits || totalUsage is not { } usage ||
            double.IsNaN(credits) || double.IsInfinity(credits) || credits < 0 ||
            double.IsNaN(usage) || double.IsInfinity(usage) || usage < 0)
            throw new OpenRouterParseException("OpenRouter credits response was missing valid totals.");

        // OpenRouter may report usage a few cents above purchased credits while a charge settles.
        // Keep the raw spend, but never render a negative balance.
        var balance = Math.Max(0, credits - usage);
        var lines = new List<MetricLine>
        {
            MetricLine.Progress("Credits used", usage, credits, MetricKind.Dollars, period: null,
                state: credits <= 0 ? MetricState.Unknown : null),
            MetricLine.ValuesLine("Spend", new[] { new MetricValue(usage, MetricKind.Dollars, "total") }),
            MetricLine.ValuesLine("Balance", new[] { new MetricValue(balance, MetricKind.Dollars, "remaining") })
        };
        return new OpenRouterMappedUsage(credits, usage, balance, lines);
    }
}

public sealed class OpenRouterAuthenticationException(string message) : Exception(message);
public sealed class OpenRouterAuthorizationException(string message) : Exception(message);
public sealed class OpenRouterRequestException(int statusCode) : Exception($"OpenRouter credits request failed ({statusCode}).")
{
    public int StatusCode { get; } = statusCode;
}

public sealed class OpenRouterParseException(string message) : Exception(message);
