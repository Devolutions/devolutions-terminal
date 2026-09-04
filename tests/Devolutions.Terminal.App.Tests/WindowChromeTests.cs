using Avalonia;
using Devolutions.Terminal.App.Platform;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class WindowChromeTests
{
    [Fact]
    public void HidesSingleTabWhenAlwaysShowTabsIsFalse()
    {
        var settings = new AppSettings { AlwaysShowTabs = false };
        Assert.False(WindowChrome.ShouldShowTabRow(settings, tabCount: 1, fullscreen: false));
        Assert.True(WindowChrome.ShouldShowTabRow(settings, tabCount: 2, fullscreen: false));
    }

    [Fact]
    public void AlwaysShowTabsKeepsSingleTabVisible()
    {
        var settings = new AppSettings { AlwaysShowTabs = true };
        Assert.True(WindowChrome.ShouldShowTabRow(settings, tabCount: 1, fullscreen: false));
    }

    [Fact]
    public void HidesTabsInFullscreenUnlessEnabled()
    {
        var settings = new AppSettings { AlwaysShowTabs = true, ShowTabsFullscreen = false };
        Assert.False(WindowChrome.ShouldShowTabRow(settings, tabCount: 2, fullscreen: true));
        settings.ShowTabsFullscreen = true;
        Assert.True(WindowChrome.ShouldShowTabRow(settings, tabCount: 2, fullscreen: true));
    }

    [Fact]
    public void EmbeddedWindowsDoNotUseCustomTitlebar()
    {
        var settings = new AppSettings { ShowTabsInTitlebar = true };
        Assert.False(WindowChrome.ShouldUseCustomTitlebar(settings, embedded: true));
        Assert.True(WindowChrome.ShouldUseCustomTitlebar(settings, embedded: false));
        settings.ShowTabsInTitlebar = false;
        Assert.False(WindowChrome.ShouldUseCustomTitlebar(settings, embedded: false));
        settings.ShowTabsInTitlebar = true;
        Assert.True(WindowChrome.ShouldUseCustomTitlebar(settings, embedded: false, macOS: true));
    }

    [Fact]
    public void MacOsOverlayTitleBarUsesSnugTrafficLightInset()
    {
        var margin = WindowChrome.TitleBarContentMargin(
            fullscreen: false,
            macOS: true,
            windows: false,
            offScreenMargin: default);

        Assert.Equal(WindowChrome.MacOsTrafficLightFallback, margin.Left);
        Assert.Equal(8, margin.Right);
    }

    [Fact]
    public void MacOsKeepsOriginalLayoutWhenWindowControlsAreOnTheRight()
    {
        var margin = WindowChrome.TitleBarContentMargin(
            fullscreen: false,
            macOS: true,
            windows: false,
            offScreenMargin: new Thickness(0, 0, 78, 0),
            rightToLeft: false);

        Assert.True(WindowChrome.MacOsWindowControlsOnRight(new Thickness(0, 0, 78, 0), rightToLeft: false));
        Assert.Equal(8, margin.Left);
        Assert.Equal(WindowChrome.WindowsCaptionFallback, margin.Right);
    }

    [Fact]
    public void MacOsRtlLayoutUsesOriginalCaptionReserve()
    {
        Assert.True(WindowChrome.MacOsWindowControlsOnRight(default, rightToLeft: true));
        var margin = WindowChrome.TitleBarContentMargin(
            fullscreen: false,
            macOS: true,
            windows: false,
            offScreenMargin: default,
            rightToLeft: true);

        Assert.Equal(8, margin.Left);
        Assert.Equal(WindowChrome.WindowsCaptionFallback, margin.Right);
    }

    [Fact]
    public void WindowsTitleBarLeavesRoomForCaptionButtons()
    {
        var margin = WindowChrome.TitleBarContentMargin(
            fullscreen: false,
            macOS: false,
            windows: true,
            offScreenMargin: default);

        Assert.Equal(8, margin.Left);
        Assert.Equal(WindowChrome.WindowsCaptionFallback, margin.Right);
    }

    [Fact]
    public void FullscreenTitleBarDropsCaptionInsets()
    {
        var margin = WindowChrome.TitleBarContentMargin(
            fullscreen: true,
            macOS: true,
            windows: true,
            offScreenMargin: new Thickness(78, 0, 138, 0));

        Assert.Equal(new Thickness(8, 0, 8, 0), margin);
    }

    [Fact]
    public void MacOsTabStripReserveIsInsetPlusTrailingControls()
    {
        Assert.Equal(
            WindowChrome.MacOsTrafficLightFallback + 72,
            WindowChrome.TabStripTrailingReserve(macOS: true, windows: false));
        Assert.Equal(
            253,
            WindowChrome.TabStripTrailingReserve(macOS: true, windows: false, macOsControlsOnRight: true));
        Assert.Equal(253, WindowChrome.TabStripTrailingReserve(macOS: false, windows: true));
    }
}
