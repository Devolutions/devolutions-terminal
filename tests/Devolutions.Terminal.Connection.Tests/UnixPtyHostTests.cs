using System.Runtime.InteropServices;
using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class UnixPtyHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dt-pty-host-" + Guid.NewGuid().ToString("N"));

    public UnixPtyHostTests() => Directory.CreateDirectory(_root);

    public static bool IsUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false, Architecture.X64, "linux-x64")]
    [InlineData(false, Architecture.Arm64, "linux-arm64")]
    [InlineData(true, Architecture.X64, "osx-x64")]
    [InlineData(true, Architecture.Arm64, "osx-arm64")]
    public void PortableRuntimeIdentifierMatchesPackageLayout(bool isMacOs, Architecture architecture, string expected) =>
        Assert.Equal(expected, UnixPtyHost.GetPortableRuntimeIdentifier(isMacOs, architecture));

    [Fact]
    public void HostNextToExecutableWinsOverRuntimesLayout()
    {
        var root = CreateHost(_root);
        CreateHost(Path.Combine(_root, "runtimes", "linux-x64", "native"));

        Assert.Equal(root, UnixPtyHost.Find(_root, "linux-x64"));
    }

    [Fact]
    public void RidLessPackageLayoutIsFound()
    {
        CreateHost(Path.Combine(_root, "runtimes", "osx-x64", "native"));
        var expected = CreateHost(Path.Combine(_root, "runtimes", "osx-arm64", "native"));

        Assert.Equal(expected, UnixPtyHost.Find(_root, "osx-arm64"));
    }

    [Fact]
    public void MissingHostIsReportedAtTheExecutableLocation()
    {
        CreateHost(Path.Combine(_root, "runtimes", "linux-arm64", "native"));

        Assert.Null(UnixPtyHost.Find(_root, "linux-x64"));
        Assert.Equal(
            Path.Combine(_root, UnixPtyHost.FileName),
            UnixPtyHost.GetCandidatePaths(_root, "linux-x64")[0]);
    }

    [Theory(Skip = "Unix file modes are Linux/macOS-only.", SkipUnless = nameof(IsUnix))]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)]
    public void RestoredHostWithoutExecuteBitBecomesExecutable(UnixFileMode restored, UnixFileMode expected)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var host = CreateHost(_root);
        File.SetUnixFileMode(host, restored);

        UnixPtyHost.EnsureExecutable(host);

        Assert.Equal(expected, File.GetUnixFileMode(host));
    }

    private static string CreateHost(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, UnixPtyHost.FileName);
        File.WriteAllBytes(path, [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        return path;
    }
}
