using System.Diagnostics;
using System.IO;

namespace UsageMonitor.Desktop;

/// <summary>
/// Resolves the Antigravity CLI launcher on native Windows. The CLI ships as <c>agy</c> through
/// npm (an <c>agy.cmd</c> shim) or as a standalone binary; the shim is launched through cmd so the
/// user can complete the browser sign-in flow in a normal console window.
/// </summary>
public static class AntigravityLoginCommand
{
    private static readonly string[] CommandNames = ["agy.exe", "agy.cmd", "agy.bat", "agy.ps1"];

    public static ProcessStartInfo CreateStartInfo()
    {
        var command = ResolveCommandPath();
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ProcessStartInfo
            {
                FileName = "agy",
                UseShellExecute = true,
                WorkingDirectory = UserDirectory()
            };
        }

        var extension = Path.GetExtension(command);
        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var shell = ResolvePowerShell();
            var info = new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = true,
                WorkingDirectory = UserDirectory(),
                WindowStyle = ProcessWindowStyle.Normal
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-File");
            info.ArgumentList.Add(command);
            return info;
        }

        return new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = true,
            WorkingDirectory = UserDirectory(),
            WindowStyle = ProcessWindowStyle.Normal
        };
    }

    public static string? ResolveCommandPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var root = directory.Trim().Trim('"');
            if (root.Length == 0) continue;
            foreach (var name in CommandNames)
            {
                try
                {
                    var candidate = Path.Combine(root, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
        }

        return null;
    }

    private static string ResolvePowerShell()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var root = directory.Trim().Trim('"');
                foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
                {
                    try
                    {
                        var candidate = Path.Combine(root, name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException) { }
                    catch (NotSupportedException) { }
                }
            }
        }

        var systemPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(systemPowerShell) ? systemPowerShell : "powershell.exe";
    }

    private static string UserDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
