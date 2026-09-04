using System.Runtime.Versioning;
using System.Text;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Ghostty.Tests;

[SupportedOSPlatform("windows")]
public sealed class GhosttyConPtyIntegrationTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task CmdEchoProjectsIntoGhosttyGrid()
    {
        var marker = "GHOSTTY_CONPTY_OK";
        await AssertGhosttyProjectsConPtyAsync(
            EchoCommand(marker),
            writeCommands: [],
            marker,
            TestContext.Current.CancellationToken);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsPowerShellMarkerProjectsIntoGhosttyGrid()
    {
        var marker = "GHOSTTY_PS_OK";
        await AssertGhosttyProjectsConPtyAsync(
            WindowsPowerShellCommand("-NoLogo -NoProfile"),
            writeCommands: ["Write-Output ([string]::Concat('GHOSTTY','_PS_OK'))\r"],
            marker,
            TestContext.Current.CancellationToken);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsPowerShellWithProfileProjectsPrompt()
    {
        await AssertGhosttyProjectsConPtyAsync(
            WindowsPowerShellCommand(string.Empty),
            writeCommands: [],
            "PS ",
            TestContext.Current.CancellationToken);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task ReplayOfPowerShellStartupBytesProjectsIntoGhosttyGrid()
    {
        var marker = "GHOSTTY_REPLAY_OK";
        var captured = await CaptureConPtyBytesAsync(
            WindowsPowerShellCommand("-NoLogo -NoProfile"),
            writeCommands: ["Write-Output ([string]::Concat('GHOSTTY','_REPLAY_OK'))\r", "exit\r"],
            marker,
            TestContext.Current.CancellationToken);

        using var ghostty = new GhosttyTerminalEngine(80, 24);
        using var builtIn = new TerminalEngine(80, 24);
        Exception? feedError = null;
        try
        {
            ghostty.Feed(captured);
            builtIn.Feed(captured);
        }
        catch (Exception ex)
        {
            feedError = ex;
        }

        AssertProjectionContains(
            marker,
            captured,
            ghostty,
            builtIn,
            feedError,
            responseCount: 0);
    }

    private static async Task AssertGhosttyProjectsConPtyAsync(
        string commandLine,
        string[] writeCommands,
        string marker,
        CancellationToken cancellationToken)
    {
        await using var connection = new ConPtyConnection();
        using var ghostty = new GhosttyTerminalEngine(80, 24);
        using var builtIn = new TerminalEngine(80, 24);
        var captured = new List<byte>();
        var responses = new List<byte[]>();
        Exception? feedError = null;
        var gate = new object();

        ghostty.Invalidated += (_, _) => ghostty.CreateSnapshot(includeHistory: true);
        ghostty.ResponseReady += (_, data) =>
        {
            lock (gate)
            {
                responses.Add(data);
            }

            try
            {
                connection.Write(data);
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    feedError ??= ex;
                }
            }
        };
        connection.OutputReceived += (_, data) =>
        {
            try
            {
                lock (gate)
                {
                    if (feedError is not null)
                    {
                        captured.AddRange(data.Span.ToArray());
                        return;
                    }
                }

                ghostty.Feed(data.Span);
                builtIn.Feed(data.Span);
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    feedError ??= ex;
                }
            }

            lock (gate)
            {
                captured.AddRange(data.Span.ToArray());
            }
        };

        await connection.StartAsync(commandLine, null, 80, 24, cancellationToken);
        foreach (var command in writeCommands)
        {
            connection.Write(command);
        }

        await WaitForAsync(
            () =>
            {
                lock (gate)
                {
                    return Encoding.UTF8.GetString([.. captured]).Contains(marker, StringComparison.Ordinal);
                }
            },
            TimeSpan.FromSeconds(15),
            () =>
            {
                lock (gate)
                {
                    return Encoding.UTF8.GetString([.. captured]);
                }
            },
            cancellationToken);

        byte[] snapshot;
        lock (captured)
        {
            snapshot = [.. captured];
        }

        AssertProjectionContains(
            marker,
            snapshot,
            ghostty,
            builtIn,
            feedError,
            responses.Count);
    }

    private static async Task<byte[]> CaptureConPtyBytesAsync(
        string commandLine,
        string[] writeCommands,
        string marker,
        CancellationToken cancellationToken)
    {
        await using var connection = new ConPtyConnection();
        var captured = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, data) =>
        {
            lock (captured)
            {
                captured.AddRange(data.Span.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);

        await connection.StartAsync(commandLine, null, 80, 24, cancellationToken);
        foreach (var command in writeCommands)
        {
            connection.Write(command);
        }

        await WaitForAsync(
            () =>
            {
                lock (captured)
                {
                    return Encoding.UTF8.GetString([.. captured]).Contains(marker, StringComparison.Ordinal);
                }
            },
            TimeSpan.FromSeconds(15),
            () =>
            {
                lock (captured)
                {
                    return Encoding.UTF8.GetString([.. captured]);
                }
            },
            cancellationToken);
        _ = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        lock (captured)
        {
            return [.. captured];
        }
    }

    private static void AssertProjectionContains(
        string marker,
        byte[] captured,
        GhosttyTerminalEngine ghostty,
        TerminalEngine builtIn,
        Exception? feedError,
        int responseCount)
    {
        var raw = Encoding.UTF8.GetString(captured);
        var ghosttyText = ViewportText(ghostty);
        var builtInText = ViewportText(builtIn);
        var detail =
            $"feedError={feedError}\n" +
            $"responses={responseCount}\n" +
            $"rawChars={raw.Length} rawNonWs={CountNonWhitespace(raw)}\n" +
            $"ghosttyNonWs={CountNonWhitespace(ghosttyText)} builtinNonWs={CountNonWhitespace(builtInText)}\n" +
            $"ghostty={FormatViewport(ghosttyText)}\n" +
            $"builtin={FormatViewport(builtInText)}\n" +
            $"rawHead={FormatRawHead(captured)}";

        Assert.True(feedError is null, detail);
        Assert.Contains(marker, raw, StringComparison.Ordinal);
        Assert.True(CountNonWhitespace(ghosttyText) > 0, detail);
        Assert.True(
            ghosttyText.Contains(marker, StringComparison.Ordinal) ||
            Compact(ghosttyText).Contains(Compact(marker), StringComparison.Ordinal),
            detail);
        Assert.True(
            builtInText.Contains(marker, StringComparison.Ordinal) ||
            Compact(builtInText).Contains(Compact(marker), StringComparison.Ordinal),
            detail);
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        TimeSpan timeout,
        Func<string> snapshot,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        Assert.Fail($"Timed out waiting for ConPTY output. snapshot={FormatViewport(snapshot())}");
    }

    private static string ViewportText(ITerminalEngine engine)
    {
        var snapshot = engine.CreateSnapshot().Buffer;
        var builder = new StringBuilder();
        for (var row = 0; row < snapshot.Rows; row++)
        {
            foreach (var cell in snapshot.Lines[row].Cells)
            {
                builder.Append(cell.Text);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static int CountNonWhitespace(string text) =>
        text.Count(static value => !char.IsWhiteSpace(value));

    private static string Compact(string text) =>
        string.Concat(text.Where(static value => !char.IsWhiteSpace(value)));

    private static string FormatViewport(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length > 400)
        {
            trimmed = trimmed[..400] + "…";
        }

        return trimmed.Replace("\n", "\\n").Replace("\r", "\\r");
    }

    private static string FormatRawHead(byte[] captured)
    {
        var length = Math.Min(captured.Length, 160);
        return Convert.ToHexString(captured.AsSpan(0, length));
    }

    private static string EchoCommand(string value)
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /s /c \"echo {value}\"";
    }

    private static string WindowsPowerShellCommand(string arguments)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return $"\"{powershell}\" {arguments}";
    }
}
