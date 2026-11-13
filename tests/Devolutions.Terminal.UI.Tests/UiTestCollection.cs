using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Devolutions.Terminal.App.Models;
using Devolutions.Terminal.App.Panes;
using Devolutions.Terminal.App.Views;
using Devolutions.Terminal.Settings;
using System.Text;
using Xunit;

namespace Devolutions.Terminal.UI.Tests;

public sealed class UiTestCollection
{
    [AvaloniaFact]
    public void ProgramStatusIndicatorSelectsBlockedPaneAndSanitizesText()
    {
        var firstControl = new TermControl();
        var secondControl = new TermControl();
        var first = new TerminalPane(1, new TerminalSessionDescriptor(), new ProfileSettings { Name = "Build" }, firstControl);
        var second = new TerminalPane(2, new TerminalSessionDescriptor(), new ProfileSettings { Name = "Deploy" }, secondControl);
        var tab = new TerminalTab(first);
        tab.Panes.SplitActive(second, PaneSplitOrientation.Vertical);
        firstControl.Engine.Feed("\u001b]7501;state=working\u0007");
        secondControl.Engine.Feed("\u001b]7501;state=working:app=deploy\u0007");
        var message = Convert.ToBase64String(Encoding.UTF8.GetBytes("Approve\u202e now"));
        secondControl.Engine.Feed($"\u001b]7501;state=blocked:id=prod:kind=permission:progress=40:msg={message}\u0007");
        first.Presentation.ProgramStatus = firstControl.Engine.ProgramStatus;
        second.Presentation.ProgramStatus = secondControl.Engine.ProgramStatus;

        var indicator = Assert.IsType<TextBlock>(MainWindow.CreateProgramStatusIndicator(tab));
        Assert.Equal("!", indicator.Text);
        var tip = Assert.IsType<string>(ToolTip.GetTip(indicator));
        Assert.Contains("Deploy: Blocked [prod] (deploy) (Permission) 40%: Approve now", tip);
        Assert.DoesNotContain("\u202e", tip, StringComparison.Ordinal);
        Assert.Equal(tip, AutomationProperties.GetName(indicator));
    }

    [AvaloniaFact]
    public void MainWindowConstructsFromCompiledXaml()
    {
        var window = new MainWindow();

        Assert.Empty(window.Title ?? string.Empty);
        Assert.True(window.Width >= 640);
        Assert.True(window.Height >= 400);
    }

    [AvaloniaFact]
    public void MainWindowProvidesNamedInteractiveChrome()
    {
        var window = new MainWindow();
        var menu = window.FindControl<Avalonia.Controls.Button>("MenuButton");
        var exitFullscreen = window.FindControl<Avalonia.Controls.Button>("ExitFullscreenButton");
        var about = window.FindControl<Avalonia.Controls.Border>("AboutOverlay");

        Assert.NotNull(menu);
        Assert.Equal("New tab menu", AutomationProperties.GetName(menu));
        Assert.NotNull(exitFullscreen);
        Assert.Equal("Exit full screen", AutomationProperties.GetName(exitFullscreen));
        Assert.NotNull(about);
        Assert.Equal(
            AutomationControlType.Window,
            AutomationProperties.GetControlTypeOverride(about));
    }
}
