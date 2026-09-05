using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class TermControlBlinkTests
{
    private const string Esc = "\u001b";

    [AvaloniaFact]
    public async Task UnfocusedPaneDoesNotAnimateCursor()
    {
        var connection = new FakePtyConnection();
        var control = new TermControl { ConnectionFactory = _ => connection };
        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);

            // Never focused: the pane draws a static cursor, so the blink timer must
            // not invalidate (damage-gated idle rendering).
            Assert.False(control.ShouldAnimateCursor);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task FocusedPaneAnimatesCursorWhenEngineBlinks()
    {
        var connection = new FakePtyConnection();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new TermControl { ConnectionFactory = _ => connection },
        };
        var control = (TermControl)window.Content!;
        try
        {
            window.Show();
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.Focus();

            Assert.True(control.IsFocused);
            Assert.True(control.ShouldAnimateCursor);
        }
        finally
        {
            await control.CloseAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SteadyCursorModeDoesNotAnimate()
    {
        var connection = new FakePtyConnection();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new TermControl { ConnectionFactory = _ => connection },
        };
        var control = (TermControl)window.Content!;
        try
        {
            window.Show();
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.Focus();
            Assert.True(control.ShouldAnimateCursor);

            // DECRST 12: steady cursor — the engine reports blinking off.
            control.Engine.Feed($"{Esc}[?12l");

            Assert.False(control.Engine.CursorBlinking);
            Assert.False(control.ShouldAnimateCursor);
        }
        finally
        {
            await control.CloseAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task HiddenCursorDoesNotAnimate()
    {
        var connection = new FakePtyConnection();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new TermControl { ConnectionFactory = _ => connection },
        };
        var control = (TermControl)window.Content!;
        try
        {
            window.Show();
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.Focus();

            // DECRST 25: cursor hidden.
            control.Engine.Feed($"{Esc}[?25l");

            Assert.False(control.Engine.CursorVisible);
            Assert.False(control.ShouldAnimateCursor);
        }
        finally
        {
            await control.CloseAsync();
            window.Close();
        }
    }

    private sealed class FakePtyConnection : IRestartableTerminalConnection
    {
#pragma warning disable CS0067
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;
        public event EventHandler<Exception>? Faulted;
        public event EventHandler<TerminalExitInfo>? SessionExited;
#pragma warning restore CS0067

        public bool IsRunning => true;
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public TerminalConnectionCapabilities Capabilities => TerminalConnectionCapabilities.Resize;
        public TerminalConnectionState State => TerminalConnectionState.Connected;
        public TerminalProcessMetadata? ProcessMetadata => null;
        public TerminalExitInfo? LastExitInfo => null;

        public Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        {
            Columns = options.Columns;
            Rows = options.Rows;
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

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void Resize(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
        }

        public Task RestartAsync(TerminalLaunchOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
