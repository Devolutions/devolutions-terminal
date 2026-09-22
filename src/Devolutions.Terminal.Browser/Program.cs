using Avalonia;
using Avalonia.Browser;
using Devolutions.Terminal.Browser;

internal sealed partial class Program
{
    private static Task Main(string[] args)
    {
        if (args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out _))
        {
            BrowserLaunchContext.PageUrl = args[0];
        }

        return BuildAvaloniaApp().StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BrowserTerminalApp>();
}
