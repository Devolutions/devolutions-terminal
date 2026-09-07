using System.Runtime.Versioning;
using System.Text;
using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LinuxPtyConnectionTests
{
    public static bool IsUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    [Fact(Skip = "Unix PTY host is Linux/macOS-only.", SkipUnless = nameof(IsUnix))]
    public async Task BlockedInputStillRelaysOutputAndCloseHasDeadline()
    {
        await using var connection = new LinuxPtyConnection();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responsive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var faults = new List<Exception>();
        connection.OutputReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                var text = output.ToString();
                if (text.Contains("READY", StringComparison.Ordinal)) ready.TrySetResult();
                if (text.Contains("RESPONSIVE", StringComparison.Ordinal)) responsive.TrySetResult();
            }
        };
        connection.Faulted += (_, error) => { lock (faults) faults.Add(error); };
        await connection.StartAsync(
            "stty -echo -icanon; printf READY; sleep 1; printf RESPONSIVE; sleep 30",
            Environment.CurrentDirectory, 80, 24, TestContext.Current.CancellationToken);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started = System.Diagnostics.Stopwatch.StartNew();
        connection.Write(new byte[2 * 1024 * 1024]);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), "Input submission blocked.");
        await responsive.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(connection.IsRunning);
        lock (faults)
        {
            Assert.NotEmpty(faults);
        }
    }

    [Fact(Skip = "Unix PTY host is Linux/macOS-only.", SkipUnless = nameof(IsUnix))]
    public async Task CancellingBlockedFrameStopsSessionWithoutWaitingForReader()
    {
        await using var connection = new LinuxPtyConnection();
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        connection.OutputReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                if (output.ToString().Contains("READY", StringComparison.Ordinal)) ready.TrySetResult();
            }
        };
        await connection.StartAsync(
            "stty -echo -icanon; printf READY; sleep 30",
            Environment.CurrentDirectory, 80, 24, TestContext.Current.CancellationToken);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = connection.WriteAsync(new byte[2 * 1024 * 1024], cancellation.Token).AsTask();
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(error is OperationCanceledException or IOException, error?.ToString());
        await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(connection.IsRunning);
    }

    [Fact(Skip = "Unix PTY host is Linux/macOS-only.", SkipUnless = nameof(IsUnix))]
    public async Task LargeInputFramesPreserveBytesAndFollowingResize()
    {
        await using var connection = new LinuxPtyConnection();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        connection.OutputReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                var text = output.ToString();
                if (text.Contains("READY", StringComparison.Ordinal)) ready.TrySetResult();
                if (text.Contains("1048576", StringComparison.Ordinal) &&
                    text.Contains("40 100", StringComparison.Ordinal)) completed.TrySetResult();
            }
        };
        await connection.StartAsync(
            "stty raw -echo; printf READY; head -c 1048576 | wc -c; sleep 1; stty size; sleep 30",
            Environment.CurrentDirectory, 80, 24, TestContext.Current.CancellationToken);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.WriteAsync(new byte[1024 * 1024]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        connection.Resize(100, 40);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RealPtySupportsInputResizeAndExit()
    {
        if (!IsUnix)
        {
            return;
        }

        await using var connection = new LinuxPtyConnection();
        var output = new StringBuilder();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                changed.TrySetResult();
            }
        };
        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = "/bin/bash --noprofile --norc",
            WorkingDirectory = "/tmp",
            Columns = 80,
            Rows = 24,
            CloseOnExit = TerminalCloseOnExitPolicy.Never,
        }, TestContext.Current.CancellationToken);

        connection.Write("printf 'LINUX_PTY_OK:%s\\n' \"$PWD\"\r");
        await WaitForAsync(
            () =>
            {
                var snapshot = Snapshot();
                return snapshot.Contains("LINUX_PTY_OK:/tmp", StringComparison.Ordinal) ||
                       snapshot.Contains("LINUX_PTY_OK:/private/tmp", StringComparison.Ordinal);
            },
            changed,
            TestContext.Current.CancellationToken);

        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Resize(100, 40);
        connection.Write("stty size\r");
        await WaitForAsync(
            () => Snapshot().Contains("40 100", StringComparison.Ordinal),
            changed,
            TestContext.Current.CancellationToken);

        Assert.True(connection.IsRunning);
        Assert.Equal(100, connection.Columns);
        Assert.Equal(40, connection.Rows);
        await connection.CloseAsync(TestContext.Current.CancellationToken);
        Assert.False(connection.IsRunning);

        string Snapshot()
        {
            lock (output)
            {
                return output.ToString();
            }
        }
    }

    [Fact(Skip = "Unix PTY host is Linux/macOS-only.", SkipUnless = nameof(IsUnix))]
    public async Task ReportsExitCodeAndRestartsWithNewSession()
    {
        await using var connection = new LinuxPtyConnection();
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
        await connection.StartAsync(
            "printf 'FIRST_SESSION\\n'",
            "/tmp",
            80,
            24,
            TestContext.Current.CancellationToken);
        var first = await firstExit.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        var secondExit = EnqueueExit(exits);
        await connection.RestartAsync(
            new TerminalLaunchOptions
            {
                CommandLine = "exit 7",
                WorkingDirectory = "/tmp",
                Columns = 100,
                Rows = 40,
            },
            TestContext.Current.CancellationToken);
        var second = await secondExit.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(7, second.ExitCode);
        Assert.NotEqual(first.Process?.SessionId, second.Process?.SessionId);
        Assert.Equal(TerminalExitReason.ProcessExited, second.Reason);
        Assert.Equal(TerminalConnectionState.Failed, connection.State);
        Assert.Equal(100, connection.Columns);
        Assert.Equal(40, connection.Rows);
    }

    [Fact(Skip = "Unix PTY host is Linux/macOS-only.", SkipUnless = nameof(IsUnix))]
    public async Task LaunchCancellationTerminatesProcessTree()
    {
        using var cancellation = new CancellationTokenSource();
        await using var connection = new LinuxPtyConnection();
        var exited = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionExited += (_, exit) => exited.TrySetResult(exit);

        await connection.StartAsync(
            "sleep 30",
            "/tmp",
            80,
            24,
            cancellation.Token);
        await cancellation.CancelAsync();
        var result = await exited.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(TerminalExitReason.Cancelled, result.Reason);
        Assert.False(connection.IsRunning);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
    }

    [Theory(Skip = "Unix PTY host is Linux/macOS-only.", SkipUnless = nameof(IsUnix))]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(65536, 24)]
    [InlineData(80, 65536)]
    public async Task RejectsInvalidDimensions(int columns, int rows)
    {
        await using var connection = new LinuxPtyConnection();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => connection.StartAsync(
                "true",
                "/tmp",
                columns,
                rows,
                TestContext.Current.CancellationToken));
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

    private static async Task WaitForAsync(
        Func<bool> condition,
        TaskCompletionSource changed,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for Linux PTY output.");
            }

            await Task.WhenAny(
                changed.Task,
                Task.Delay(100, cancellationToken)).ConfigureAwait(false);
        }
    }
}
