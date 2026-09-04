using System.Text;
using Avalonia.Headless.XUnit;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class TermControlOutputPumpTests
{
    [AvaloniaFact]
    public async Task OutputPumpKeepsFeedingWhenViewportListenersThrow()
    {
        var connection = new FakePtyConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;
        control.ViewportChanged += (_, _) => throw new InvalidOperationException("scrollbar");

        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            await Task.Run(() =>
            {
                connection.Emit("hello");
                connection.Emit(" world");
            });

            var line = string.Concat(
                control.Engine.CreateSnapshot().Buffer.Lines[0].Cells.Select(static cell => cell.Text));
            Assert.Contains("hello world", line, StringComparison.Ordinal);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    private sealed class FakePtyConnection : IRestartableTerminalConnection
    {
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
#pragma warning disable CS0067
        public event EventHandler<int>? Exited;
        public event EventHandler<Exception>? Faulted;
        public event EventHandler<TerminalExitInfo>? SessionExited;
#pragma warning restore CS0067

        public bool IsRunning { get; private set; }
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public TerminalConnectionCapabilities Capabilities => TerminalConnectionCapabilities.Resize;
        public TerminalConnectionState State { get; private set; }
        public TerminalProcessMetadata? ProcessMetadata => null;
        public TerminalExitInfo? LastExitInfo => null;

        public Task StartAsync(
            TerminalLaunchOptions options,
            CancellationToken cancellationToken = default)
        {
            Columns = options.Columns;
            Rows = options.Rows;
            IsRunning = true;
            State = TerminalConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task StartAsync(
            string commandLine,
            string? workingDirectory,
            int columns,
            int rows,
            CancellationToken cancellationToken = default) =>
            StartAsync(
                new TerminalLaunchOptions
                {
                    CommandLine = commandLine,
                    WorkingDirectory = workingDirectory,
                    Columns = columns,
                    Rows = rows,
                },
                cancellationToken);

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void Resize(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
        }

        public Task RestartAsync(
            TerminalLaunchOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            State = TerminalConnectionState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(string text) =>
            OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));
    }
}
