using System.Net.Http;
using UsageMonitor.Core.Providers;

namespace UsageMonitor.Core.Providers.Zai;

/// <summary>
/// Windows-native Z.ai GLM Coding Plan provider. The API key is stored in Windows Credential
/// Manager, and the subscription call is best effort so a plan-name outage never hides quotas.
/// </summary>
public sealed class ZaiProvider : IUsageProvider
{
    public const string SecretKey = ProviderSecretKeys.ZaiApiKey;
    public static readonly ProviderDescriptor Provider = new(
        ProviderIds.Zai,
        "Z.ai",
        "zai",
        new[]
        {
            new ProviderLink("Z.ai dashboard", "https://z.ai/manage-apikey/coding-plan/personal/my-plan"),
            new ProviderLink("Z.ai API keys", "https://z.ai/manage-apikey/apikey-list")
        });

    public ProviderDescriptor Descriptor => Provider;

    private readonly IProviderHttpClient _http;
    private readonly ISecretStore _defaultSecrets;

    public ZaiProvider(IProviderHttpClient? http = null, ISecretStore? secrets = null)
    {
        _http = http ?? new ProviderHttpClient();
        _defaultSecrets = secrets ?? new CredentialManagerSecretStore();
    }

    public async Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
    {
        var secrets = context.Secrets ?? _defaultSecrets;
        var apiKey = await secrets.GetAsync(SecretKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return ProviderSnapshot.Error(Provider, "Not configured. Add a Z.ai API key in Settings.", ProviderErrorCategory.NotConfigured);

        try
        {
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {apiKey}",
                ["Accept"] = "application/json",
                ["User-Agent"] = "UsageMonitor/1.0"
            };
            var quota = await _http.SendAsync(HttpMethod.Get, ZaiUsageMapper.QuotaUri, headers,
                proxy: context.Proxy, cancellationToken: cancellationToken).ConfigureAwait(false);
            ProviderHttpResponse? subscription = null;
            try
            {
                subscription = await _http.SendAsync(HttpMethod.Get, ZaiUsageMapper.SubscriptionUri, headers,
                    proxy: context.Proxy, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            var mapped = ZaiUsageMapper.Map(quota, subscription, context.Now);
            return ProviderSnapshot.Success(Provider, mapped.Lines, mapped.Plan ?? "GLM Coding Plan", context.Now);
        }
        catch (ZaiAuthenticationException ex)
        {
            return ProviderSnapshot.Error(Provider, ex.Message, ProviderErrorCategory.Authentication);
        }
        catch (ZaiNoCodingPlanException ex)
        {
            return ProviderSnapshot.Error(Provider, ex.Message, ProviderErrorCategory.Unsupported);
        }
        catch (ZaiRequestException ex)
        {
            return ProviderSnapshot.Error(Provider, ex.Message,
                ex.StatusCode == 429 ? ProviderErrorCategory.RateLimited : ProviderErrorCategory.Network);
        }
        catch (ZaiParseException ex)
        {
            return ProviderSnapshot.Error(Provider, ex.Message, ProviderErrorCategory.Parse);
        }
        catch (HttpRequestException)
        {
            return ProviderSnapshot.Error(Provider, "Z.ai connection failed.", ProviderErrorCategory.Network);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderSnapshot.Error(Provider, "Z.ai request timed out.", ProviderErrorCategory.Network);
        }
    }
}
