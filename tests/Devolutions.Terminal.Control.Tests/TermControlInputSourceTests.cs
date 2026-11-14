using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

/// <summary>
/// Pins where input comes from: replies the engine produces on its own go through
/// <see cref="ITerminalConnection.WriteResponse"/>, focus goes through
/// <see cref="ITerminalConnection.ReportFocus"/>, and a connection that overrides neither
/// sees exactly the bytes it saw before.
/// </summary>
public sealed class TermControlInputSourceTests
{
    private const string Esc = "\u001b";

    [AvaloniaFact]
    public async Task EngineRepliesGoThroughWriteResponse()
    {
        var connection = new SourceAwareConnection();
        var control = new TermControl { ConnectionFactory = _ => connection };
        await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
        try
        {
            control.Engine.Feed($"{Esc}[6n");
            control.WriteInput("x");

            Assert.Equal($"{Esc}[1;1R", connection.ResponseText);
            Assert.Equal("x", connection.WrittenText);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task RepliesStillReachAConnectionThatDoesNotTellSourcesApart()
    {
        var connection = new PlainConnection();
        var control = new TermControl { ConnectionFactory = _ => connection };
        await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
        try
        {
            control.Engine.Feed($"{Esc}[6n");

            Assert.Equal($"{Esc}[1;1R", connection.WrittenText);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task FocusCarriedByTheConnectionIsNotTyped()
    {
        var connection = new SourceAwareConnection();
        var (window, control, other) = Host(connection);
        try
        {
            window.Show();
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.Engine.Feed($"{Esc}[?1004h");

            control.Focus();
            other.Focus();

            Assert.Equal(new[] { true, false }, connection.FocusReports);
            Assert.Equal("", connection.WrittenText);
        }
        finally
        {
            await control.CloseAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FocusIsReportedEvenWhenTheProgramDidNotAskForIt()
    {
        var connection = new SourceAwareConnection();
        var (window, control, other) = Host(connection);
        try
        {
            window.Show();
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            Assert.False(control.Engine.FocusTracking);

            control.Focus();
            other.Focus();

            Assert.Equal(new[] { true, false }, connection.FocusReports);
        }
        finally
        {
            await control.CloseAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FocusIsTypedForAConnectionThatDoesNotCarryIt()
    {
        var connection = new PlainConnection();
        var (window, control, other) = Host(connection);
        try
        {
            window.Show();
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.Engine.Feed($"{Esc}[?1004h");

            control.Focus();
            other.Focus();

            Assert.Equal($"{Esc}[I{Esc}[O", connection.WrittenText);
        }
        finally
        {
            await control.CloseAsync();
            window.Close();
        }
    }

    private static (Window Window, TermControl Control, TextBox Other) Host(ITerminalConnection connection)
    {
        var control = new TermControl { ConnectionFactory = _ => (IRestartableTerminalConnection)connection };
        var other = new TextBox();
        var panel = new DockPanel();
        DockPanel.SetDock(other, Dock.Top);
        panel.Children.Add(other);
        panel.Children.Add(control);
        return (new Window { Width = 800, Height = 600, Content = panel }, control, other);
    }

    private sealed class SourceAwareConnection : PlainConnection, IRestartableTerminalConnection
    {
        private readonly MemoryStream _responses = new();

        public string ResponseText => Encoding.UTF8.GetString(_responses.ToArray());
        public List<bool> FocusReports { get; } = [];

        void ITerminalConnection.WriteResponse(ReadOnlySpan<byte> data) => _responses.Write(data);

        bool ITerminalConnection.ReportFocus(bool focused)
        {
            FocusReports.Add(focused);
            return true;
        }
    }

    private class PlainConnection : IRestartableTerminalConnection
    {
        private readonly MemoryStream _written = new();

#pragma warning disable CS0067
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;
        public event EventHandler<Exception>? Faulted;
        public event EventHandler<TerminalExitInfo>? SessionExited;
#pragma warning restore CS0067

        public string WrittenText => Encoding.UTF8.GetString(_written.ToArray());

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
            StartAsync(new TerminalLaunchOptions { CommandLine = commandLine, Columns = columns, Rows = rows }, cancellationToken);

        public void Write(ReadOnlySpan<byte> data) => _written.Write(data);

        public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Write(data.Span);
            return ValueTask.CompletedTask;
        }

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
