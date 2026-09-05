using System.Text;
using Avalonia.Headless.XUnit;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

/// <summary>
/// Pins the paste vs send-input separation (winterm-ghostty GD-08/GD-15): paste is
/// sanitized and bracketed by the engine; SendInput/WriteInput writes literal bytes.
/// </summary>
public sealed class TermControlPasteTests
{
    private const string Esc = "\u001b";

    [AvaloniaFact]
    public async Task WriteInputSendsLiteralBytesEvenInBracketedPasteMode()
    {
        var connection = new RecordingConnection();
        var control = new TermControl { ConnectionFactory = _ => connection };
        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.Engine.Feed($"{Esc}[?2004h");
            Assert.True(control.Engine.BracketedPaste);

            control.WriteInput($"a{Esc}[Xb");

            Assert.Equal($"a{Esc}[Xb", connection.WrittenText);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task PasteTextWrapsAndStripsWhenBracketedPasteEnabled()
    {
        var connection = new RecordingConnection();
        var control = new TermControl { ConnectionFactory = _ => connection };
        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            control.Engine.Feed($"{Esc}[?2004h");
            Assert.True(control.Engine.BracketedPaste);

            var result = control.PasteText($"echo hi{Esc}[31m");

            Assert.Equal(TerminalPasteResult.Written, result);
            Assert.Equal($"{Esc}[200~echo hi[31m{Esc}[201~", connection.WrittenText);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task PasteTextSendsRawTextWhenBracketedPasteDisabled()
    {
        var connection = new RecordingConnection();
        var control = new TermControl { ConnectionFactory = _ => connection };
        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);
            Assert.False(control.Engine.BracketedPaste);

            var result = control.PasteText("echo hi");

            Assert.Equal(TerminalPasteResult.Written, result);
            Assert.Equal("echo hi", connection.WrittenText);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task PasteTextTrimsTrailingWhitespaceWhenNotBracketed()
    {
        var connection = new RecordingConnection();
        var control = new TermControl { ConnectionFactory = _ => connection };
        try
        {
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 80, 24);

            var result = control.PasteText(
                "echo hi  \r\n",
                new TerminalPasteOptions
                {
                    TrimWhitespace = true,
                    WarnAboutLargePaste = false,
                    WarnAboutMultiLinePaste = "never",
                });

            Assert.Equal(TerminalPasteResult.Written, result);
            Assert.Equal("echo hi", connection.WrittenText);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    private sealed class RecordingConnection : IRestartableTerminalConnection
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
            lock (_written)
            {
                _written.Write(data);
            }
        }

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
