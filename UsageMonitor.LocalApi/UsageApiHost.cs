using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UsageMonitor.Core;

namespace UsageMonitor.LocalApi;

/// <summary>Loopback-only ASP.NET transport for the shared usage API.</summary>
public sealed class UsageApiHost : IAsyncDisposable
{
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
    public string BaseAddress => $"http://{_options.Host}:{_options.Port}";

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
        builder.Logging.ClearProviders();
        var app = builder.Build();

        // Catch every method so the pure service can return the documented JSON 405 response rather
        // than ASP.NET's default HTML/plain-text response for unsupported verbs.
        app.Map("/{**path}", async (HttpContext context) =>
        {
            var force = context.Request.Query.TryGetValue("force", out var forceValue) &&
                        bool.TryParse(forceValue.FirstOrDefault(), out var forceParsed) && forceParsed;
            var response = await service.HandleAsync(context.Request.Method, context.Request.Path + context.Request.QueryString,
                force, context.RequestAborted).ConfigureAwait(false);
            ApplyCors(context, options.EnableCors);
            context.Response.StatusCode = response.StatusCode;
            if (!string.IsNullOrEmpty(response.Body))
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
            }
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
        catch (IOException)
        {
            // The dashboard remains usable if another process owns the port.  This mirrors the
            // upstream behavior: local API is an optional integration, never a launch blocker.
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

    private static void ApplyCors(HttpContext context, bool enabled)
    {
        if (!enabled) return;
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
    }
}
