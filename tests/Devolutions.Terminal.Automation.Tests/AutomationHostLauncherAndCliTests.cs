using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Devolutions.Terminal.Automation;
using Devolutions.Terminal.Cli;
using Xunit;

namespace Devolutions.Terminal.Automation.Tests;

public sealed class AutomationHostLauncherAndCliTests
{
    [Fact]
    public async Task EnsureRunningAsync_RejectsBlankExecutableBeforeLaunching()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AutomationHostLauncher.EnsureRunningAsync(" ", []));
    }

    [Fact]
    public async Task EnsureRunningAsync_RejectsNullArgumentsBeforeLaunching()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            AutomationHostLauncher.EnsureRunningAsync("dt", null!));
    }

    [Fact]
    public void GetCurrentProcessCommand_DescribesExecutingHost()
    {
        var (executable, prefixArguments) = AutomationHostLauncher.GetCurrentProcessCommand();

        Assert.Equal(Environment.ProcessPath, executable);
        if (Path.GetFileNameWithoutExtension(executable)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(Environment.GetCommandLineArgs()[0], Assert.Single(prefixArguments));
        }
        else
        {
            Assert.Empty(prefixArguments);
        }
    }

    [Fact]
    public async Task StartHostProcess_DoesNotKeepCallerOutputPipeOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var probeHandle = pipe.ClientSafePipeHandle.DangerousGetHandle();
        var originalOutput = GetStdHandle(-11);
        if (!SetStdHandle(-11, probeHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Process? child = null;
        try
        {
            try
            {
                var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                startInfo.ArgumentList.Add("/Q");
                child = AutomationHostLauncher.StartHostProcess(startInfo);
                Assert.True(GetHandleInformation(probeHandle, out var flags));
                Assert.NotEqual(0u, flags & 1);
            }
            finally
            {
                if (!SetStdHandle(-11, originalOutput))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            pipe.DisposeLocalCopyOfClientHandle();
            var buffer = new byte[1];
            var count = await pipe.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, count);
            Assert.False(child.HasExited);
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        await child.StandardInput.WriteLineAsync("exit");
                        child.StandardInput.Close();
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                finally
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync();
                    }

                    child.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task TerminalAutomationCli_HelpPrintsCommandSummaryWithoutStartingHost()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            Assert.Equal(0, await TerminalAutomationCli.RunAsync([]));
            Assert.StartsWith("dt automation <command>", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("start --command <command-line>", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("close <id> | stop", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public void ParseRequest_StartValidatesOptionsAndKeepsRequestedValues()
    {
        var request = TerminalAutomationCli.ParseRequest(
            ["start", "--rows", "4", "--command", "sample --flag", "--cwd", "C:\\work", "--cols", "12"]);

        Assert.Equal("start", request.Type);
        Assert.Equal("sample --flag", request.CommandLine);
        Assert.Equal("C:\\work", request.WorkingDirectory);
        Assert.Equal((12, 4), (request.Columns, request.Rows));

        foreach (var invalid in new[]
        {
            new[] { "start" },
            ["start", "--command", " "],
            ["start", "--command", "cmd", "--cwd", ""],
            ["start", "--command", "cmd", "--cols", "not-a-number"],
            ["start", "--command", "cmd", "--col", "12"],
            ["start", "--command", "cmd", "--command", "other"],
            ["start", "--command", "cmd", "--rows"],
        })
        {
            Assert.Throws<ArgumentException>(() => TerminalAutomationCli.ParseRequest(invalid));
        }
    }

    [Fact]
    public void ParseRequest_PreservesTextAndRejectsUnexpectedArguments()
    {
        Assert.Equal(" ", TerminalAutomationCli.ParseRequest(["text", "session", " "]).Text);
        Assert.Equal("", TerminalAutomationCli.ParseRequest(["paste", "session", ""]).Text);
        Assert.Equal("\t", TerminalAutomationCli.ParseRequest(["wait", "session", "\t"]).Text);
        Assert.Equal(10_000, TerminalAutomationCli.ParseRequest(["wait", "session", "ready"]).TimeoutMilliseconds);
        Assert.Equal(0, TerminalAutomationCli.ParseRequest(["click", "session", "1", "2"]).Button);

        foreach (var invalid in new[]
        {
            new[] { "unknown" },
            ["list", "extra"],
            ["observe"],
            ["text", "session"],
            ["key", "session", "Enter", "Control", "symbol", "extra"],
            ["click", "session", "x", "2"],
            ["resize", "session", "80", "24", "extra"],
            ["wait", "session", "ready", "100", "extra"],
            ["stop", "extra"],
        })
        {
            Assert.Throws<ArgumentException>(() => TerminalAutomationCli.ParseRequest(invalid));
        }
    }

    [Fact]
    public async Task RunAsync_InvalidRequestReturnsErrorWithoutLaunchingHost()
    {
        var originalError = Console.Error;
        using var output = new StringWriter();
        Console.SetError(output);
        try
        {
            Assert.Equal(1, await TerminalAutomationCli.RunAsync(["start", "--command", "cmd", "--col", "12"]));
            Assert.Contains("Unknown start option '--col'", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void ParseRequest_VisualCommandsPreserveTypedRegionInputAndDefaults()
    {
        const string regionJson = """{"column":2,"row":1,"columns":3,"rows":2}""";
        const string inputJson = """{"type":"mouse","event":"press","column":2,"row":1,"button":2,"modifiers":"Control"}""";
        var region = new TerminalAutomationRegion(2, 1, 3, 2);
        var capture = TerminalAutomationCli.ParseRequest(["capture", "session", "a file with spaces.png", regionJson]);
        Assert.Equal("capture", capture.Type);
        Assert.Equal("session", capture.SessionId);
        Assert.Equal(region, capture.Region);
        Assert.Null(capture.IncludeCursor);
        var wait = TerminalAutomationCli.ParseRequest(["wait-change", "session", new string('A', 64), "123", regionJson]);
        Assert.Equal("wait-change", wait.Type);
        Assert.Equal(new string('A', 64), wait.Hash);
        Assert.Equal(123, wait.TimeoutMilliseconds);
        Assert.Equal(region, wait.Region);
        var probe = TerminalAutomationCli.ParseRequest(["probe", "session", inputJson, "456", regionJson]);
        Assert.Equal("probe", probe.Type);
        Assert.Equal(456, probe.TimeoutMilliseconds);
        Assert.Equal(region, probe.Region);
        Assert.Equal(new TerminalAutomationInput
        {
            Type = "mouse",
            Event = "press",
            Column = 2,
            Row = 1,
            Button = 2,
            Modifiers = "Control",
        }, probe.Input);
        var measure = TerminalAutomationCli.ParseRequest(["measure", "session", "500", "20", regionJson]);
        Assert.Equal("measure", measure.Type);
        Assert.Equal((500, 20), (measure.DurationMilliseconds, measure.IntervalMilliseconds));
        Assert.Equal(region, measure.Region);
        Assert.Equal(10_000, TerminalAutomationCli.ParseRequest(["wait-change", "session", "hash"]).TimeoutMilliseconds);
        Assert.Equal(10_000, TerminalAutomationCli.ParseRequest(["probe", "session", """{"type":"text","text":" "}"""]).TimeoutMilliseconds);
        var defaults = TerminalAutomationCli.ParseRequest(["measure", "session"]);
        Assert.Equal((1000, 16), (defaults.DurationMilliseconds, defaults.IntervalMilliseconds));
        Assert.Null(defaults.Region);
    }

    [Fact]
    public void ParseRequest_MouseAndKeyEventsPreservePositionsModifiersAndDefaults()
    {
        var mouse = TerminalAutomationCli.ParseRequest(["mouse", "session", "move", "2", "3", "1", "Shift,Control"]);
        Assert.Equal(("mouse", "session", "move", 2, 3, 1, "Shift,Control"),
            (mouse.Type, mouse.SessionId, mouse.Event, mouse.Column, mouse.Row, mouse.Button, mouse.Modifiers));
        var defaultMouse = TerminalAutomationCli.ParseRequest(["mouse", "session", "press", "0", "0"]);
        Assert.Equal(0, defaultMouse.Button);
        Assert.Null(defaultMouse.Modifiers);
        var key = TerminalAutomationCli.ParseRequest(["key-event", "session", "Enter", "release", "Alt"]);
        Assert.Equal(("key-event", "session", "Enter", "release", "Alt"),
            (key.Type, key.SessionId, key.Key, key.Event, key.Modifiers));
    }

    [Fact]
    public void ParseRequest_KeySymbolFollowsModifiersForKeyAndKeyEventCommands()
    {
        var key = TerminalAutomationCli.ParseRequest(["key", "session", "A", "Shift", "€"]);
        Assert.Equal(("send-key", "session", "A", "Shift", "€"),
            (key.Type, key.SessionId, key.Key, key.Modifiers, key.KeySymbol));

        var keyEvent = TerminalAutomationCli.ParseRequest(
            ["key-event", "session", "A", "press", "Alt", "€"]);
        Assert.Equal(("key-event", "session", "A", "press", "Alt", "€"),
            (keyEvent.Type, keyEvent.SessionId, keyEvent.Key, keyEvent.Event, keyEvent.Modifiers, keyEvent.KeySymbol));
    }

    [Fact]
    public void ParseRequest_VisualCommandsRejectMissingExtraAndMalformedArguments()
    {
        foreach (var args in new[]
        {
            new[] { "capture", "session" },
            ["capture", "session", "image.png", "{}", "extra"],
            ["wait-change", "session"],
            ["wait-change", "session", "hash", "nan"],
            ["probe", "session"],
            ["measure", "session", "nan"],
            ["measure", "session", "100", "nan"],
            ["mouse", "session", "press", "1"],
            ["mouse", "session", "press", "nan", "0"],
            ["mouse", "session", "press", "0", "0", "nan"],
            ["key-event", "session", "Enter"],
            ["key-event", "session", "Enter", "release", "Alt", "symbol", "extra"],
        })
        {
            Assert.Throws<ArgumentException>(() => TerminalAutomationCli.ParseRequest(args));
        }

        Assert.Throws<JsonException>(() => TerminalAutomationCli.ParseRequest(["capture", "session", "image.png", "{"]));
        Assert.Throws<JsonException>(() => TerminalAutomationCli.ParseRequest(["probe", "session", "{"]));
        Assert.Throws<ArgumentException>(() => TerminalAutomationCli.ParseRequest(["probe", "session", "null"]));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);
}
