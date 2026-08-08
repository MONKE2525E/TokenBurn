using System.Diagnostics;
using System.IO;

namespace UsageMonitor.Desktop;

/// <summary>
/// Resolves the Claude Code launcher on native Windows. npm installs commonly expose a
/// <c>claude.ps1</c> shim rather than a directly executable <c>claude.exe</c>; starting the bare
/// command from WPF is not reliable in that case, so the shim is launched through PowerShell with
/// argument boundaries preserved by <see cref="ProcessStartInfo.ArgumentList"/>.
/// </summary>
public static class ClaudeLoginCommand
{
    private static readonly string[] CommandNames = ["claude.exe", "claude.cmd", "claude.bat", "claude.ps1"];

    public static ProcessStartInfo CreateStartInfo()
    {
        var command = ResolveCommandPath();
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "auth login",
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
            info.ArgumentList.Add("auth");
            info.ArgumentList.Add("login");
            return info;
        }

        var direct = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = true,
            WorkingDirectory = UserDirectory(),
            WindowStyle = ProcessWindowStyle.Normal
        };
        direct.ArgumentList.Add("auth");
        direct.ArgumentList.Add("login");
        return direct;
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
