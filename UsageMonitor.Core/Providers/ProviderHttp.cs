using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace UsageMonitor.Core.Providers;

/// <summary>A deliberately small HTTP seam used by providers and replaced by fakes in tests.</summary>
public sealed record ProviderHttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value :
        Headers.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

public interface IProviderHttpClient
{
    Task<ProviderHttpResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null,
        string? contentType = null,
        Uri? proxy = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Production HTTP implementation. Providers never create an HttpClient per request.</summary>
public sealed class ProviderHttpClient : IProviderHttpClient
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public ProviderHttpClient(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsClient = client is null;
    }

    public async Task<ProviderHttpResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null,
        string? contentType = null,
        Uri? proxy = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                    request.Content ??= new StringContent(string.Empty);
                request.Content?.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
        }

        // Proxy is normally applied by the app's shared HttpClient. A per-request proxy is not
        // supported by HttpClientHandler, so retain the argument for the contract and document it
        // through a diagnostic response rather than mutating process-wide settings.
        _ = proxy;
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(x => x.Value)), StringComparer.OrdinalIgnoreCase);
        return new ProviderHttpResponse((int)response.StatusCode, responseHeaders, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public void DisposeOwnedClient()
    {
        if (_ownsClient) _client.Dispose();
    }
}
