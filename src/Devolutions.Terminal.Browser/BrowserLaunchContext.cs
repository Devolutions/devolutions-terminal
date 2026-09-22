namespace Devolutions.Terminal.Browser;

internal static class BrowserLaunchContext
{
    public static string? PageUrl { get; set; }

    public static Uri? TryGetHttpBase()
    {
        if (!Uri.TryCreate(PageUrl, UriKind.Absolute, out var page) ||
            page.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return new UriBuilder(page.Scheme, page.Host, page.Port, "/").Uri;
    }
}
