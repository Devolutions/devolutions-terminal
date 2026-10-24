using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Devolutions.Terminal.Connection;

internal static class UnixPtyHost
{
    public const string FileName = "dt-pty-host";

    private const UnixFileMode ReadBits =
        UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    // App and RID-specific publishes place the host next to the executable.
    // RID-less builds that consume the Devolutions.Terminal.Control package
    // keep NuGet's runtimes/<rid>/native layout instead.
    public static IReadOnlyList<string> GetCandidatePaths(string baseDirectory, string runtimeIdentifier) =>
    [
        Path.Combine(baseDirectory, FileName),
        Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", FileName),
    ];

    public static string GetPortableRuntimeIdentifier(bool isMacOs, Architecture architecture) =>
        $"{(isMacOs ? "osx" : "linux")}-{architecture.ToString().ToLowerInvariant()}";

    public static string? Find(string baseDirectory, string runtimeIdentifier)
    {
        foreach (var candidate in GetCandidatePaths(baseDirectory, runtimeIdentifier))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // NuGet does not preserve Unix file modes, so a host restored from the
    // package arrives without execute permission.
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static void EnsureExecutable(string path)
    {
        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserExecute) != 0)
        {
            return;
        }

        var executable = mode | UnixFileMode.UserExecute | (UnixFileMode)((int)(mode & ReadBits) >> 2);
        try
        {
            File.SetUnixFileMode(path, executable);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"The Unix PTY host '{path}' is not executable and its mode could not be changed. " +
                "Run 'chmod +x' on it.",
                exception);
        }
    }
}
