using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iseberg.Core;

/// <summary>What the bridge's pwsh reports about itself before it loads the bridge module.</summary>
public sealed record PowerShellInstallation(string Home, string Version, string RuntimeVersion, string Architecture)
{
    public const string HomeVariable = "DT_ISEBERG_PSHOME";
    /// <summary>Prefix of the single stdout line written by <see cref="ReportScript"/>.</summary>
    public const string ReportPrefix = "DT-ISEBERG-INSTALLATION:";
    public static Version MinimumVersion => PowerShellCompatibility.MinimumVersion;

    /// <summary>
    /// Runs first in the bridge bootstrap, before the net8.0 module is imported, so a pwsh that cannot
    /// load the module still describes itself and fails with an actionable message.
    /// </summary>
    public static string ReportScript { get; } =
        "[Console]::Out.WriteLine('" + ReportPrefix + "' + ([pscustomobject]@{ " +
        "Home = $PSHOME; " +
        "Version = $PSVersionTable.PSVersion.ToString(); " +
        "RuntimeVersion = [Environment]::Version.ToString(); " +
        "Architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() " +
        "} | ConvertTo-Json -Compress))";

    public static string MissingMessage =>
        $"Install PowerShell {PowerShellCompatibility.SupportedVersions} on PATH, or set {HomeVariable} to its installation directory.";

    public static PowerShellInstallation Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, PowerShellInstallationJsonContext.Default.PowerShellInstallation)
                ?? throw new InvalidOperationException("PowerShell returned an empty installation description.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("PowerShell returned an invalid installation description.", exception);
        }
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
