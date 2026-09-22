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
}
