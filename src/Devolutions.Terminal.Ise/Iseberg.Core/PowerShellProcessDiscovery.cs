using System.Diagnostics;

namespace Iseberg.Core;

/// <summary>Discovers the subprocess without probing or loading any PowerShell assemblies.</summary>
public static class PowerShellProcessDiscovery
{
    public static Version MinimumVersion => PowerShellCompatibility.MinimumVersion;
    public static bool IsSupportedVersion(Version version) =>
        PowerShellCompatibility.IsSupportedVersion(version);
    public static string ModulePath
    {
        get
        {
            var besideExecutable = Path.Combine(AppContext.BaseDirectory, IseBridgeProtocol.ModuleDirectory, "Iseberg.PowerShell.dll");
            if (File.Exists(besideExecutable) || !OperatingSystem.IsMacOS()) return besideExecutable;
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources",
                IseBridgeProtocol.ModuleDirectory, "Iseberg.PowerShell.dll"));
        }
    }

    public static string FindExecutable()
    {
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        if (Environment.GetEnvironmentVariable("DT_ISEBERG_PSHOME") is { Length: > 0 } home)
        {
            var candidate = Path.Combine(Path.GetFullPath(home), executable);
            if (!File.Exists(candidate))
                throw new FileNotFoundException("DT_ISEBERG_PSHOME must identify an installed PowerShell directory.", candidate);
            return candidate;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException($"Iseberg requires installed PowerShell {PowerShellCompatibility.SupportedVersions}. Install pwsh on PATH or set DT_ISEBERG_PSHOME.");
    }

    public static async Task<Version> GetVersionAsync(string? executable = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable ?? FindExecutable())
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (process.ExitCode != 0 || !Version.TryParse((await output).Trim(), out var version))
                throw new InvalidOperationException("Could not determine installed PowerShell version: " + await error);
            return version;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
