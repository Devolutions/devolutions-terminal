using System.Text;
using Devolutions.Terminal.Automation;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Automation.Tests;

public sealed class TerminalAutomationServiceTests
{
    [Fact]
    public async Task StartAsync_ValidRequestStartsConnectionWithDimensionsAndIsListed()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);

        var info = await service.StartAsync(new("test-command", "C:\\work", 12, 4));

        Assert.True(connection.IsRunning);
        Assert.Equal("test-command", connection.StartOptions?.CommandLine);
        Assert.Equal("C:\\work", connection.StartOptions?.WorkingDirectory);
        Assert.Equal((12, 4), (connection.Columns, connection.Rows));
        Assert.Equal(info, Assert.Single(service.List()));
        Assert.True(info.IsRunning);
        Assert.Equal(12, info.Columns);
        Assert.Equal(4, info.Rows);
    }

    [Fact]
    public async Task StartAsync_InvalidRequestRejectsBeforeCreatingConnection()
    {
        var factoryCalls = 0;
        await using var service = new TerminalAutomationService(() =>
        {
            factoryCalls++;
            return new FakeConnection();
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.StartAsync(new(" ")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.StartAsync(new("test-command", Columns: 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.StartAsync(new("test-command", Columns: 501, Rows: 200)));

        Assert.Equal(0, factoryCalls);
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task StartAsync_GridAtCellLimitIsAccepted()
    {
        await using var service = new TerminalAutomationService(() => new FakeConnection());

        var session = await service.StartAsync(new("test-command", Columns: 500, Rows: 200));

        Assert.Equal((500, 200), (session.Columns, session.Rows));
    }

    [Fact]
    public async Task StartAsync_SessionLimitDisposesRejectedConnectionAndReusesFreedSlot()
    {
        var connections = new List<FakeConnection>();
        await using var service = new TerminalAutomationService(() =>
        {
            var connection = new FakeConnection();
            connections.Add(connection);
            return connection;
        });
        var sessions = new List<TerminalAutomationSessionInfo>();
        for (var index = 0; index < TerminalAutomationService.MaximumSessions; index++)
        {
            sessions.Add(await service.StartAsync(new($"command-{index}")));
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(new("over-limit")));
        Assert.Contains("at most 16", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, connections[^1].DisposeCalls);
        Assert.Equal(TerminalAutomationService.MaximumSessions, service.List().Count);

        await service.CloseAsync(sessions[0].Id);
        var replacement = await service.StartAsync(new("replacement"));

        Assert.True(replacement.IsRunning);
        Assert.Equal(TerminalAutomationService.MaximumSessions, service.List().Count);
        Assert.DoesNotContain(service.List(), session => session.Id == sessions[0].Id);
    }

    [Fact]
    public async Task Observe_ContainsCellCoordinatesStylesCursorAndNegotiatedModes()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command", Columns: 20, Rows: 3));
        connection.EmitOutput("\u001b[31mA界e\u0301\u001b[0m\u001b[?1h\u001b[?2004h\u001b[?1000h\u001b[?1006h");

        var snapshot = service.Observe(session.Id);

        Assert.Contains("A界e\u0301", snapshot.Text, StringComparison.Ordinal);
        Assert.Equal(
            [
                new TerminalAutomationCell(0, "A", 1),
                new TerminalAutomationCell(1, "界", 2),
                new TerminalAutomationCell(3, "e\u0301", 1),
            ],
            snapshot.Screen[0].Cells);
        Assert.Equal(
            new TerminalAutomationStyleRun(0, 4, CellFlags.None, TermColor.FromIndex(1), TermColor.Default),
            Assert.Single(snapshot.Screen[0].Styles));
        Assert.Equal(4, snapshot.CursorX);
        Assert.Equal(0, snapshot.CursorY);
        Assert.True(snapshot.ApplicationCursorKeys);
        Assert.True(snapshot.BracketedPaste);
        Assert.True(snapshot.MouseTracking);
        Assert.True(snapshot.SgrMouse);
    }

    [Fact]
    public async Task InputMethods_WriteExactModeAwarePayloadsToSession()
    {
        var firstConnection = new FakeConnection();
        var secondConnection = new FakeConnection();
        var connections = new Queue<FakeConnection>([firstConnection, secondConnection]);
        await using var service = new TerminalAutomationService(() => connections.Dequeue());
        var first = await service.StartAsync(new("first"));
        var second = await service.StartAsync(new("second"));

        firstConnection.EmitOutput("\u001b[?1h\u001b[?2004h\u001b[?1000h\u001b[?1006h");
        service.SendText(first.Id, "literal text");
        service.Paste(first.Id, "paste");
        service.SendKey(first.Id, "Up");
        service.Click(first.Id, 1, 1);
        service.Paste(second.Id, "plain paste");

        Assert.Equal(
            [
                "literal text",
                "\u001b[200~paste\u001b[201~",
                "\u001bOA",
                "\u001b[<0;2;2M\u001b[<0;2;2m",
            ],
            firstConnection.Writes);
        Assert.Equal(["plain paste"], secondConnection.Writes);
    }

    [Fact]
    public async Task Click_RejectsDisabledTrackingAndOutOfGridCoordinates()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command", Columns: 5, Rows: 2));

        Assert.Throws<InvalidOperationException>(() => service.Click(session.Id, 0, 0));
        connection.EmitOutput("\u001b[?1000h");
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Click(session.Id, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Click(session.Id, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Click(session.Id, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Click(session.Id, 0, 0, button: 3));

        Assert.Empty(connection.Writes);
    }

    [Fact]
    public async Task SendKey_RejectsUnknownKeysAndModifiersWithoutWriting()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command"));

        Assert.Throws<ArgumentException>(() => service.SendKey(session.Id, "not-a-key"));
        Assert.Throws<ArgumentException>(() => service.SendKey(session.Id, "Up", "not-a-modifier"));
        Assert.Throws<ArgumentException>(() => service.SendKey(session.Id, "Up", "128"));
        Assert.Empty(connection.Writes);
    }

    [Fact]
    public async Task Resize_UpdatesConnectionAndSnapshotDimensions()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command", Columns: 8, Rows: 3));

        service.Resize(session.Id, 20, 5);
        var resized = service.Observe(session.Id);

        Assert.Equal([(8, 3), (20, 5)], connection.ResizeCalls);
        Assert.Equal(20, resized.Columns);
        Assert.Equal(5, resized.Rows);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Resize(session.Id, 0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Resize(session.Id, 501, 200));
        Assert.Equal([(8, 3), (20, 5)], connection.ResizeCalls);
    }

    [Fact]
    public async Task WaitForTextAsync_MatchesCurrentAndSubsequentOutput()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command"));
        connection.EmitOutput("ready");

        var current = await service.WaitForTextAsync(session.Id, "ready", TimeSpan.FromSeconds(1));
        var pending = service.WaitForTextAsync(session.Id, "later", TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        connection.EmitOutput(" later");
        var future = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(current.Matched);
        Assert.Contains("ready", current.Snapshot.Text, StringComparison.Ordinal);
        Assert.True(future.Matched);
        Assert.Contains("ready later", future.Snapshot.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForTextAsync_ReturnsUnmatchedOnTimeout()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command"));

        var result = await service.WaitForTextAsync(
            session.Id,
            "absent",
            TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.Matched);
        Assert.All(result.Snapshot.Screen, row => Assert.True(string.IsNullOrWhiteSpace(row.Text)));
    }

    [Fact]
    public async Task WaitForTextAsync_EnforcesTimeoutBoundaries()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command"));
        connection.EmitOutput("ready");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.WaitForTextAsync(session.Id, "ready", TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.WaitForTextAsync(session.Id, "ready", TimeSpan.FromTicks(-1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.WaitForTextAsync(session.Id, "ready", TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1)));
        Assert.True((await service.WaitForTextAsync(session.Id, "ready", TimeSpan.FromTicks(1))).Matched);
        Assert.True((await service.WaitForTextAsync(session.Id, "ready", TimeSpan.FromMinutes(5))).Matched);
    }

    [Fact]
    public async Task CloseAsync_RemovesSessionAndDisposesConnection()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command"));

        await service.CloseAsync(session.Id);

        Assert.False(connection.IsRunning);
        Assert.Equal(1, connection.DisposeCalls);
        Assert.Empty(service.List());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Task.Run(() => service.Observe(session.Id)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CloseAsync(session.Id));
    }

    [Fact]
    public async Task FaultedSession_ObserveFailsWithOriginalException()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command"));
        var expected = new IOException("fake PTY failure");
        connection.EmitFault(expected);

        var error = Assert.Throws<InvalidOperationException>(() => service.Observe(session.Id));

        Assert.Same(expected, error.InnerException);
    }

    [Fact]
    public async Task ProcessExit_PreservesOutputAndCompletesUnmatchedWait()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command"));
        connection.EmitOutput("last output");
        var pending = service.WaitForTextAsync(session.Id, "not present", TimeSpan.FromSeconds(2));

        connection.EmitExit(7);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var info = Assert.Single(service.List());

        Assert.False(result.Matched);
        Assert.Contains("last output", result.Snapshot.Text, StringComparison.Ordinal);
        Assert.False(info.IsRunning);
        Assert.Equal(7, info.ExitCode);
    }

    [Fact]
    public async Task WaitForTextAsync_ObservesOutputDeliveredAfterProcessExit()
    {
        var connection = new FakeConnection { CompleteOutputOnExit = false };
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("test-command"));
        var waiting = service.WaitForTextAsync(session.Id, "final output", TimeSpan.FromSeconds(2));

        connection.EmitExit(0);
        var interval = Task.Delay(100);
        Assert.Same(interval, await Task.WhenAny(waiting, interval));
        connection.EmitOutput("final output");
        connection.CompleteOutput();

        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Matched);
        Assert.False(result.Snapshot.IsRunning);
        Assert.Equal(0, result.Snapshot.ExitCode);
    }

    [Fact]
    public async Task StartAsync_AlreadyExitedProcessPreservesFinalOutputWithoutInitialResize()
    {
        var connection = new FakeConnection { CompleteOutputOnExit = false };
        connection.OnStart = () =>
        {
            connection.EmitExit(7);
            connection.EmitOutput("final output");
            connection.CompleteOutput();
        };
        await using var service = new TerminalAutomationService(() => connection);

        var session = await service.StartAsync(new("short-lived", Columns: 20, Rows: 3));
        var snapshot = service.Observe(session.Id);

        Assert.False(session.IsRunning);
        Assert.Equal(7, session.ExitCode);
        Assert.Equal(session, Assert.Single(service.List()));
        Assert.False(snapshot.IsRunning);
        Assert.Equal(7, snapshot.ExitCode);
        Assert.Contains("final output", snapshot.Text, StringComparison.Ordinal);
        Assert.Empty(connection.ResizeCalls);
        Assert.Empty(connection.PixelResizeCalls);
        Assert.Equal(0, connection.DisposeCalls);
        var wait = await service.WaitForTextAsync(session.Id, "final output", TimeSpan.FromSeconds(1));
        Assert.True(wait.Matched);
        Assert.Equal(snapshot.Text, wait.Snapshot.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_ExitDuringInitialResizePreservesSessionAndFinalOutput(bool ioFailure)
    {
        var connection = new FakeConnection();
        Exception expected = ioFailure ? new IOException("closed PTY") : new InvalidOperationException("process exited");
        connection.OnResize = () =>
        {
            connection.EmitOutput("last output");
            connection.EmitExit(17);
            throw expected;
        };
        await using var service = new TerminalAutomationService(() => connection);

        var session = await service.StartAsync(new("short-lived", Columns: 20, Rows: 3));
        var snapshot = service.Observe(session.Id);

        Assert.False(session.IsRunning);
        Assert.Equal(17, session.ExitCode);
        Assert.Null(session.Error);
        Assert.Equal(session, Assert.Single(service.List()));
        Assert.False(snapshot.IsRunning);
        Assert.Equal(17, snapshot.ExitCode);
        Assert.Contains("last output", snapshot.Text, StringComparison.Ordinal);
        Assert.Equal((20, 3), Assert.Single(connection.ResizeCalls));
        Assert.Single(connection.PixelResizeCalls);
        Assert.Equal(0, connection.DisposeCalls);
        var wait = await service.WaitForTextAsync(session.Id, "last output", TimeSpan.FromSeconds(1));
        Assert.True(wait.Matched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_InitialResizeFailureWhileRunningPropagatesAndDisposes(bool ioFailure)
    {
        var connection = new FakeConnection();
        Exception expected = ioFailure ? new IOException("live PTY error") : new InvalidOperationException("live resize error");
        connection.OnResize = () =>
        {
            Assert.True(connection.IsRunning);
            throw expected;
        };
        await using var service = new TerminalAutomationService(() => connection);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.StartAsync(new("still-running", Columns: 20, Rows: 3)));

        Assert.Same(expected, error);
        Assert.Empty(service.List());
        Assert.Equal(1, connection.DisposeCalls);
        Assert.False(connection.IsRunning);
        Assert.Equal((20, 3), Assert.Single(connection.ResizeCalls));
        Assert.Single(connection.PixelResizeCalls);
    }

    [Fact]
    public async Task StartAsync_UnrelatedResizeFailureAfterExitStillPropagatesAndDisposes()
    {
        var connection = new FakeConnection();
        var expected = new ArgumentException("invalid resize");
        connection.OnResize = () =>
        {
            connection.EmitExit(1);
            throw expected;
        };
        await using var service = new TerminalAutomationService(() => connection);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.StartAsync(new("short-lived", Columns: 20, Rows: 3)));

        Assert.Same(expected, error);
        Assert.Empty(service.List());
        Assert.Equal(1, connection.DisposeCalls);
        Assert.Equal((20, 3), Assert.Single(connection.ResizeCalls));
    }

    internal sealed class FakeConnection : IRestartableTerminalConnection, ITerminalOutputCompletion
    {
        private readonly TaskCompletionSource _outputCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;
        public event EventHandler<Exception>? Faulted;
        public event EventHandler<TerminalExitInfo>? SessionExited;

        public bool IsRunning { get; private set; }
        public int Columns { get; private set; } = 80;
        public int Rows { get; private set; } = 30;
        public TerminalConnectionCapabilities Capabilities =>
            TerminalConnectionCapabilities.Resize | TerminalConnectionCapabilities.Restart;
        public TerminalConnectionState State { get; private set; } = TerminalConnectionState.NotConnected;
        public TerminalProcessMetadata? ProcessMetadata => null;
        public TerminalExitInfo? LastExitInfo { get; private set; }
        public Task OutputCompletion => _outputCompletion.Task;
        public bool CompleteOutputOnExit { get; init; } = true;
        public TerminalLaunchOptions? StartOptions { get; private set; }
        public List<string> Writes { get; } = [];
        public Action<string>? OnWrite { get; set; }
        public Func<CancellationToken, ValueTask>? AfterWriteAsync { get; set; }
        public Action? OnStart { get; set; }
        public Action? OnResize { get; set; }
        public List<(int Columns, int Rows)> ResizeCalls { get; } = [];
        public List<(int Columns, int Rows, int PixelWidth, int PixelHeight)> PixelResizeCalls { get; } = [];
        public int DisposeCalls { get; private set; }

        public Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartOptions = options;
            Columns = options.Columns;
            Rows = options.Rows;
            IsRunning = true;
            State = TerminalConnectionState.Connected;
            OnStart?.Invoke();
            return Task.CompletedTask;
        }

        public Task StartAsync(
            string commandLine,
            string? workingDirectory,
            int columns,
            int rows,
            CancellationToken cancellationToken = default) =>
            StartAsync(new TerminalLaunchOptions
            {
                CommandLine = commandLine,
                WorkingDirectory = workingDirectory,
                Columns = columns,
                Rows = rows,
            }, cancellationToken);

        public void Write(ReadOnlySpan<byte> data) => Write(Encoding.UTF8.GetString(data));

        public void Write(string text)
        {
            Writes.Add(text);
            OnWrite?.Invoke(text);
        }

        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(Encoding.UTF8.GetString(data.Span));
            if (AfterWriteAsync is { } afterWriteAsync)
            {
                await afterWriteAsync(cancellationToken);
            }
        }

        public void Resize(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
            ResizeCalls.Add((columns, rows));
        }

        public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
        {
            PixelResizeCalls.Add((columns, rows, pixelWidth, pixelHeight));
            Resize(columns, rows);
            OnResize?.Invoke();
        }

        public Task RestartAsync(
            TerminalLaunchOptions? options = null,
            CancellationToken cancellationToken = default) =>
            StartAsync(options ?? StartOptions ?? throw new InvalidOperationException("No start options."), cancellationToken);

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            State = TerminalConnectionState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            IsRunning = false;
            State = TerminalConnectionState.Disposed;
            _outputCompletion.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void EmitOutput(string text) =>
            OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));

        public void EmitFault(Exception exception)
        {
            IsRunning = false;
            State = TerminalConnectionState.Failed;
            Faulted?.Invoke(this, exception);
        }

        public void EmitExit(int exitCode)
        {
            IsRunning = false;
            State = TerminalConnectionState.Closed;
            var exitInfo = new TerminalExitInfo(
                null,
                exitCode,
                TerminalExitReason.ProcessExited,
                ShouldClose: false,
                DateTimeOffset.UnixEpoch);
            LastExitInfo = exitInfo;
            Exited?.Invoke(this, exitCode);
            SessionExited?.Invoke(this, exitInfo);
            if (CompleteOutputOnExit)
            {
                CompleteOutput();
            }
        }

        public void CompleteOutput() => _outputCompletion.TrySetResult();
    }
}
