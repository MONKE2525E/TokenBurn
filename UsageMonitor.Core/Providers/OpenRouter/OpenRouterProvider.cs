using System.Net.Http;

namespace UsageMonitor.Core.Providers.OpenRouter;

/// <summary>
/// Reads a user-entered OpenRouter management key from Windows Credential Manager and fetches
/// aggregate credit spend. OpenRouter's credits endpoint intentionally rejects ordinary inference
/// keys, so a 403 is surfaced as authorization required rather than being shown as zero usage.
/// </summary>
public sealed class OpenRouterProvider : IUsageProvider
{
    public const string SecretKey = ProviderSecretKeys.OpenRouterApiKey;
    public static readonly ProviderDescriptor Provider = new(
        ProviderIds.OpenRouter,
        "OpenRouter",
        "openrouter",
        new[] { new ProviderLink("OpenRouter", "https://openrouter.ai") });

    public ProviderDescriptor Descriptor => Provider;

    private readonly IProviderHttpClient _http;
    private readonly ISecretStore _defaultSecrets;

    public OpenRouterProvider(IProviderHttpClient? http = null, ISecretStore? secrets = null)
    {
        _http = http ?? new ProviderHttpClient();
        _defaultSecrets = secrets ?? new CredentialManagerSecretStore();
    }

    public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        var secrets = context.Secrets ?? _defaultSecrets;
        var apiKey = await secrets.GetAsync(SecretKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return ProviderSnapshot.Error(Provider, "Not configured. Add an OpenRouter management key in Settings.", ProviderErrorCategory.NotConfigured);

        try
        {
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {apiKey}",
                ["Accept"] = "application/json",
                ["User-Agent"] = "UsageMonitor/1.0"
            };
            var response = await _http.SendAsync(
                HttpMethod.Get,
                new Uri("https://openrouter.ai/api/v1/credits"),
                headers,
                proxy: context.Proxy,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var mapped = OpenRouterUsageMapper.Map(response, context.Now);
            return ProviderSnapshot.Success(Provider, mapped.Lines, plan: "API credits", refreshedAt: context.Now);
        }
        catch (OpenRouterAuthenticationException ex)
        {
            return ProviderSnapshot.Error(Provider, ex.Message, ProviderErrorCategory.Authentication);
        }
        catch (OpenRouterAuthorizationException ex)
        {
            return ProviderSnapshot.Error(Provider, ex.Message, ProviderErrorCategory.Authorization);
        }
        catch (OpenRouterRequestException ex)
        {
            return ProviderSnapshot.Error(Provider, ex.Message,
                ex.StatusCode == 429 ? ProviderErrorCategory.RateLimited : ProviderErrorCategory.Network);
        }
        catch (OpenRouterParseException ex)
        {
            return ProviderSnapshot.Error(Provider, ex.Message, ProviderErrorCategory.Parse);
        }
        catch (HttpRequestException)
        {
            return ProviderSnapshot.Error(Provider, "OpenRouter connection failed.", ProviderErrorCategory.Network);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderSnapshot.Error(Provider, "OpenRouter request timed out.", ProviderErrorCategory.Network);
        }
    }
}
