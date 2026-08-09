using System.Diagnostics;

namespace UsageMonitor.Core.Providers.OpenCode;

public sealed record OpenCodeDatabaseDiscovery(
    IReadOnlyList<string> DatabasePaths,
    IReadOnlyList<string> AuthPaths);

public interface IOpenCodeDatabaseLocator
{
    OpenCodeDatabaseDiscovery Discover();
}

/// <summary>
/// Finds OpenCode's active database and the release-channel database candidates that may contain
/// historical sessions. The CLI path is authoritative when it is available; known locations are
/// still scanned because OpenCode has changed database names and locations between releases.
/// </summary>
public sealed class OpenCodeDatabaseLocator : IOpenCodeDatabaseLocator
{
    private readonly Func<string?> _dataDirectoryOverride;
    private readonly Func<string?> _xdgDataHome;
    private readonly Func<string> _userProfile;
    private readonly Func<string?> _localAppData;
    private readonly Func<string?> _activeDatabasePath;
    private readonly bool _includeDefaultLocations;

    public OpenCodeDatabaseLocator(
        Func<string?>? dataDirectoryOverride = null,
        Func<string?>? xdgDataHome = null,
        Func<string>? userProfile = null,
        Func<string?>? localAppData = null,
        Func<string?>? activeDatabasePath = null,
        bool includeDefaultLocations = true)
    {
        _dataDirectoryOverride = dataDirectoryOverride ?? (() => Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR"));
        _xdgDataHome = xdgDataHome ?? (() => Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _localAppData = localAppData ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _activeDatabasePath = activeDatabasePath ?? FindActiveDatabasePath;
        _includeDefaultLocations = includeDefaultLocations;
    }

    public OpenCodeDatabaseDiscovery Discover()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitDirectory = Trim(_dataDirectoryOverride());
        AddRootOrDatabase(roots, explicitDirectory);

        if (_includeDefaultLocations)
        {
            var activeDatabase = Trim(_activeDatabasePath());
            AddRootOrDatabase(roots, activeDatabase);
            if (activeDatabase is not null && File.Exists(activeDatabase))
                AddRootOrDatabase(roots, Path.GetDirectoryName(activeDatabase));

            var xdg = Trim(_xdgDataHome());
            if (xdg is not null)
                AddRootOrDatabase(roots, Path.Combine(Environment.ExpandEnvironmentVariables(xdg), "opencode"));

            var profile = Trim(_userProfile());
            if (profile is not null)
                AddRootOrDatabase(roots, Path.Combine(profile, ".local", "share", "opencode"));

            var localAppData = Trim(_localAppData());
            if (localAppData is not null)
                AddRootOrDatabase(roots, Path.Combine(Environment.ExpandEnvironmentVariables(localAppData), "opencode"));
        }

        var databases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var authPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            authPaths.Add(Path.Combine(root, "auth.json"));
            if (File.Exists(root) && IsDatabasePath(root))
            {
                databases.Add(root);
                continue;
            }

            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "opencode*.db", SearchOption.TopDirectoryOnly))
                    databases.Add(Canonical(path));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new OpenCodeDatabaseDiscovery(
            databases.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            authPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void AddRootOrDatabase(ISet<string> roots, string? value)
    {
        if (value is null) return;
        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (File.Exists(expanded))
        {
            roots.Add(Canonical(expanded));
            return;
        }

        roots.Add(Canonical(expanded));
    }

    private static bool IsDatabasePath(string path) =>
        path.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string path) => Path.GetFullPath(path);

    private static string? FindActiveDatabasePath()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveOpenCodeExecutable(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("db");
            process.StartInfo.ArgumentList.Add("path");
            if (!process.Start()) return null;
            if (!process.WaitForExit(2_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0) return null;
            return process.StandardOutput.ReadToEnd()
                .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
                .Select(Trim)
                .FirstOrDefault(value => value is not null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string ResolveOpenCodeExecutable()
    {
        var path = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "opencode.cmd", "opencode.exe", "opencode.bat", "opencode" }
            : new[] { "opencode" };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var candidate in candidates)
        {
            var executable = Path.Combine(directory, candidate);
            if (File.Exists(executable)) return executable;
        }

        return "opencode";
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
