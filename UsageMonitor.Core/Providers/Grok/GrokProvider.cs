using System.Text.Json;
using UsageMonitor.Core.Providers;

namespace UsageMonitor.Core.Providers.Grok;

public sealed class GrokProvider : IUsageProvider
{
    private static readonly ProviderDescriptor Provider = new(
        ProviderIds.Grok, "Grok", "grok", [new ProviderLink("xAI API", "https://docs.x.ai")]);
    private readonly IProviderHttpClient _http;

    public GrokProvider(IProviderHttpClient? http = null) => _http = http ?? new ProviderHttpClient();
    public ProviderDescriptor Descriptor => Provider;

    public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        var key = await ReadKeyAsync(context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
            return ProviderSnapshot.Error(Provider, "Not configured. Add an xAI API key as XAI_API_KEY.", ProviderErrorCategory.NotConfigured);
        try
        {
            var response = await _http.SendAsync(HttpMethod.Get, new Uri("https://api.x.ai/v1/models"),
                new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}", ["Accept"] = "application/json" },
                proxy: context.Proxy, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is 401 or 403)
                return ProviderSnapshot.Error(Provider, "The xAI API key was rejected.", ProviderErrorCategory.Authentication);
            if (response.StatusCode == 429)
                return ProviderSnapshot.Error(Provider, "The xAI API is rate limited.", ProviderErrorCategory.RateLimited);
            if (response.StatusCode is < 200 or >= 300)
                return ProviderSnapshot.Error(Provider, "The xAI model request failed.", ProviderErrorCategory.Network);
            if (response.Body.Length > 8_000_000)
                return ProviderSnapshot.Error(Provider, "xAI returned an unexpectedly large model list.", ProviderErrorCategory.Parse);
            using var document = ProviderJson.Parse(response.Body);
            if (document is null)
                return ProviderSnapshot.Error(Provider, "xAI returned invalid model data.", ProviderErrorCategory.Parse);
            var models = ProviderJson.Array(ProviderJson.Property(document?.RootElement ?? default, "data"));
            if (models is not { } modelArray)
                return ProviderSnapshot.Error(Provider, "xAI returned an invalid model list.", ProviderErrorCategory.Parse);
            var values = modelArray.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => ProviderJson.String(ProviderJson.Property(item, "id")))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => new MetricValue(1, MetricKind.Count, id))
                .ToArray();
            if (values.Length == 0)
                return ProviderSnapshot.Error(Provider, "xAI returned no usable models.", ProviderErrorCategory.Parse);
            return ProviderSnapshot.Success(Provider,
                [MetricLine.ValuesLine("Available models", values),
                 MetricLine.Badge("Status", "API connected", "#F2F2F0")], "xAI API", context.Now);
        }
        catch (HttpRequestException) { return ProviderSnapshot.Error(Provider, "xAI connection failed.", ProviderErrorCategory.Network); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return ProviderSnapshot.Error(Provider, "xAI request timed out.", ProviderErrorCategory.Network); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { return ProviderSnapshot.Error(Provider, "xAI returned invalid model data.", ProviderErrorCategory.Parse); }
    }

    private static Task<string?> ReadKeyAsync(ProviderContext context, CancellationToken cancellationToken)
        => context.Secrets is null
            ? Task.FromResult(Environment.GetEnvironmentVariable("XAI_API_KEY"))
            : ReadStoredOrEnvironmentAsync(context.Secrets, cancellationToken);

    private static async Task<string?> ReadStoredOrEnvironmentAsync(ISecretStore secrets, CancellationToken cancellationToken)
        => await secrets.GetAsync(ProviderSecretKeys.GrokApiKey, cancellationToken).ConfigureAwait(false)
           ?? Environment.GetEnvironmentVariable("XAI_API_KEY");
}
