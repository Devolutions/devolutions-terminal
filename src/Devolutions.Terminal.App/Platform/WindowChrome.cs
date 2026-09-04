using Avalonia;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Platform;

public static class WindowChrome
{
    public const double MacOsTrafficLightFallback = 70;
    public const double WindowsCaptionFallback = 138;

    public static bool ShouldShowTabRow(
        AppSettings settings,
        int tabCount,
        bool fullscreen)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (fullscreen && !settings.ShowTabsFullscreen)
        {
            return false;
        }

        return settings.AlwaysShowTabs || tabCount > 1;
    }

    public static bool ShouldUseCustomTitlebar(AppSettings settings, bool embedded, bool macOS = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !embedded && settings.ShowTabsInTitlebar;
    }

    public static Thickness TitleBarContentMargin(
        bool fullscreen,
        bool macOS,
        bool windows,
        Thickness offScreenMargin,
        bool rightToLeft = false)
    {
        if (fullscreen)
        {
            return new Thickness(8, 0, 8, 0);
        }

        if (macOS && !MacOsWindowControlsOnRight(offScreenMargin, rightToLeft))
        {
            return new Thickness(
                Math.Max(offScreenMargin.Left, MacOsTrafficLightFallback),
                0,
                8,
                0);
        }

        var right = windows || macOS
            ? Math.Max(offScreenMargin.Right, WindowsCaptionFallback)
            : WindowsCaptionFallback;
        return new Thickness(8, 0, right, 0);
    }

    public static bool MacOsWindowControlsOnRight(
        Thickness decorationMargin,
        bool rightToLeft)
    {
        if (rightToLeft)
        {
            return true;
        }

        return decorationMargin.Right - decorationMargin.Left >= 24 &&
               decorationMargin.Right >= 40;
    }

    public static double TabStripTrailingReserve(
        bool macOS,
        bool windows,
        bool macOsControlsOnRight = false) =>
        macOS && !macOsControlsOnRight
            ? MacOsTrafficLightFallback + 72
            : 253;
}
