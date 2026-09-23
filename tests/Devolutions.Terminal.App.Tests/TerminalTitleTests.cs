using Devolutions.Terminal.App.Views;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class TerminalTitleTests
{
    [Theory]
    [InlineData(@"Administrator: C:\Program Files\gsudo\Current\gsudo.exe")]
    [InlineData(@"Administrator: C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"Administrator: ""C:\Program Files\gsudo\Current\gsudo.exe"" --inline --direct run pwsh.exe")]
    [InlineData("Administrator: gsudo.exe")]
    public void ElevatedLauncherTitlesShowProfileInstead(string title)
    {
        var profile = new ProfileSettings
        {
            Name = "PowerShell",
            Commandline = @"""C:\Program Files\PowerShell\7\pwsh.exe""",
            Elevate = true,
        };

        Assert.Equal("PowerShell", MainWindow.ResolveTerminalTitle(profile, title));
    }

    [Fact]
    public void CustomApplicationTitlesRemainVisibleInElevatedTabs()
    {
        var profile = new ProfileSettings { Name = "PowerShell", Elevate = true };

        Assert.Equal(
            "Administrator: Build complete",
            MainWindow.ResolveTerminalTitle(profile, "Administrator: Build complete"));
        Assert.Equal("Editor", MainWindow.ResolveTerminalTitle(profile, "Editor"));
        Assert.Equal(
            "Administrator: notgsudo.exe",
            MainWindow.ResolveTerminalTitle(profile, "Administrator: notgsudo.exe"));
        profile.TabTitle = "My shell";
        Assert.Equal(
            "My shell",
            MainWindow.ResolveTerminalTitle(profile, @"Administrator: C:\Program Files\gsudo\Current\gsudo.exe"));
    }

    [Fact]
    public void ResolvedPowerShellPathDoesNotReplaceTheProfileName()
    {
        var profile = ProfileSettings.CreatePwsh();
        profile.Elevate = true;

        Assert.Equal(
            "PowerShell",
            MainWindow.ResolveTerminalTitle(
                profile, @"Administrator: C:\Program Files\PowerShell\7\pwsh.exe"));
    }

    [Fact]
    public void UnelevatedTitlesAreNotTreatedAsGsudoLaunches()
    {
        var profile = new ProfileSettings { Name = "PowerShell" };

        Assert.Equal(
            @"Administrator: C:\Program Files\gsudo\Current\gsudo.exe",
            MainWindow.ResolveTerminalTitle(
                profile, @"Administrator: C:\Program Files\gsudo\Current\gsudo.exe"));
    }
}
