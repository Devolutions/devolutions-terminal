using Devolutions.Terminal.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Devolutions.Terminal.Automation.Mcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var hostExecutable = Environment.GetEnvironmentVariable("DTERM_AUTOMATION_HOST")
                ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dt.exe" : "dt");
            if (Environment.GetEnvironmentVariable("DTERM_AUTOMATION_HOST") is null &&
                !File.Exists(hostExecutable))
            {
                hostExecutable = "dt";
            }

            var ensureHost = (CancellationToken cancellationToken) => AutomationHostLauncher.EnsureRunningAsync(
                hostExecutable, ["automation", "host", "--detach"], cancellationToken);
            await ensureHost(CancellationToken.None).ConfigureAwait(false);

            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });
            builder.Services.AddSingleton(_ => new AutomationIpcClient(ensureHost));
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<TerminalAutomationTools>();
            await builder.Build().RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or
                                          InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Console.Error.WriteLineAsync($"dt-automation-mcp: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
