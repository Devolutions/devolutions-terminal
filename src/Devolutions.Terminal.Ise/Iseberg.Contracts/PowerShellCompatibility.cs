namespace Iseberg.Core;

public static class PowerShellCompatibility
{
    public static Version MinimumVersion { get; } = new(7, 4, 6);
    public const string SupportedVersions = "7.4.6+ within 7.4.x, 7.5.x or 7.6.x";

    public static bool IsSupportedVersion(Version version) =>
        version.Major == 7 && version.Minor is >= 4 and <= 6 && version >= MinimumVersion;

    public static int RuntimeMajor(Version version) => version.Minor + 4;

    public static bool IsSupportedRuntime(Version version, Version runtime) =>
        IsSupportedVersion(version) && runtime.Major == RuntimeMajor(version);
}
