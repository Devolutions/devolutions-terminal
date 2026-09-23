using Devolutions.Terminal.Connection;
using System.Runtime.Versioning;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsElevationTests
{
    [Fact]
    public void FindsGsudoOnPathWithoutUsingRelativeDirectories()
    {
        var missing = Path.Combine(Path.GetTempPath(), "missing gsudo");
        var installed = Path.Combine(Path.GetTempPath(), "gsudo installed");
        var executable = Path.Combine(installed, "gsudo.exe");
        var path = string.Join(Path.PathSeparator, "relative", missing, installed);

        var resolved = WindowsElevation.FindGsudo(path, candidate => candidate == executable);

        Assert.Equal(executable, resolved);
    }

    [Fact]
    public void DoesNotFallBackToAnUnelevatedLaunchWhenGsudoIsMissing()
    {
        var path = Path.GetTempPath();

        var resolved = WindowsElevation.FindGsudo(path, _ => false);

        Assert.Null(resolved);
    }

    [Fact]
    public void PreservesProfileCommandLineAndQuotesGsudoPath()
    {
        const string commandLine = """
            "C:\Program Files\PowerShell\7\pwsh.exe" -NoExit -Command "Write-Host 'Hello World'"
            """;

        var wrapped = WindowsElevation.BuildCommandLine(
            commandLine,
            @"C:\Program Files\gsudo\Current\gsudo.exe");

        Assert.Equal(
            """
            "C:\Program Files\gsudo\Current\gsudo.exe" --inline --direct run "C:\Program Files\PowerShell\7\pwsh.exe" -NoExit -Command "Write-Host 'Hello World'"
            """,
            wrapped);
    }
}
