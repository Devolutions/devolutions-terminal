using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Microsoft.Win32;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConnectionContractTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task BlockedInputSubmissionAndCloseRemainResponsive()
    {
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        connection.OutputReceived += (_, data) => { lock (output) output.AddRange(data.ToArray()); };
        await connection.StartAsync(
            "powershell.exe -NoProfile -Command \"[Console]::Write('READY'); Start-Sleep -Seconds 30\"",
            Environment.CurrentDirectory, 80, 24, TestContext.Current.CancellationToken);
        await WaitForOutputAsync(output, "READY");
        var started = Stopwatch.StartNew();
        connection.Write(new string('x', 2 * 1024 * 1024));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), "Input submission blocked.");
        await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(connection.IsRunning);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task ConPtyStartsStopped()
    {
        await using var connection = new ConPtyConnection();

        Assert.False(connection.IsRunning);
        Assert.Equal(0, connection.Columns);
        Assert.Equal(0, connection.Rows);
        Assert.Equal(TerminalConnectionState.NotConnected, connection.State);
        Assert.True(connection.Capabilities.HasFlag(TerminalConnectionCapabilities.Restart));
        Assert.False(connection.Capabilities.HasFlag(TerminalConnectionCapabilities.Elevation));
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task WriteBeforeStartFails()
    {
        await using var connection = new ConPtyConnection();

        Assert.Throws<InvalidOperationException>(() => connection.Write("input"));
    }

    [Theory(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(32768, 24)]
    [InlineData(80, 32768)]
    public async Task RejectsInvalidDimensions(int columns, int rows)
    {
        await using var connection = new ConPtyConnection();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => connection.StartAsync("cmd.exe", null, columns, rows));
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task CannotStartTwice()
    {
        await using var connection = new ConPtyConnection();
        await connection.StartAsync(EchoCommand("ready"), null, 80, 24);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.StartAsync(EchoCommand("again"), null, 80, 24));
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task CapturesUnicodeOutputAndExitCode()
    {
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);
        connection.Faulted += (_, error) => faulted.TrySetResult(error);

        await connection.StartAsync(CommandPrompt(), null, 80, 24);
        connection.Write("chcp 65001>nul\r");
        connection.Write("echo héllo\r");
        connection.Write("exit\r");
        var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exitCode);
        await WaitForOutputAsync(output, "héllo");

        Assert.False(faulted.Task.IsCompleted);
        Assert.False(connection.IsRunning);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
        Assert.NotNull(connection.ProcessMetadata);
        Assert.Equal(exitCode, connection.LastExitInfo?.ExitCode);
        Assert.Equal(TerminalExitReason.ProcessExited, connection.LastExitInfo?.Reason);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task SixelSurvivesConPtyAndDecodesToImage()
    {
        await using var connection = new ConPtyConnection();
        var engine = new TerminalEngine(80, 24);
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
                engine.Feed(bytes.Span);
            }
        };
        engine.ResponseReady += (_, bytes) => connection.Write(bytes);
        connection.Exited += (_, code) => exited.TrySetResult(code);
        connection.Faulted += (_, error) => exited.TrySetException(error);
        const string sixel = "\u001bPq\"1;1;60;6#0;2;100;0;0#0!60~\u001b\\";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("BEFORE" + sixel + "AFTER"));
        var script = $"[Console]::Write([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{payload}')))";
        var command = "powershell.exe -NoLogo -NoProfile -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        await connection.StartAsync(command, null, 80, 24, TestContext.Current.CancellationToken);

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitForOutputAsync(output, "AFTER");
        lock (output)
        {
            var text = Encoding.UTF8.GetString([.. output]);
            Assert.Contains("BEFORE", text);
            Assert.Contains(sixel, text);
            var image = Assert.Single(engine.Images);
            Assert.Equal(TerminalImageProtocol.Sixel, image.Protocol);
            Assert.NotNull(image.Sixel);
            Assert.Equal(60, image.Sixel.Width);
            Assert.Equal(6, image.Sixel.Height);
            Assert.All(image.Sixel.ToRgba32(), pixel => Assert.Equal(0xFFFF0000u, pixel));
        }
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task ResizesRunningPseudoConsole()
    {
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync(CommandPrompt(), null, 80, 24);

        connection.Resize(132, 43);
        connection.Write("mode con\r");
        connection.Write("exit\r");
        _ = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForOutputAsync(output, "132");
        await WaitForOutputAsync(output, "43");

        Assert.Equal(132, connection.Columns);
        Assert.Equal(43, connection.Rows);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task CancellationStopsProcess()
    {
        using var cancellation = new CancellationTokenSource();
        await using var connection = new ConPtyConnection();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync(LongRunningCommand(), null, 80, 24, cancellation.Token);

        await cancellation.CancelAsync();
        _ = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(connection.IsRunning);
        Assert.Equal(TerminalExitReason.Cancelled, connection.LastExitInfo?.Reason);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task AppliesEnvironmentOverrides()
    {
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);

        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = CommandPrompt(),
            Columns = 80,
            Rows = 24,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["WT_DOTNET_TEST_VALUE"] = "profile-value",
            },
        });
        connection.Write("echo %WT_DOTNET_TEST_VALUE%\r");
        connection.Write("exit\r");

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitForOutputAsync(output, "profile-value");
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void RegeneratedEnvironmentIncludesMachineAndUserPath()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        WindowsEnvironment.ApplyRegistryVariables(
            variables,
            [
                new("SystemRoot", @"C:\Windows", RegistryValueKind.String),
                new("Path", @"%SystemRoot%\System32", RegistryValueKind.ExpandString),
            ]);
        WindowsEnvironment.ApplyRegistryVariables(
            variables,
            [
                new("Path", @"C:\Users\test\AppData\Local\Programs", RegistryValueKind.String),
                new("TOOLS_HOME", @"%SystemRoot%\Tools", RegistryValueKind.ExpandString),
                new("PLAIN_TOOLS_HOME", @"%SystemRoot%\PlainTools", RegistryValueKind.String),
            ]);

        Assert.Equal(
            @"C:\Windows\System32;C:\Users\test\AppData\Local\Programs",
            variables["Path"]);
        Assert.Equal(@"C:\Windows\Tools", variables["TOOLS_HOME"]);
        Assert.Equal(@"C:\Windows\PlainTools", variables["PLAIN_TOOLS_HOME"]);
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void RegistryPathConcatenationRespectsTrailingSemicolon()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\Windows;",
        };

        WindowsEnvironment.ApplyRegistryVariables(
            variables,
            [new("Path", @"C:\Tools", RegistryValueKind.String)]);

        Assert.Equal(@"C:\Windows;C:\Tools", variables["Path"]);
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void OverridesExpandAgainstConstructedEnvironment()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\Windows\System32",
            ["SystemRoot"] = @"C:\Windows",
        };

        WindowsEnvironment.ApplyOverrides(
            variables,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Path"] = @"%PATH%;C:\tools",
                ["TOOLS_HOME"] = @"%systemroot%\Tools",
            });

        Assert.Equal(@"C:\Windows\System32;C:\tools", variables["Path"]);
        Assert.Equal(@"C:\Windows\Tools", variables["TOOLS_HOME"]);
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void OverrideExpansionKeepsTrailingSemicolonAndUnknownReferences()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\Windows;",
        };

        WindowsEnvironment.ApplyOverrides(
            variables,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Path"] = @"%PATH%C:\tools",
                ["UNKNOWN"] = @"%NOT_DEFINED%\bin",
                ["UNTERMINATED"] = "%NOT_CLOSED",
            });

        Assert.Equal(@"C:\Windows;C:\tools", variables["Path"]);
        Assert.Equal(@"%NOT_DEFINED%\bin", variables["UNKNOWN"]);
        Assert.Equal("%NOT_CLOSED", variables["UNTERMINATED"]);
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void OverrideNullDeletesAndEmptyValueIsIgnored()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["KEEP"] = "existing",
            ["DROP"] = "existing",
        };

        WindowsEnvironment.ApplyOverrides(
            variables,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["keep"] = string.Empty,
                ["drop"] = null,
                ["LITERAL"] = "%NOT_DEFINED%",
            });

        Assert.Equal("existing", variables["KEEP"]);
        Assert.False(variables.ContainsKey("DROP"));
        Assert.Equal("%NOT_DEFINED%", variables["LITERAL"]);
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void TempOverrideFallsBackToOriginalPathWhenShorteningFails()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = Path.Combine(Path.GetTempPath(), "dt missing " + Guid.NewGuid().ToString("N"));

        WindowsEnvironment.ApplyOverrides(
            variables,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["TMP"] = missing,
            });

        Assert.Equal(missing, variables["TMP"]);
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void TempOverrideUsesShortPathWhenAvailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dt short path " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "marker.txt"), "marker");
        try
        {
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            WindowsEnvironment.ApplyOverrides(
                variables,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TEMP"] = directory,
                });

            var value = variables["TEMP"];
            Assert.True(
                value.Length <= directory.Length,
                "Short path must not be longer than the original path.");
            Assert.True(
                File.Exists(Path.Combine(value, "marker.txt")),
                "Shortened TEMP must still resolve to the original directory.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void ProgramFilesMappingsMatchProcessArchitecture()
    {
        var x86 = WindowsEnvironment.GetProgramFilesMappings(false, Architecture.X86);
        Assert.Equal(
            new[] { "ProgramFiles", "CommonProgramFiles" },
            x86.Select(static mapping => mapping.VariableName));

        var x64 = WindowsEnvironment.GetProgramFilesMappings(true, Architecture.X64)
            .Select(static mapping => mapping.VariableName)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "ProgramFiles",
                "CommonProgramFiles",
                "ProgramFiles(x86)",
                "CommonProgramFiles(x86)",
                "ProgramW6432",
                "CommonProgramW6432",
            },
            x64);

        var arm64 = WindowsEnvironment.GetProgramFilesMappings(true, Architecture.Arm64);
        Assert.Contains(arm64, mapping => mapping.VariableName == "ProgramFiles(Arm)");
        Assert.Contains(arm64, mapping => mapping.ValueName == "CommonFilesDir (Arm)");
        Assert.Contains(arm64, mapping => mapping.VariableName == "ProgramW6432");
    }

    [Fact(Skip = "Windows environment regeneration is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void ReloadedEnvironmentSourcesProgramFilesFromRegistry()
    {
        const string Marker = "WT_DOTNET_NEVER_INHERITED";
        Environment.SetEnvironmentVariable(Marker, "host-value");
        try
        {
            var variables = WindowsEnvironment.Create(new TerminalLaunchOptions
            {
                CommandLine = CommandPrompt(),
                InheritEnvironment = true,
                ReloadEnvironmentVariables = true,
            });

            Assert.True(variables.ContainsKey("ProgramFiles"));
            Assert.True(variables.ContainsKey("CommonProgramFiles"));
            Assert.True(variables.ContainsKey("Path"));

            // ReloadEnvironmentVariables takes precedence over InheritEnvironment.
            Assert.False(variables.ContainsKey(Marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Marker, null);
        }
    }
    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task ConPtyStandardHandlesAreConsoleHandles()
    {
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        await connection.StartAsync(
            $"\"{powershell}\" -NoLogo -NoProfile -Command " +
            "\"Write-Output ([Console]::IsInputRedirected); " +
            "Write-Output ([Console]::IsOutputRedirected)\"",
            null,
            80,
            24);

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitForOutputAsync(output, "False");
        lock (output)
        {
            var text = Encoding.UTF8.GetString([.. output]);
            Assert.Equal(2, text.Split("False", StringSplitOptions.None).Length - 1);
        }
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task InteractivePowerShellLoadsPsReadLine()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");
        if (!File.Exists(powershell))
        {
            return;
        }

        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);

        await connection.StartAsync($"\"{powershell}\" -NoLogo -NoProfile", null, 80, 24);
        await WaitForOutputAsync(output, "PS ", TimeSpan.FromSeconds(20));
        connection.Write(
            "if (Get-Module PSReadLine) { 'PSREAD' + 'LINE_OK' } else { 'PSREADLINE_MISSING' }\r");
        await WaitForOutputAsync(output, "PSREADLINE_OK", TimeSpan.FromSeconds(20));
        connection.Write("exit\r");

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        lock (output)
        {
            Assert.DoesNotContain(
                "Cannot load PSReadLine module",
                Encoding.UTF8.GetString([.. output]),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task ConcurrentStartAndDisposeCannotPublishAfterDisposal()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var connection = new ConPtyConnection();
            var start = Task.Run(async () =>
            {
                try
                {
                    await connection.StartAsync(LongRunningCommand(), null, 80, 24);
                }
                catch (ObjectDisposedException)
                {
                }
            });
            var dispose = Task.Run(async () => await connection.DisposeAsync());

            await Task.WhenAll(start, dispose).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(connection.IsRunning);
            Assert.Equal(TerminalConnectionState.Disposed, connection.State);
        }
    }

    [Theory(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    [InlineData(TerminalCloseOnExitPolicy.Never, 0, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Graceful, 0, false, true)]
    [InlineData(TerminalCloseOnExitPolicy.Graceful, 1, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Always, 1, false, true)]
    [InlineData(TerminalCloseOnExitPolicy.Automatic, 1, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Automatic, 1, true, true)]
    public void EvaluatesCloseOnExitPolicy(
        TerminalCloseOnExitPolicy policy,
        int exitCode,
        bool isDefaultTerminalSession,
        bool expected)
    {
        Assert.Equal(
            expected,
            TerminalCloseOnExit.ShouldClose(
                policy,
                TerminalExitReason.ProcessExited,
                exitCode,
                isDefaultTerminalSession));
        Assert.False(
            TerminalCloseOnExit.ShouldClose(
                policy,
                TerminalExitReason.StartupFailure,
                exitCode,
                isDefaultTerminalSession));
    }

    [Theory(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    [InlineData(TerminalCloseOnExitPolicy.Never, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Graceful, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Always, false, true)]
    [InlineData(TerminalCloseOnExitPolicy.Automatic, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Automatic, true, true)]
    public void EvaluatesCloseOnConnectionFailure(
        TerminalCloseOnExitPolicy policy,
        bool isDefaultTerminalSession,
        bool expected)
    {
        Assert.Equal(
            expected,
            TerminalCloseOnExit.ShouldClose(
                policy,
                TerminalExitReason.ConnectionFailure,
                null,
                isDefaultTerminalSession));
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task ExitInfoCarriesProcessMetadataAndCloseDecision()
    {
        await using var connection = new ConPtyConnection();
        var exited = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionExited += (_, exit) => exited.TrySetResult(exit);

        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = ExitCommand(7),
            WorkingDirectory = Environment.CurrentDirectory,
            Columns = 80,
            Rows = 24,
            CloseOnExit = TerminalCloseOnExitPolicy.Always,
        });
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(TerminalExitReason.ProcessExited, result.Reason);
        Assert.True(result.ShouldClose);
        Assert.Equal(Environment.CurrentDirectory, result.Process?.WorkingDirectory);
        Assert.True(result.Process?.ProcessId > 0);
        Assert.Equal(connection.ProcessMetadata, result.Process);
        Assert.Equal(TerminalConnectionState.Failed, connection.State);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task RestartsExitedSessionWithNewIdentity()
    {
        await using var connection = new ConPtyConnection();
        var exits = new Queue<TaskCompletionSource<TerminalExitInfo>>();
        connection.SessionExited += (_, exit) =>
        {
            lock (exits)
            {
                if (exits.Count > 0)
                {
                    exits.Dequeue().TrySetResult(exit);
                }
            }
        };

        var firstExit = EnqueueExit(exits);
        await connection.StartAsync(EchoCommand("first"), null, 80, 24);
        var first = await firstExit.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondExit = EnqueueExit(exits);
        await connection.RestartAsync(new TerminalLaunchOptions
        {
            CommandLine = EchoCommand("second"),
            Columns = 100,
            Rows = 40,
        });
        var second = await secondExit.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(first.Process?.SessionId, second.Process?.SessionId);
        Assert.NotEqual(first.Process?.ProcessId, second.Process?.ProcessId);
        Assert.Equal(100, connection.Columns);
        Assert.Equal(40, connection.Rows);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task CloseDoesNotRequestPolicyDrivenTabClose()
    {
        await using var connection = new ConPtyConnection();
        var exited = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionExited += (_, exit) => exited.TrySetResult(exit);
        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = LongRunningCommand(),
            Columns = 80,
            Rows = 24,
            CloseOnExit = TerminalCloseOnExitPolicy.Always,
        });

        await connection.CloseAsync();
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TerminalExitReason.Closed, result.Reason);
        Assert.False(result.ShouldClose);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task CanRetryAfterStartupFailure()
    {
        await using var connection = new ConPtyConnection();

        await Assert.ThrowsAnyAsync<Exception>(
            () => connection.StartAsync(
                "\"Z:\\path-that-does-not-exist\\missing.exe\"",
                null,
                80,
                24));
        Assert.Equal(TerminalExitReason.StartupFailure, connection.LastExitInfo?.Reason);
        Assert.False(connection.LastExitInfo?.ShouldClose);

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync(EchoCommand("recovered"), null, 80, 24);

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task RepeatedSessionsDoNotLeakProcessHandles()
    {
        await RunShortSessionAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var currentProcess = Process.GetCurrentProcess();
        var baseline = currentProcess.HandleCount;

        for (var iteration = 0; iteration < 30; iteration++)
        {
            await RunShortSessionAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        currentProcess.Refresh();
        var final = currentProcess.HandleCount;
        Assert.InRange(final - baseline, int.MinValue, 12);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task ExitedSessionsReleaseHandlesBeforeConnectionDisposal()
    {
        await RunShortSessionAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var currentProcess = Process.GetCurrentProcess();
        var baseline = currentProcess.HandleCount;
        var connections = new List<ConPtyConnection>();

        try
        {
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var connection = new ConPtyConnection();
                connections.Add(connection);
                var exited = new TaskCompletionSource<int>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                connection.Exited += (_, code) => exited.TrySetResult(code);
                await connection.StartAsync(ExitCommand(0), null, 80, 24);
                Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            int handleCount;
            do
            {
                await Task.Delay(25);
                currentProcess.Refresh();
                handleCount = currentProcess.HandleCount;
            }
            while (handleCount > baseline + 12 && DateTime.UtcNow < deadline);

            Assert.InRange(handleCount - baseline, int.MinValue, 12);
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    [Theory(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\src", "Ubuntu", "/home/user/src")]
    [InlineData(@"\\wsl$\Debian", "Debian", "/")]
    public void ParsesWslUncPaths(string path, string distribution, string linuxPath)
    {
        Assert.True(WslPathTranslator.TryParseWindowsPath(path, out var result));
        Assert.Equal(distribution, result?.Distribution);
        Assert.Equal(linuxPath, result?.LinuxPath);
    }

    [Theory(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    [InlineData(@"C:\Users\name\source", "/mnt/c/Users/name/source")]
    [InlineData(@"D:\", "/mnt/d/")]
    [InlineData("/home/name", "/home/name")]
    public void TranslatesPathsForWsl(string path, string expected)
    {
        Assert.True(WslPathTranslator.TryToLinuxPath(path, out var linuxPath));
        Assert.Equal(expected, linuxPath);
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public void BuildsWslCommandLineWithTranslatedWorkingDirectory()
    {
        var commandLine = WslPathTranslator.BuildCommandLine(
            "Ubuntu",
            @"C:\Users\name\source",
            "bash -l");

        Assert.Equal(
            "wsl.exe --distribution \"Ubuntu\" --cd \"/mnt/c/Users/name/source\" --exec bash -l",
            commandLine);
        Assert.Equal(
            @"\\wsl.localhost\Ubuntu\home\name",
            WslPathTranslator.ToWindowsPath("Ubuntu", "/home/name"));
    }

    private static string EchoCommand(string value)
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /s /c \"echo {value}\"";
    }

    private static string CommandPrompt()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /q";
    }

    private static string LongRunningCommand()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /s /c \"ping 127.0.0.1 -n 30 > nul\"";
    }

    private static string ExitCommand(int exitCode)
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /s /c \"exit {exitCode}\"";
    }

    private static TaskCompletionSource<TerminalExitInfo> EnqueueExit(
        Queue<TaskCompletionSource<TerminalExitInfo>> exits)
    {
        var completion = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (exits)
        {
            exits.Enqueue(completion);
        }

        return completion;
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task RawModeBurstReachesWslReader()
    {
        var distro = FindWslDistro();
        if (distro is null)
        {
            Assert.Skip("No WSL distribution is installed.");
        }

        const string script = """
            import os, sys, tty, termios, select
            fd = sys.stdin.fileno()
            old = termios.tcgetattr(fd)
            tty.setraw(fd)
            sys.stdout.write("READY\n")
            sys.stdout.flush()
            data = bytearray()
            while len(data) < 7:
                ready, _, _ = select.select([fd], [], [], 3.0)
                if not ready:
                    break
                chunk = os.read(fd, 64)
                if not chunk:
                    break
                data.extend(chunk)
            termios.tcsetattr(fd, termios.TCSADRAIN, old)
            sys.stdout.write("BYTES " + data.hex() + "\n")
            sys.stdout.flush()
            """;
        InstallWslScript(distro, "/tmp/dterm-pace.py", script);

        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        await connection.StartAsync(
            $"wsl.exe -d {distro} -- python3 -u /tmp/dterm-pace.py",
            null,
            80,
            24);
        await WaitForOutputAsync(output, "READY", TimeSpan.FromSeconds(20));

        // No gap between submissions. ConPTY used to keep only the first byte
        // while the Linux reader was in raw/no-echo mode, which truncated sudo
        // passwords typed at full speed.
        foreach (var value in "secret\r"u8)
        {
            connection.Write([(byte)value]);
        }

        await WaitForOutputAsync(output, "BYTES 7365637265740d", TimeSpan.FromSeconds(5));
    }

    [Fact(Skip = "ConPTY is Windows-only.", SkipUnless = nameof(IsWindows))]
    public async Task ConcurrentWritesNeverSplitSequence()
    {
        // Pins the ConPTY injection lesson: query responses (engine, PTY thread) and
        // key input (UI thread) race on the input pipe — every write must reach the
        // child as one indivisible sequence. The child answers 'ok' for a uniform
        // payload line and 'BAD' for a byte-interleaved one.
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };

        await connection.StartAsync(
            "powershell.exe -NoProfile -NonInteractive -Command \"" +
            "while (($line = [Console]::In.ReadLine()) -ne $null) { " +
            "if ($line -match '^(A+|B+)$') { [Console]::Out.Write('ok`n') } " +
            "else { [Console]::Out.Write('BAD:' + $line + '`n') } " +
            "[Console]::Out.Flush() }\"",
            null,
            200,
            50);

        var payloadA = new string('A', 100);
        var payloadB = new string('B', 100);
        const int writesPerThread = 100;
        await Task.WhenAll(
            Task.Run(() =>
            {
                for (var i = 0; i < writesPerThread; i++)
                {
                    connection.Write(payloadA + "\r");
                }
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < writesPerThread; i++)
                {
                    connection.Write(payloadB + "\r");
                }
            }));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var okCount = 0;
        var sawBad = false;
        while (DateTime.UtcNow < deadline)
        {
            string text;
            lock (output)
            {
                text = Encoding.UTF8.GetString([.. output]);
            }

            okCount = Regex.Matches(text, "ok", RegexOptions.None, TimeSpan.FromSeconds(1)).Count;
            sawBad = text.Contains("BAD:", StringComparison.Ordinal);
            if (sawBad || okCount >= writesPerThread * 2)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.False(sawBad, "child received a byte-interleaved line");
        Assert.Equal(writesPerThread * 2, okCount);
    }

    private static async Task RunShortSessionAsync()
    {
        var connection = new ConPtyConnection();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync(ExitCommand(0), null, 80, 24);
        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await connection.DisposeAsync();
    }

    private static string? FindWslDistro()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("wsl.exe", "-l -q")
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Unicode,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (!process.WaitForExit(2_000))
            {
                return null;
            }

            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim().TrimStart('\uFEFF');
                if (name.Length > 0 && !name.Contains(' ') && !name.Contains('\t') && !name.Contains('"'))
                {
                    return name;
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static void InstallWslScript(string distro, string path, string script)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add(distro);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("tee");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("wsl.exe did not start.");
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(15_000), "Installing the WSL reader timed out.");
        Assert.Equal(0, process.ExitCode);
    }

    private static Task WaitForOutputAsync(List<byte> output, string expected) =>
        WaitForOutputAsync(output, expected, TimeSpan.FromSeconds(5));

    private static async Task WaitForOutputAsync(List<byte> output, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
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
