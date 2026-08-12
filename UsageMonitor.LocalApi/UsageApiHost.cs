using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UsageMonitor.Core;

namespace UsageMonitor.LocalApi;

/// <summary>Loopback-only ASP.NET transport for the shared usage API.</summary>
public sealed class UsageApiHost : IAsyncDisposable
{
    /// <summary>Rejects request bodies: the API is read-only and never consumes one.</summary>
    private const long MaxRequestBodyBytes = 8192;

    private readonly WebApplication _application;
    private readonly UsageApiOptions _options;
    private bool _started;

    private UsageApiHost(WebApplication application, UsageApiService service, UsageApiOptions options)
    {
        _application = application;
        Service = service;
        _options = options;
    }

    public UsageApiService Service { get; }
    public bool IsStarted => _started;

    /// <summary>
    /// Address callers should use. With a fixed port this is known before startup; with port 0
    /// (ephemeral) it is only correct once the server has bound and reported its actual address.
    /// </summary>
    public string BaseAddress => _started ? ActualAddress ?? $"http://{_options.Host}:{_options.Port}" : $"http://{_options.Host}:{_options.Port}";

    private string? ActualAddress => _application.Services.GetService<IServer>()?
        .Features.Get<IServerAddressesFeature>()?
        .Addresses
        .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address));

    public static UsageApiHost Create(IUsageSnapshotSource? source = null, UsageApiOptions? options = null)
    {
        options ??= new UsageApiOptions();
        if (!string.Equals(options.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The local API must bind to loopback.", nameof(options));

        var service = new UsageApiService(source, options);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(UsageApiHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
            Args = []
        });
        builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // The API is GET-only. A request carrying a body is either a misbehaving client or a
            // probe, so keep the framing limits tight and the header read bounded against
            // slow-loris connections that never complete.
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();

        // Catch every method so the pure service can return the documented JSON 405 response rather
        // than ASP.NET's default HTML/plain-text response for unsupported verbs.
        app.Map("/{**path}", async (HttpContext context) =>
        {
            if (!LoopbackRequestGate.IsAllowedHost(context.Request.Headers.Host.ToString()))
            {
                await WriteJsonAsync(context, 403, "{\"error\":\"forbidden_host\"}").ConfigureAwait(false);
                return;
            }
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) && !LoopbackRequestGate.IsAllowedOrigin(origin))
            {
                // A foreign webpage must not be able to trigger side effects (e.g. ?force=true
                // provider refreshes that hit provider APIs with real credentials) even though
                // CORS already stops it from reading the response. Native clients send no Origin.
                await WriteJsonAsync(context, 403, "{\"error\":\"forbidden_origin\"}").ConfigureAwait(false);
                return;
            }

            var contentLength = context.Request.ContentLength;
            if (contentLength is > MaxRequestBodyBytes)
            {
                await WriteJsonAsync(context, 413, "{\"error\":\"payload_too_large\"}").ConfigureAwait(false);
                return;
            }

            var force = context.Request.Query.TryGetValue("force", out var forceValue) &&
                        bool.TryParse(forceValue.FirstOrDefault(), out var forceParsed) && forceParsed;
            if (force && string.IsNullOrEmpty(origin) &&
                !LoopbackRequestGate.HasNativeClientMarker(
                    context.Request.Headers[LoopbackRequestGate.NativeClientMarkerHeader].ToString()))
            {
                // A forced refresh hits provider APIs with the user's real credentials. An
                // allowlisted Origin covers the embedded WebView; the marker covers native
                // clients. An <img>/<script> GET from a hostile webpage carries neither and must
                // not be able to trigger credential-bearing network activity.
                await WriteJsonAsync(context, 403, "{\"error\":\"forbidden_client\"}").ConfigureAwait(false);
                return;
            }
            UsageApiResponse response;
            try
            {
                response = await service.HandleAsync(context.Request.Method, context.Request.Path + context.Request.QueryString,
                    force, context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
            {
                // Malformed input must produce the documented JSON error envelope, never an
                // ASP.NET HTML 500. Provider refresh errors are already converted to snapshots by
                // the source and cannot reach this handler.
                response = UsageApiResponse.Error(400, "bad_request");
            }
            ApplyCors(context, options.EnableCors);
            context.Response.StatusCode = response.StatusCode;
            if (!string.IsNullOrEmpty(response.Body))
                await WriteJsonAsync(context, response.StatusCode, response.Body).ConfigureAwait(false);
        });

        return new UsageApiHost(app, service, options);
    }

    public static UsageApiHost Create(IUsageProviderCatalog catalog, IUsageCache? cache = null,
        UsageApiOptions? options = null) =>
        Create(new CoreUsageSnapshotSource(catalog, cache), options);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _application.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            // The dashboard remains usable if another process owns the port.  This mirrors the
            // upstream behavior: local API is an optional integration, never a launch blocker.
            FileDiagnosticsLogger.Default.Warning("The loopback usage API could not bind; the dashboard remains usable", exception: ex);
            _started = false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _application.StopAsync(cancellationToken).ConfigureAwait(false);
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        await _application.DisposeAsync().ConfigureAwait(false);
        _started = false;
    }

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, string body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private static void ApplyCors(HttpContext context, bool enabled)
    {
        if (!enabled) return;
        // The API exposes plan, quota, and spend history to any caller that reads the response,
        // so CORS must not open it to every website that can reach 127.0.0.1. Only the embedded
        // Tauri WebView origins may read it cross-origin; non-browser clients (CLI, HttpClient)
        // send no Origin header and are unaffected by CORS entirely.
        var origin = context.Request.Headers.Origin.ToString();
        // The gate accepts an absent Origin (native client), but only a present, allowlisted
        // origin may be reflected back.
        if (string.IsNullOrEmpty(origin) || !LoopbackRequestGate.IsAllowedOrigin(origin)) return;
        // The response varies on the request Origin: reflect only the exact allowed origin and
        // tell intermediaries not to reuse a cached response for a different origin.
        context.Response.Headers.Vary = "Origin";
        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
    }
}
