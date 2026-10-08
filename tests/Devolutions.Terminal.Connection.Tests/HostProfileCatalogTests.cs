using System.Text.Json;
using Devolutions.Terminal.Browser.Host;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class HostProfileCatalogTests
{
    [Fact]
    public void CatalogHidesCommandLinesAndRefusesElevation()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shell = ProfileSettings.CreatePwsh("secret-command-token");
        shell.StartingDirectory = home;
        var elevated = ProfileSettings.CreateCmd();
        elevated.Guid = "{11111111-1111-1111-1111-111111111111}";
        elevated.Name = "Admin Prompt";
        elevated.Elevate = true;
        var wasm = ProfileSettings.CreateBrowserShell();
        var settings = new AppSettings
        {
            Profiles = { shell, elevated, wasm },
            DefaultProfile = elevated.Guid,
        };

        var catalog = HostProfileCatalog.FromSettings(settings);
        var json = JsonSerializer.Serialize(
            new HostControlMessage
            {
                Type = HostControlProtocol.Profiles,
                Profiles = catalog.Profiles.ToList(),
            },
            HostControlJsonContext.Default.HostControlMessage);

        Assert.Equal(shell.Guid, catalog.DefaultProfileId);
        Assert.Contains(catalog.Profiles, profile => profile.Id == shell.Guid && profile.Launchable);
        Assert.Contains(
            catalog.Profiles,
            profile => profile.Id == elevated.Guid &&
                !profile.Launchable &&
                profile.Reason == "Elevation is not available from the browser host.");
        Assert.Contains(catalog.Profiles, profile => profile.Id == wasm.Guid && !profile.Launchable);
        Assert.All(catalog.Profiles, profile =>
        {
            Assert.Null(profile.CommandLine);
            Assert.Null(profile.StartingDirectory);
        });
        Assert.DoesNotContain("secret-command-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(home, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commandLine", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("startingDirectory", json, StringComparison.OrdinalIgnoreCase);

        var launch = catalog.CreateLaunchOptions(
            catalog.Profiles.Single(profile => profile.Id == shell.Guid),
            80,
            24);
        Assert.Equal("secret-command-token", launch.CommandLine);
        Assert.Equal(home, launch.WorkingDirectory);
        Assert.Throws<InvalidOperationException>(() => catalog.CreateLaunchOptions(
            catalog.Profiles.Single(profile => profile.Id == elevated.Guid),
            80,
            24));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pwsh.exe")]
    public void CatalogListsIseAsNonlaunchableAndRefusesLaunchOptions(string commandline)
    {
        var terminal = ProfileSettings.CreatePwsh();
        var ise = ProfileSettings.CreatePowerShellIse();
        ise.Commandline = commandline;
        var settings = new AppSettings { Profiles = [terminal, ise], DefaultProfile = ise.Guid };
        var catalog = HostProfileCatalog.FromSettings(settings);
        var info = catalog.Profiles.Single(p => p.Id == ise.Guid);

        Assert.False(info.Launchable);
        Assert.Equal("This profile requires the desktop PowerShell ISE workbench.", info.Reason);
        Assert.Throws<InvalidOperationException>(() => catalog.CreateLaunchOptions(info, 80, 24));
        Assert.Equal(terminal.Guid, catalog.DefaultProfileId);
        var launch = catalog.CreateLaunchOptions(catalog.Profiles.Single(p => p.Id == terminal.Guid), 80, 24);
        Assert.Equal("pwsh.exe", launch.CommandLine);
        Assert.Equal(80, launch.Columns);
        Assert.Equal(24, launch.Rows);
    }

    [Theory]
    [InlineData(ProfileKind.PowerShellIse)]
    [InlineData(ProfileKind.Unsupported)]
    public void CatalogWithoutLaunchableTerminalHasNoDefault(ProfileKind kind)
    {
        var profile = ProfileSettings.CreatePowerShellIse();
        profile.Kind = kind;
        var catalog = HostProfileCatalog.FromSettings(new AppSettings { Profiles = [profile], DefaultProfile = profile.Guid });

        Assert.Null(catalog.DefaultProfileId);
        var info = Assert.Single(catalog.Profiles);
        Assert.False(info.Launchable);
        Assert.Throws<InvalidOperationException>(() => catalog.CreateLaunchOptions(info, 80, 24));
    }
}
