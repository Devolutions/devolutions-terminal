using Avalonia;

namespace Devolutions.Terminal.App.Platform;

/// <summary>
/// Keeps a window reachable on the displays that exist now. A display can go
/// away while the app runs or between runs (a laptop undocked); Windows moves a
/// window off a removed display without shrinking it, and a restored position
/// can point at a display that is gone, so a window can end up larger than the
/// screen or outside it, with its caption buttons out of reach.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Returns <paramref name="bounds"/> unchanged when the work areas cover all of
    /// it, so a window spanning two connected displays stays put. Otherwise returns
    /// it shrunk to fit and moved inside the work area it overlaps most, or the
    /// nearest one when it overlaps none. All values are physical pixels.
    /// </summary>
    public static PixelRect Fit(PixelRect bounds, IReadOnlyList<PixelRect> workAreas)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return bounds;
        }

        // Mirrored displays report the same work area; count it once.
        var areas = workAreas.Where(static area => area.Width > 0 && area.Height > 0).Distinct().ToArray();
        if (areas.Length == 0)
        {
            return bounds;
        }

        long covered = 0;
        long bestOverlap = 0;
        var target = areas[0];
        foreach (var area in areas)
        {
            var overlap = OverlapArea(bounds, area);
            covered += overlap;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                target = area;
            }
        }

        // Work areas do not overlap one another, so full coverage means every pixel
        // of the window is on some display.
        if (covered >= (long)bounds.Width * bounds.Height)
        {
            return bounds;
        }

        if (bestOverlap == 0)
        {
            target = Nearest(bounds, areas);
        }

        var width = Math.Min(bounds.Width, target.Width);
        var height = Math.Min(bounds.Height, target.Height);
        return new PixelRect(
            Math.Clamp(bounds.X, target.X, target.Right - width),
            Math.Clamp(bounds.Y, target.Y, target.Bottom - height),
            width,
            height);
    }

    private static long OverlapArea(PixelRect a, PixelRect b)
    {
        long width = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        long height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }

    // The work area closest to the window's center.
    private static PixelRect Nearest(PixelRect bounds, PixelRect[] areas)
    {
        var centerX = bounds.X + (bounds.Width / 2L);
        var centerY = bounds.Y + (bounds.Height / 2L);
        var nearest = areas[0];
        var nearestDistance = long.MaxValue;
        foreach (var area in areas)
        {
            var dx = centerX - Math.Clamp(centerX, area.X, area.Right);
            var dy = centerY - Math.Clamp(centerY, area.Y, area.Bottom);
            var distance = (dx * dx) + (dy * dy);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = area;
            }
        }

        return nearest;
    }
}
