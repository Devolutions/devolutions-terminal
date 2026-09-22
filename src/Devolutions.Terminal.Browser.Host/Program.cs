namespace Devolutions.Terminal.Browser.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Console.Error.WriteLine(eventArgs.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Console.Error.WriteLine(eventArgs.Exception);
            eventArgs.SetObserved();
        };

        var port = 5235;
        string? webRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out var parsed):
                    port = parsed;
                    break;
                case "--wwwroot" when i + 1 < args.Length:
                    webRoot = args[++i];
                    break;
                case "--help" or "-h":
                    Console.WriteLine("Devolutions.Terminal.Browser.Host [--wwwroot <path>] [--port <n>]");
                    Console.WriteLine("Serves the Avalonia WASM site, /dt profile control, and one WebSocket per tab.");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                    return 1;
            }
        }

        webRoot ??= ResolveWebRoot();
        if (!Directory.Exists(webRoot))
        {
            Console.Error.WriteLine($"wwwroot was not found at '{webRoot}'.");
            Console.Error.WriteLine("Publish the browser host first:");
            Console.Error.WriteLine("  dotnet publish src/Devolutions.Terminal.Browser -c Release -o artifacts/browser-wasm");
            return 1;
        }

        Console.WriteLine("Loopback only. The page can list profiles and launch each as its own tab.");
        Console.WriteLine("loading profiles...");
        var catalog = HostProfileCatalog.Load();
        var launchable = catalog.Profiles.Count(profile => profile.Launchable);
        Console.WriteLine($"profiles {catalog.Profiles.Count} ({launchable} launchable)");
        foreach (var profile in catalog.Profiles)
        {
            var state = profile.Launchable ? "launch" : "skip";
            Console.WriteLine($"  {state} {profile.Name}" + (profile.Reason is null ? "" : $" - {profile.Reason}"));
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        await using var broker = catalog.CreateBroker();
        await HostPtyStaticServer.RunAsync(
            new HostPtyStaticServerOptions { WebRoot = webRoot, Port = port, Broker = broker },
            stop.Token).ConfigureAwait(false);
        return 0;
    }

    private static string ResolveWebRoot()
    {
        var candidates = new[]
        {
            Path.Combine("artifacts", "browser-wasm", "wwwroot"),
            Path.Combine("artifacts", "browser-wasm", "publish", "wwwroot"),
            Path.Combine("src", "Devolutions.Terminal.Browser", "bin", "Release", "net10.0-browser", "wwwroot"),
            Path.Combine("src", "Devolutions.Terminal.Browser", "bin", "Release", "net10.0-browser", "publish", "wwwroot"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }
        }

        return candidates[0];
    }
}
