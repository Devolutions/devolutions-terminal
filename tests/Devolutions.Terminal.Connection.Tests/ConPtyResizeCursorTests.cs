using System.Runtime.Versioning;
using System.Text;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

// ConPTY positions the shell's input with absolute cursor moves relative to its own
// scrollback-less buffer, so the engine must place its viewport the same way after
// a resize or clear, or typed input lands on the wrong row.
[SupportedOSPlatform("windows")]
public sealed class ConPtyResizeCursorTests
{
    private const int Columns = 120;
    private const int Rows = 30;

    public static bool IsWindows => OperatingSystem.IsWindows();

    // Steps are ';'-separated: "e:cols,rows" resizes only the engine (the control resizes
    // it before the debounced PTY resize), "p:cols,rows" only the PTY, "b:cols,rows" both.
    [Theory(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    [InlineData(0, "b:120,29", "b:120,30")]
    [InlineData(0, "e:120,29", "p:120,29;b:120,30")]
    [InlineData(0, "", "b:120,20")]
    [InlineData(0, "", "b:120,1;b:120,30")]
    [InlineData(40, "", "b:120,40")]
    [InlineData(40, "", "b:60,30;b:120,40")]
    [InlineData(10, "", "b:120,5;b:120,30")]
    public async Task ShellInputStaysOnPromptRowAfterResize(int fillLines, string beforeOutput, string afterOutput)
    {
        await using var connection = new ConPtyConnection();
        var session = new PromptSession(connection);
        await session.StartAsync(fillLines, beforeOutput);

        await ApplyAsync(afterOutput, session.Engine, connection, session.Gate);

        await session.TypeAndAssertPromptRowAsync();
    }

    [Theory(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(40)]
    public async Task ShellInputStaysOnPromptRowAfterClearBuffer(int fillLines)
    {
        await using var connection = new ConPtyConnection();
        var session = new PromptSession(connection);
        await session.StartAsync(fillLines, string.Empty);

        lock (session.Gate)
        {
            session.Engine.ClearBufferKeepingCursorRow();
        }

        connection.ClearBuffer(keepCursorRow: true);

        await session.TypeAndAssertPromptRowAsync();
        lock (session.Gate)
        {
            Assert.Equal(0, session.Engine.CursorY);
            Assert.Equal(0, session.Engine.HistoryCount);
        }
    }

    private sealed class PromptSession
    {
        private readonly ConPtyConnection _connection;
        private readonly StringBuilder _transcript = new();

        public PromptSession(ConPtyConnection connection)
        {
            _connection = connection;
            connection.OutputReceived += (_, bytes) =>
            {
                lock (Gate)
                {
                    Engine.Feed(bytes.Span);
                    _transcript.Append(Encoding.UTF8.GetString(bytes.Span));
                }
            };
            Engine.ResponseReady += (_, bytes) => connection.Write(bytes);
        }

        public TerminalEngine Engine { get; } = new(Columns, Rows);
        public object Gate { get; } = new();

        public async Task StartAsync(int fillLines, string beforeOutput)
        {
            // Mimic PSReadLine: after a key press, move the cursor through the console API
            // (which ConPTY translates into absolute CUP sequences) and echo the input.
            var script =
                $"for($i=0;$i -lt {fillLines};$i++){{[Console]::WriteLine('fill'+$i)}};" +
                "[Console]::WriteLine('BANNER');[Console]::Write('PROMPT> ');" +
                "$null=[Console]::ReadKey($true);$x=[Console]::CursorLeft;$y=[Console]::CursorTop;" +
                "[Console]::SetCursorPosition(0,0);[Console]::SetCursorPosition($x,$y);" +
                "[Console]::Write('typed');Start-Sleep -Seconds 10";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            await _connection.StartAsync(
                $"powershell.exe -NoLogo -NoProfile -EncodedCommand {encoded}",
                null,
                Columns,
                Rows,
                TestContext.Current.CancellationToken);

            await ApplyAsync(beforeOutput, Engine, _connection, Gate);
            await WaitForOutputAsync("PROMPT> ");
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        public async Task TypeAndAssertPromptRowAsync()
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            _connection.Write("x");
            await WaitForOutputAsync("typed");
            await Task.Delay(300, TestContext.Current.CancellationToken);

            lock (Gate)
            {
                Assert.Equal("PROMPT> typed", RowText(Engine, Engine.CursorY));
            }
        }

        private async Task WaitForOutputAsync(string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (Gate)
                {
                    if (_transcript.ToString().Contains(expected, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            lock (Gate)
            {
                throw new TimeoutException(
                    $"Timed out waiting for '{expected}'. Output: {_transcript.ToString().Replace("\u001b", "\\e", StringComparison.Ordinal)}");
            }
        }
    }

    private static async Task ApplyAsync(
        string steps,
        TerminalEngine engine,
        ConPtyConnection connection,
        object gate)
    {
        foreach (var step in steps.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var size = step[2..].Split(',');
            var columns = int.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture);
            var rows = int.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture);
            if (step[0] is 'e' or 'b')
            {
                lock (gate)
                {
                    engine.Resize(columns, rows);
                }
            }

            if (step[0] is 'p' or 'b')
            {
                connection.Resize(columns, rows);
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static string RowText(TerminalEngine engine, int row) =>
        string.Concat(engine.Buffer.GetRow(row)
            .Where(static cell => !cell.IsWideContinuation)
            .Select(static cell => cell.Text)).TrimEnd();
}
