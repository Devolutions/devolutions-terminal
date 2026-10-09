using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iseberg.Core;

namespace Devolutions.Terminal.App.Connections;

public sealed record PowerShellInstallation(string Home, string Version, string RuntimeVersion, string Architecture)
{
    public const string HomeVariable = "DT_ISEBERG_PSHOME";
    public static Version MinimumVersion => PowerShellCompatibility.MinimumVersion;

    public static PowerShellInstallation Find()
    {
        var home = Environment.GetEnvironmentVariable(HomeVariable);
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? executable : Path.Combine(home, executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            """
            [pscustomobject]@{
                Home = $PSHOME
                Version = $PSVersionTable.PSVersion.ToString()
                RuntimeVersion = [Environment]::Version.ToString()
                Architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            } | ConvertTo-Json -Compress
            """
        })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Install PowerShell {PowerShellCompatibility.SupportedVersions} on PATH, or set {HomeVariable} to its installation directory.", exception);
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("PowerShell did not respond to the installation check within 15 seconds.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell installation check failed: {error.GetAwaiter().GetResult().Trim()}");
        PowerShellInstallation installation;
        try
        {
            installation = JsonSerializer.Deserialize(output.GetAwaiter().GetResult(),
                PowerShellInstallationJsonContext.Default.PowerShellInstallation)
                ?? throw new InvalidOperationException("PowerShell returned an empty installation description.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("PowerShell returned an invalid installation description.", exception);
        }
        installation.Validate();
        return installation;
    }

    public void Validate()
    {
        if (!System.Version.TryParse(Version, out var version) ||
            !PowerShellCompatibility.IsSupportedVersion(version))
            throw new InvalidOperationException($"Iseberg requires PowerShell {PowerShellCompatibility.SupportedVersions}; found {Version}.");
        if (!System.Version.TryParse(RuntimeVersion, out var runtime) ||
            !PowerShellCompatibility.IsSupportedRuntime(version, runtime))
            throw new InvalidOperationException($"PowerShell {version.Major}.{version.Minor} requires its bundled .NET {PowerShellCompatibility.RuntimeMajor(version)} runtime; found {RuntimeVersion}.");
        if (!Enum.TryParse<System.Runtime.InteropServices.Architecture>(Architecture, out var architecture) ||
            !Enum.IsDefined(architecture))
            throw new InvalidOperationException($"PowerShell returned an unknown process architecture: {Architecture}.");
        if (string.IsNullOrWhiteSpace(Home) || !Path.IsPathFullyQualified(Home) ||
            !File.Exists(Path.Combine(Home, "System.Management.Automation.dll")) ||
            !File.Exists(Path.Combine(Home, "pwsh.dll")))
            throw new InvalidOperationException($"PowerShell installation is incomplete: {Home}.");
    }

}

[JsonSerializable(typeof(PowerShellInstallation))]
internal sealed partial class PowerShellInstallationJsonContext : JsonSerializerContext;
