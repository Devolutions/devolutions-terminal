using Avalonia;
using Devolutions.Terminal.App.Platform;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class WindowPlacementTests
{
    // A 1920x1080 laptop panel with a 48-pixel taskbar, and a 2560x1440 external
    // display to its right.
    private static readonly PixelRect Laptop = new(0, 0, 1920, 1032);
    private static readonly PixelRect External = new(1920, 0, 2560, 1392);

    [Fact]
    public void LeavesWindowInsideOneDisplayAlone()
    {
        var bounds = new PixelRect(100, 80, 1200, 800);
        Assert.Equal(bounds, WindowPlacement.Fit(bounds, [Laptop, External]));
    }

    [Fact]
    public void LeavesWindowSpanningConnectedDisplaysAlone()
    {
        var bounds = new PixelRect(1500, 100, 1000, 800);
        Assert.Equal(bounds, WindowPlacement.Fit(bounds, [Laptop, External]));
    }

    [Fact]
    public void ShrinksWindowLargerThanTheRemainingDisplay()
    {
        // Sized for the external display, then moved onto the laptop panel by
        // Windows when the external one was disconnected.
        var bounds = new PixelRect(100, 50, 2400, 1300);
        Assert.Equal(Laptop, WindowPlacement.Fit(bounds, [Laptop]));
    }

    [Fact]
    public void MovesWindowPartlyOffTheEdgeBackInside()
    {
        var bounds = new PixelRect(1500, 600, 800, 600);
        Assert.Equal(new PixelRect(1120, 432, 800, 600), WindowPlacement.Fit(bounds, [Laptop]));
    }

    [Fact]
    public void MovesWindowOnARemovedDisplayToTheNearestOne()
    {
        // Restored at a position on the external display, which is gone.
        var bounds = new PixelRect(2500, 200, 1200, 800);
        Assert.Equal(new PixelRect(720, 200, 1200, 800), WindowPlacement.Fit(bounds, [Laptop]));

        // With a display left of the laptop too, the laptop is still the nearest.
        var left = new PixelRect(-2560, 0, 2560, 1392);
        Assert.Equal(new PixelRect(720, 200, 1200, 800), WindowPlacement.Fit(bounds, [left, Laptop]));
    }

    [Fact]
    public void FitsIntoTheDisplayItOverlapsMostWhenPartIsOnNoDisplay()
    {
        // The external display is taller, so the bottom of a window spanning both
        // hangs below the laptop panel, where there is no display.
        var bounds = new PixelRect(1700, 600, 1000, 700);
        Assert.Equal(new PixelRect(1920, 600, 1000, 700), WindowPlacement.Fit(bounds, [Laptop, External]));
    }

    [Fact]
    public void HandlesDisplaysLeftOfAndAbovePrimary()
    {
        var left = new PixelRect(-2560, -360, 2560, 1392);
        var bounds = new PixelRect(-3000, -500, 1000, 700);
        Assert.Equal(new PixelRect(-2560, -360, 1000, 700), WindowPlacement.Fit(bounds, [left, Laptop]));
    }

    [Fact]
    public void CountsMirroredDisplaysOnce()
    {
        // Two displays mirroring the same area would otherwise cover the off-screen
        // half of the window twice over.
        var bounds = new PixelRect(1400, 100, 1000, 700);
        Assert.Equal(new PixelRect(920, 100, 1000, 700), WindowPlacement.Fit(bounds, [Laptop, Laptop]));
    }

    [Fact]
    public void LeavesBoundsAloneWithoutDisplayInformation()
    {
        var bounds = new PixelRect(5000, 5000, 800, 600);
        Assert.Equal(bounds, WindowPlacement.Fit(bounds, []));
        Assert.Equal(bounds, WindowPlacement.Fit(bounds, [new PixelRect(0, 0, 0, 0)]));
    }
}
