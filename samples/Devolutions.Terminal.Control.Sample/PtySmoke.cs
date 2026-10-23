using System.Text;
using Devolutions.Terminal.Connection;

namespace Devolutions.Terminal.Control.Sample;

// Headless check, used by CI, that the package's own PTY transport can start a
// local process: ConPTY with the bundled OpenConsole.exe on Windows, and
// dt-pty-host from runtimes/<rid>/native on Linux and macOS.
internal static class PtySmoke
{
    private const string Marker = "DT_PTY_SMOKE_OK";

    public static async Task<int> RunAsync()
    {
        await using ITerminalConnection connection = CreateConnection(out var commandLine);
        var output = new StringBuilder();
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                if (output.ToString().Contains(Marker, StringComparison.Ordinal))
                {
                    seen.TrySetResult();
                }
            }
        };
        connection.Faulted += (_, error) => seen.TrySetException(error);

        try
        {
            await connection.StartAsync(commandLine, Environment.CurrentDirectory, 80, 24);
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception)
        {
            lock (output)
            {
                Console.Error.WriteLine($"PTY smoke failed: {exception.Message}\nOutput so far:\n{output}");
            }

            return 1;
        }

        Console.WriteLine($"PTY smoke passed: {connection.GetType().Name} ran '{commandLine}'.");
        return 0;
    }

    // The marker is split in the command so that only executed output can match it.
    private static ITerminalConnection CreateConnection(out string commandLine)
    {
        if (OperatingSystem.IsWindows())
        {
            commandLine = "cmd.exe /d /c echo DT_PTY_^SMOKE_OK";
            return new ConPtyConnection();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            commandLine = "printf 'DT_PTY_%s\\n' SMOKE_OK";
            return new LinuxPtyConnection();
        }

        throw new PlatformNotSupportedException("No local PTY implementation is available for this platform.");
    }
}
