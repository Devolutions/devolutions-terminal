using Devolutions.Terminal.App.Connections;
using Iseberg.Core;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class PortableIseInstallationTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("dt-ise-installation-").FullName;

    public PortableIseInstallationTests()
    {
        File.WriteAllBytes(Path.Combine(home, "System.Management.Automation.dll"), []);
        File.WriteAllBytes(Path.Combine(home, "pwsh.dll"), []);
    }

    [Theory]
    [InlineData("7.4.6", "8.0.6", "X64")]
    [InlineData("7.4.20", "8.0.19", "Arm64")]
    [InlineData("7.5.0", "9.0.0", "X64")]
    [InlineData("7.5.9", "9.0.10", "Arm64")]
    [InlineData("7.6.0", "10.0.0", "X64")]
    [InlineData("7.6.6", "10.0.12", "X64")]
    [InlineData("7.6.7", "10.0.12", "Arm64")]
    public void SupportedChildDoesNotRequireMatchingParentArchitecture(string version, string runtime, string architecture)
    {
        new PowerShellInstallation(home, version, runtime, architecture).Validate();
        Assert.True(PowerShellProcessDiscovery.IsSupportedVersion(Version.Parse(version)));
        Assert.Equal(0, new FileInfo(Path.Combine(home, "System.Management.Automation.dll")).Length);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name == "System.Management.Automation");
    }

    [Theory]
    [InlineData("5.1.0")]
    [InlineData("7.3.13")]
    [InlineData("7.4.0")]
    [InlineData("7.4.5")]
    [InlineData("7.4.0-preview.1")]
    [InlineData("7.7.0")]
    [InlineData("8.0.0")]
    [InlineData("invalid")]
    public void UnsupportedPowerShellVersionReportsActualVersion(string version)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new PowerShellInstallation(home, version, "10.0.6", "X64").Validate());
        Assert.Contains("7.4.6", error.Message);
        Assert.Contains($"found {version}", error.Message);
    }

    [Theory]
    [InlineData("7.4.20", "7.0.0", 8)]
    [InlineData("7.4.20", "9.0.0", 8)]
    [InlineData("7.5.9", "8.0.19", 9)]
    [InlineData("7.5.9", "10.0.12", 9)]
    [InlineData("7.6.6", "9.0.9", 10)]
    [InlineData("7.6.6", "11.0.0", 10)]
    [InlineData("7.4.20", "invalid", 8)]
    public void IncompatibleChildRuntimeReportsRequiredAndActualRuntime(string version, string runtime, int expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new PowerShellInstallation(home, version, runtime, "X64").Validate());
        Assert.Contains($".NET {expected}", error.Message);
        Assert.Contains($"found {runtime}", error.Message);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("99")]
    public void InvalidChildArchitectureIsRejected(string architecture)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new PowerShellInstallation(home, "7.6.6", "10.0.6", architecture).Validate());
        Assert.Contains(architecture, error.Message);
    }

    [Theory]
    [InlineData("System.Management.Automation.dll")]
    [InlineData("pwsh.dll")]
    public void IncompleteChildInstallationReportsItsDirectory(string missingFile)
    {
        File.Delete(Path.Combine(home, missingFile));
        var error = Assert.Throws<InvalidOperationException>(() =>
            new PowerShellInstallation(home, "7.6.6", "10.0.6", "X64").Validate());
        Assert.Contains("incomplete", error.Message);
        Assert.Contains(home, error.Message);
    }

    [Fact]
    public void RelativeInstallationHomeIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new PowerShellInstallation("relative", "7.6.6", "10.0.6", "X64").Validate());
        Assert.Contains("incomplete", error.Message);
    }

    public void Dispose()
    {
        File.Delete(Path.Combine(home, "System.Management.Automation.dll"));
        File.Delete(Path.Combine(home, "pwsh.dll"));
        Directory.Delete(home);
    }
}
