using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Settings.Tests;

public sealed class LinuxRuntimeEnvironmentTests
{
    [Fact]
    public void SettingsPathsUseLinuxXdgDirectories()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var stateRoot = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        configRoot = string.IsNullOrWhiteSpace(configRoot)
            ? Path.Combine(home, ".config")
            : configRoot;
        stateRoot = string.IsNullOrWhiteSpace(stateRoot)
            ? Path.Combine(home, ".local", "state")
            : stateRoot;

        Assert.Equal(
            Path.Combine(configRoot, "devolutions-terminal"),
            SettingsService.SettingsDirectory);
        Assert.Equal(
            Path.Combine(stateRoot, "devolutions-terminal"),
            SettingsService.StateDirectory);
    }

    [Fact]
    public void SettingsPathsUseMacOsApplicationSupport()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(
            Path.Combine(root, "Devolutions", "Terminal"),
            SettingsService.SettingsDirectory);
        Assert.Equal(SettingsService.SettingsDirectory, SettingsService.StateDirectory);
    }

    [Fact]
    public async Task NativeLinuxProfileDiscoveryFindsExecutableShells()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await DynamicProfileManager.CreateDefault().GenerateAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(result.Profiles, profile => Assert.Equal(DynamicProfileSource.Linux, profile.Source));
        var shells = result.Profiles.Where(profile => profile.Kind == ProfileKind.Terminal).ToArray();
        Assert.NotEmpty(shells);
        Assert.All(shells, profile =>
        {
            Assert.Equal(DynamicProfileSource.Linux, profile.Source);
            Assert.True(File.Exists(profile.Commandline), profile.Commandline);
        });
    }

    [Fact]
    public async Task NativeMacOsProfileDiscoveryFindsExecutableShells()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var result = await DynamicProfileManager.CreateDefault().GenerateAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(result.Profiles, profile => Assert.Equal(DynamicProfileSource.MacOS, profile.Source));
        var shells = result.Profiles.Where(profile => profile.Kind == ProfileKind.Terminal).ToArray();
        Assert.NotEmpty(shells);
        Assert.Contains(result.Profiles, profile => profile.Name == "Zsh");
        Assert.All(shells, profile =>
        {
            Assert.Equal(DynamicProfileSource.MacOS, profile.Source);
            Assert.EndsWith(" -l", profile.Commandline, StringComparison.Ordinal);
            var executable = profile.Commandline[..profile.Commandline.LastIndexOf(' ')];
            Assert.True(File.Exists(executable), profile.Commandline);
        });
    }
}
