using System.Net;
using Microsoft.Playwright;
using Xunit;

namespace Devolutions.Terminal.Browser.E2E;

public sealed class BrowserWasmE2ETests
{
    public static bool BrowserE2EEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("DTERM_BROWSER_E2E"),
            "1",
            StringComparison.Ordinal);

    [Fact(Skip = "Set DTERM_BROWSER_E2E=1 to publish WASM and drive Chromium.", SkipUnless = nameof(BrowserE2EEnabled))]
    public async Task BrowserHostBootsAndRunsShellCommands()
    {
        var wwwroot = await PublishBrowserHostAsync();
        var prefix = StartStaticServer(wwwroot, out var server);
        try
        {
            if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
            {
                throw new InvalidOperationException("Playwright failed to install Chromium.");
            }
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(ChromiumOptions());
            var page = await browser.NewPageAsync();
            var consoleMessages = new List<string>();
            page.Console += (_, msg) => consoleMessages.Add($"{msg.Type}: {msg.Text}");
            page.PageError += (_, error) => consoleMessages.Add("pageerror: " + error);
            await page.GotoAsync(prefix, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120_000 });
            try
            {
                await page.WaitForFunctionAsync(
                    "() => globalThis.__dterm && globalThis.__dterm.ready && globalThis.__dterm.ready()",
                    null,
                    new() { Timeout = 120_000 });
            }
            catch (TimeoutException)
            {
                var body = await page.ContentAsync();
                throw new TimeoutException(
                    "Timed out waiting for the browser terminal probe.\n" +
                    string.Join('\n', consoleMessages) +
                    "\n" + body);
            }

            var welcome = await page.EvaluateAsync<string>("() => globalThis.__dterm.getText()");
            Assert.Contains("dt-wasm", welcome, StringComparison.Ordinal);

            await page.EvaluateAsync("() => globalThis.__dterm.sendInput('echo e2e-ok\\r')");
            await page.WaitForFunctionAsync(
                "() => (globalThis.__dterm.getText() || '').includes('e2e-ok')",
                null,
                new() { Timeout = 30_000 });

            var text = await page.EvaluateAsync<string>("() => globalThis.__dterm.getText()");
            Assert.Contains("e2e-ok", text, StringComparison.Ordinal);
            Assert.Contains("browser wasm", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            server.Stop();
            server.Close();
        }
    }

    public static bool HostPtyUrlConfigured =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("DTERM_HOST_PTY_URL"), UriKind.Absolute, out _);

    [Fact(Skip = "Set DTERM_HOST_PTY_URL to an already running loopback host.", SkipUnless = nameof(HostPtyUrlConfigured))]
    public async Task HostPtyPageRunsRealShell()
    {
        var prefix = Environment.GetEnvironmentVariable("DTERM_HOST_PTY_URL")
            ?? throw new InvalidOperationException("DTERM_HOST_PTY_URL is required.");
        if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
        {
            throw new InvalidOperationException("Playwright failed to install Chromium.");
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(ChromiumOptions());
        var page = await browser.NewPageAsync();
        var consoleMessages = new List<string>();
        page.Console += (_, msg) => consoleMessages.Add($"{msg.Type}: {msg.Text}");
        page.PageError += (_, error) => consoleMessages.Add("pageerror: " + error);
        await page.GotoAsync(prefix, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120_000 });
        try
        {
            await page.WaitForFunctionAsync(
                "() => globalThis.__dterm && globalThis.__dterm.ready && globalThis.__dterm.ready()",
                null,
                new() { Timeout = 120_000 });
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "Timed out waiting for the host PTY page.\n" + string.Join('\n', consoleMessages));
        }

        await page.EvaluateAsync("() => globalThis.__dterm.sendInput('echo host-browser-ok\\r')");
        try
        {
            await page.WaitForFunctionAsync(
                "() => (globalThis.__dterm.getText() || '').includes('host-browser-ok')",
                null,
                new() { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            var text = await page.EvaluateAsync<string>("() => globalThis.__dterm.getText()");
            throw new TimeoutException(text + "\n" + string.Join('\n', consoleMessages));
        }
    }

    private static BrowserTypeLaunchOptions ChromiumOptions() => new()
    {
        Headless = true,
        Args =
        [
            "--use-gl=angle",
            "--use-angle=swiftshader",
            "--enable-unsafe-swiftshader",
            "--ignore-gpu-blocklist",
            "--disable-dev-shm-usage",
        ],
    };

    private static async Task<string> PublishBrowserHostAsync()
    {
        var overridePath = Environment.GetEnvironmentVariable("DTERM_BROWSER_WWWROOT");
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, "src", "Devolutions.Terminal.Browser", "Devolutions.Terminal.Browser.csproj");
        var output = Path.Combine(repoRoot, "artifacts", "browser-wasm");
        Directory.CreateDirectory(output);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("publish");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("-p:SkipNativeRestore=true");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(output);

        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed ({process.ExitCode}):\n{stdout}\n{stderr}");
        }

        var wwwroot = Path.Combine(output, "wwwroot");
        if (!Directory.Exists(wwwroot))
        {
            wwwroot = Path.Combine(
                repoRoot,
                "src",
                "Devolutions.Terminal.Browser",
                "bin",
                "Release",
                "net10.0-browser",
                "publish",
                "wwwroot");
        }

        if (!Directory.Exists(wwwroot))
        {
            throw new DirectoryNotFoundException($"Published wwwroot was not found. {stdout}");
        }

        return wwwroot;
    }

    private static string StartStaticServer(string wwwroot, out HttpListener server)
    {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        server = new HttpListener();
        server.Prefixes.Add(prefix);
        server.Start();
        var root = wwwroot;
        var listener = server;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => Serve(root, context));
            }
        });
        return prefix;
    }

    private static void Serve(string root, HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/")
            {
                path = "/index.html";
            }

            var full = Path.GetFullPath(Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            context.Response.ContentType = Mime(full);
            var bytes = File.ReadAllBytes(full);
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // The listener is shutting down.
            }
        }
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".wasm" => "application/wasm",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".dat" => "application/octet-stream",
        ".map" => "application/json",
        ".webcil" => "application/octet-stream",
        _ => "application/octet-stream",
    };

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Devolutions.Terminal.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
