using Avalonia;

namespace Devolutions.Terminal.Control.Sample;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        args.Contains("--pty-smoke", StringComparer.Ordinal)
            ? PtySmoke.RunAsync().GetAwaiter().GetResult()
            : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
