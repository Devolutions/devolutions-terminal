using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConPtySchedulingTests
{
    private const string ProbeEnvironmentVariable = "DEVOLUTIONS_CONPTY_SCHEDULING_PROBE";
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task IdleSessionsDoNotStarveInputExitOrClose()
    {
        if (Environment.GetEnvironmentVariable(ProbeEnvironmentVariable) == "1")
        {
            await RunProbeAsync();
            return;
        }

        // Limit workers only in a child runner, never in the shared test process.
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(ConPtySchedulingTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(ConPtySchedulingTests).FullName}.{nameof(IdleSessionsDoNotStarveInputExitOrClose)}");
        start.ArgumentList.Add("-parallel");
        start.ArgumentList.Add("none");
        start.ArgumentList.Add("-noAutoReporters");
        start.Environment[ProbeEnvironmentVariable] = "1";
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Scheduling probe did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail($"ConPTY sessions exhausted the worker pool.\n{await stdout}\n{await stderr}");
        }

        Assert.True(process.ExitCode == 0, $"Scheduling probe failed:\n{await stdout}\n{await stderr}");
    }

    private static async Task RunProbeAsync()
    {
        ThreadPool.GetMinThreads(out _, out var minimumIo);
        ThreadPool.GetMaxThreads(out _, out var maximumIo);
        Assert.True(ThreadPool.SetMinThreads(2, minimumIo));
        Assert.True(ThreadPool.SetMaxThreads(8, maximumIo));
        var connections = new List<ConPtyConnection>();
        var ready = new List<Task>();
        var exits = new List<Task<int>>();
        try
        {
            for (var index = 0; index < 6; index++)
            {
                var connection = new ConPtyConnection();
                connections.Add(connection);
                var output = new StringBuilder();
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.OutputReceived += (_, bytes) =>
                {
                    output.Append(Encoding.UTF8.GetString(bytes.Span));
                    if (output.ToString().Contains("READY", StringComparison.Ordinal))
                    {
                        received.TrySetResult();
                    }
                };
                connection.Exited += (_, code) => exited.TrySetResult(code);
                connection.Faulted += (_, error) =>
                {
                    received.TrySetException(error);
                    exited.TrySetException(error);
                };
                ready.Add(received.Task);
                exits.Add(exited.Task);
                var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                await connection.StartAsync($"\"{comSpec}\" /d /q", null, 80, 24);
            }

            foreach (var connection in connections)
            {
                connection.Write("echo READY\r");
            }
            await Task.WhenAll(ready).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var connection in connections)
            {
                connection.Resize(132, 43);
                connection.Write("exit\r");
            }
            var exitCodes = await Task.WhenAll(exits).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(exitCodes, code => Assert.Equal(0, code));
        }
        finally
        {
            await Task.WhenAll(connections.Select(connection => connection.DisposeAsync().AsTask()))
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.All(connections, connection => Assert.False(connection.IsRunning));
    }
}
