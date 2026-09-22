using System.Text;
using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class BrowserShellConnectionTests
{
    [Fact]
    public async Task StartsStopped()
    {
        await using var connection = new BrowserShellConnection();

        Assert.False(connection.IsRunning);
        Assert.Equal(0, connection.Columns);
        Assert.Equal(0, connection.Rows);
        Assert.Equal(TerminalConnectionState.NotConnected, connection.State);
        Assert.True(connection.Capabilities.HasFlag(TerminalConnectionCapabilities.Restart));
    }

    [Fact]
    public async Task WriteBeforeStartFails()
    {
        await using var connection = new BrowserShellConnection();

        Assert.Throws<InvalidOperationException>(() => connection.Write("input"));
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(32768, 24)]
    public async Task RejectsInvalidDimensions(int columns, int rows)
    {
        await using var connection = new BrowserShellConnection();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => connection.StartAsync("dt-wasm", null, columns, rows));
    }

    [Fact]
    public async Task CannotStartTwice()
    {
        await using var connection = new BrowserShellConnection();
        await connection.StartAsync("dt-wasm", null, 80, 24);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.StartAsync("dt-wasm", null, 80, 24));
    }

    [Fact]
    public async Task WelcomeBannerAndEcho()
    {
        await using var connection = new BrowserShellConnection();
        var output = Capture(connection);
        await connection.StartAsync("dt-wasm", "/", 80, 24);

        await WaitForOutputAsync(output, "dt-wasm");
        connection.Write("echo hello-wasm\r");
        await WaitForOutputAsync(output, "hello-wasm");
        Assert.True(connection.IsRunning);
        Assert.Equal(TerminalConnectionState.Connected, connection.State);
        Assert.NotNull(connection.ProcessMetadata);
    }

    [Fact]
    public async Task UnknownCommandIsReported()
    {
        await using var connection = new BrowserShellConnection();
        var output = Capture(connection);
        await connection.StartAsync("dt-wasm", null, 80, 24);

        connection.Write("not-a-command\r");
        await WaitForOutputAsync(output, "command not found");
    }

    [Fact]
    public async Task ListsAndReadsVirtualFiles()
    {
        await using var connection = new BrowserShellConnection();
        var output = Capture(connection);
        await connection.StartAsync("dt-wasm", null, 80, 24);

        connection.Write("ls\r");
        await WaitForOutputAsync(output, "README.md");
        connection.Write("cat README.md\r");
        await WaitForOutputAsync(output, "in-process shell");
    }

    [Fact]
    public async Task BackspaceEditsTheLine()
    {
        await using var connection = new BrowserShellConnection();
        var output = Capture(connection);
        await connection.StartAsync("dt-wasm", null, 80, 24);

        connection.Write("echo hellp");
        connection.Write("\u007f");
        connection.Write("o\r");
        await WaitForOutputAsync(output, "hello");
        connection.Write("history\r");
        await WaitForOutputAsync(output, "echo hello");
    }

    [Fact]
    public async Task ExitClosesTheSession()
    {
        await using var connection = new BrowserShellConnection();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync("dt-wasm", null, 80, 24);

        connection.Write("exit\r");
        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, code);
        Assert.False(connection.IsRunning);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
        Assert.Equal(TerminalExitReason.ProcessExited, connection.LastExitInfo?.Reason);
    }

    [Fact]
    public async Task RestartReplacesTheSession()
    {
        await using var connection = new BrowserShellConnection();
        var output = Capture(connection);
        await connection.StartAsync("dt-wasm", null, 80, 24);
        connection.Write("echo first\r");
        await WaitForOutputAsync(output, "first");

        await connection.RestartAsync();
        await WaitForOutputAsync(output, "browser wasm");
        Assert.True(connection.IsRunning);
    }

    private static List<byte> Capture(BrowserShellConnection connection)
    {
        var output = new List<byte>();
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        return output;
    }

    private static async Task WaitForOutputAsync(List<byte> output, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                if (Encoding.UTF8.GetString([.. output]).Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(25);
        }

        lock (output)
        {
            Assert.Contains(expected, Encoding.UTF8.GetString([.. output]), StringComparison.Ordinal);
        }
    }
}
