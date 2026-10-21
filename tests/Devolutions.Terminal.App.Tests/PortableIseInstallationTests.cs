using System.Diagnostics;
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

    [Fact]
    public async Task ReportScriptOutputFromInstalledPowerShellParsesAndValidates()
    {
        var info = new ProcessStartInfo(PowerShellProcessDiscovery.FindExecutable())
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", PowerShellInstallation.ReportScript })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var report = Assert.Single(output.Split('\n', StringSplitOptions.TrimEntries),
            line => line.StartsWith(PowerShellInstallation.ReportPrefix, StringComparison.Ordinal));
        var installation = PowerShellInstallation.Parse(report[PowerShellInstallation.ReportPrefix.Length..]);
        installation.Validate();
        Assert.True(PowerShellProcessDiscovery.IsSupportedVersion(Version.Parse(installation.Version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("null")]
    public void MalformedReportIsRejected(string report)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PowerShellInstallation.Parse(report));
        Assert.Contains("installation description", error.Message);
    }

    [Fact]
    public async Task SessionRejectsUnsupportedPowerShellFromItsOwnReport()
    {
        // A pwsh too old to load the net8.0 bridge module must still fail with the version it found,
        // not after the bridge pipe times out.
        var report = PowerShellInstallation.ReportPrefix + "{\"Home\":\"" + home.Replace("\\", "\\\\", StringComparison.Ordinal) +
            "\",\"Version\":\"7.3.0\",\"RuntimeVersion\":\"7.0.0\",\"Architecture\":\"X64\"}";
        await using var session = new PowerShellSession { ExecutablePath = FakePowerShell("old-pwsh", stdout: report) };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal($"Iseberg requires PowerShell {PowerShellCompatibility.SupportedVersions}; found 7.3.0.", error.Message);
        Assert.Equal(SessionState.Failed, session.State);
    }

    [Fact]
    public async Task SessionReportsPowerShellThatExitsWithoutDescribingItself()
    {
        await using var session = new PowerShellSession { ExecutablePath = FakePowerShell("broken-pwsh", stdout: null) };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.StartsWith("PowerShell installation check failed:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReportsMissingPowerShellWithInstallGuidance()
    {
        await using var session = new PowerShellSession
        {
            ExecutablePath = Path.Combine(home, OperatingSystem.IsWindows() ? "missing-pwsh.exe" : "missing-pwsh")
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(PowerShellInstallation.MissingMessage, error.Message);
        Assert.Contains(PowerShellInstallation.HomeVariable, error.Message);
    }

    // A stand-in for pwsh that ignores its arguments: it prints one stdout line (or nothing) and,
    // when it printed one, stays alive like a bridge still loading its module.
    private string FakePowerShell(string name, string? stdout)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(home, name + ".cmd");
            File.WriteAllText(script, stdout is null
                ? "@echo off\r\necho fake pwsh failed before reporting 1>&2\r\nexit /b 3\r\n"
                : $"@echo off\r\necho {stdout}\r\nping -n 30 127.0.0.1 >nul\r\n");
            return script;
        }
        var shell = Path.Combine(home, name);
        File.WriteAllText(shell, stdout is null
            ? "#!/bin/sh\necho 'fake pwsh failed before reporting' >&2\nexit 3\n"
            : $"#!/bin/sh\nprintf '%s\\n' '{stdout}'\nsleep 30\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shell;
    }

    public void Dispose()
    {
        Directory.Delete(home, recursive: true);
    }
}
