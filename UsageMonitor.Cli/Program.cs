using UsageMonitor.Core;
using UsageMonitor.Core.Providers.Claude;
using UsageMonitor.Core.Providers.Codex;
using UsageMonitor.Core.Providers.Antigravity;
using UsageMonitor.Core.Providers.OpenCode;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => CliApplication.RunAsync(args);
}

public static class CliApplication
{
    public const int Success = 0;
    public const int InvalidArguments = 2;
    public const int RefreshFailed = 4;

    public static async Task<int> RunAsync(string[] args, TextWriter? stdout = null, TextWriter? stderr = null,
        IUsageSnapshotSource? source = null, CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        var parsed = Parse(args);
        if (!parsed.IsValid)
        {
            await stderr.WriteLineAsync(parsed.Error).ConfigureAwait(false);
            await stderr.WriteLineAsync("Usage: usage-monitor [provider] [--force] [--diagnose]").ConfigureAwait(false);
            return InvalidArguments;
        }

        if (parsed.ShowHelp)
        {
            await stdout.WriteLineAsync("Usage Monitor CLI").ConfigureAwait(false);
            await stdout.WriteLineAsync("usage-monitor [provider] [--force] [--diagnose]").ConfigureAwait(false);
            await stdout.WriteLineAsync("Prints the local usage limits envelope as JSON; it never launches the desktop app.").ConfigureAwait(false);
            return Success;
        }

        source ??= CreateDefaultSource();
        if (parsed.Diagnose)
        {
            await WriteDiagnosticsAsync(stdout, source).ConfigureAwait(false);
            return Success;
        }

        if (parsed.ProviderId is not null && !source.KnownProviderIds.Contains(parsed.ProviderId))
        {
            await stderr.WriteLineAsync($"Unknown provider: {parsed.ProviderId}").ConfigureAwait(false);
            return InvalidArguments;
        }

        try
        {
            var snapshots = await source.GetSnapshotsAsync(parsed.ProviderId, parsed.Force, cancellationToken)
                .ConfigureAwait(false);
            var service = new UsageApiService(source);
            await stdout.WriteLineAsync(service.SerializeLimits(snapshots)).ConfigureAwait(false);
            return snapshots.Any(snapshot => !string.IsNullOrWhiteSpace(snapshot.Error)) ? RefreshFailed : Success;
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("Usage refresh timed out.").ConfigureAwait(false);
            return RefreshFailed;
        }
        catch (Exception)
        {
            // Do not print exception messages. Provider exceptions can contain account identifiers or
            // paths; diagnostics logs are the only place where a redacted detail may be retained.
            await stderr.WriteLineAsync("Usage refresh failed. Check the desktop diagnostics log.").ConfigureAwait(false);
            return RefreshFailed;
        }
    }

    private static IUsageSnapshotSource CreateDefaultSource()
    {
        var catalog = ProviderCatalog.CreateDefault([new CodexProvider(), new ClaudeProvider(), new AntigravityProvider(), new OpenCodeProvider()]);
        var cache = new JsonFileUsageCache();
        return new CoreUsageSnapshotSource(catalog, cache);
    }

    private static async Task WriteDiagnosticsAsync(TextWriter output, IUsageSnapshotSource source)
    {
        var diagnostics = new
        {
            product = "Usage Monitor",
            platform = OperatingSystem.IsWindows() ? "windows" : Environment.OSVersion.Platform.ToString(),
            api = "127.0.0.1:6736",
            cache = "<local-app-data>/UsageMonitor/Cache",
            settings = "<app-data>/UsageMonitor/settings.json",
            credentialStore = OperatingSystem.IsWindows() ? "Windows Credential Manager + DPAPI" : "unavailable",
            telemetry = false,
            providers = source.KnownProviderIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(diagnostics,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))).ConfigureAwait(false);
    }

    private static ParsedArguments Parse(string[] args)
    {
        string? provider = null;
        var force = false;
        var diagnose = false;
        var help = false;
        foreach (var arg in args ?? [])
        {
            if (string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase)) { force = true; continue; }
            if (string.Equals(arg, "--diagnose", StringComparison.OrdinalIgnoreCase)) { diagnose = true; continue; }
            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) || arg == "-h") { help = true; continue; }
            if (arg.StartsWith("-", StringComparison.Ordinal)) return ParsedArguments.Invalid($"Unknown option: {arg}");
            if (provider is not null) return ParsedArguments.Invalid("Only one provider may be specified.");
            provider = arg;
        }
        if (diagnose && (provider is not null || force)) return ParsedArguments.Invalid("--diagnose cannot be combined with a provider or --force.");
        return new ParsedArguments(true, null, provider, force, diagnose, help);
    }

    private sealed record ParsedArguments(bool IsValid, string? Error, string? ProviderId, bool Force, bool Diagnose, bool ShowHelp)
    {
        public static ParsedArguments Invalid(string error) => new(false, error, null, false, false, false);
    }
}
