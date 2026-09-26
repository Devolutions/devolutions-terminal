using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class TermControlResizeTests
{
    [AvaloniaFact]
    public async Task StartAsyncDiscardsResizeFromBeforeStart()
    {
        var connection = new FakePtyConnection();
        var control = CreateControl(connection);
        try
        {
            ArrangeControl(control, 800, 600);
            Assert.True(control.Bounds.Width > 0);
            var arrangedColumns = control.Engine.Columns;
            var arrangedRows = control.Engine.Rows;

            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 62, 19);
            Assert.True(arrangedColumns != 62 || arrangedRows != 19);
            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(62, control.Engine.Columns);
            Assert.Equal(19, control.Engine.Rows);
            Assert.Equal(62, connection.Columns);
            Assert.Equal(19, connection.Rows);
            Assert.Equal(0, connection.ResizeCount);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task LayoutDuringStartFlushesLatestResizeAfterConnectionStarts()
    {
        var connection = new FakePtyConnection { StartGate = NewGate() };
        var control = CreateControl(connection);
        try
        {
            ArrangeControl(control, 800, 600);
            var start = control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 62, 19);
            await connection.StartEntered.Task;

            ArrangeControl(control, 920, 600);
            var firstColumns = control.Engine.Columns;
            ArrangeControl(control, 1040, 700);
            var expectedColumns = control.Engine.Columns;
            var expectedRows = control.Engine.Rows;
            Assert.NotEqual(firstColumns, expectedColumns);

            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, connection.ResizeCount);

            connection.StartGate.SetResult();
            await start;

            Assert.Equal(expectedColumns, control.Engine.Columns);
            Assert.Equal(expectedRows, control.Engine.Rows);
            Assert.Equal(expectedColumns, connection.Columns);
            Assert.Equal(expectedRows, connection.Rows);
            Assert.Equal(1, connection.ResizeCount);
            Assert.Equal(expectedColumns * CellPixelWidth(control), connection.PixelWidth);
            Assert.Equal(expectedRows * CellPixelHeight(control), connection.PixelHeight);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task LayoutDuringRestartFlushesLatestResizeAfterConnectionRestarts()
    {
        var connection = new FakePtyConnection { RestartGate = NewGate() };
        var control = CreateControl(connection);
        try
        {
            ArrangeControl(control, 800, 600);
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 62, 19);
            var restart = control.RestartAsync();
            await connection.RestartEntered.Task;

            ArrangeControl(control, 1040, 700);
            var expectedColumns = control.Engine.Columns;
            var expectedRows = control.Engine.Rows;
            Assert.True(expectedColumns != 62 || expectedRows != 19);

            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, connection.ResizeCount);

            connection.RestartGate.SetResult();
            await restart;

            Assert.Equal(expectedColumns, connection.Columns);
            Assert.Equal(expectedRows, connection.Rows);
            Assert.Equal(1, connection.ResizeCount);
            Assert.Equal(expectedColumns * CellPixelWidth(control), connection.PixelWidth);
            Assert.Equal(expectedRows * CellPixelHeight(control), connection.PixelHeight);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task RestartRestoresCurrentGridAfterConnectionReusesLaunchSize()
    {
        var connection = new FakePtyConnection();
        var control = CreateControl(connection);
        try
        {
            ArrangeControl(control, 800, 600);
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 62, 19);

            ArrangeControl(control, 1040, 600);
            var expectedColumns = control.Engine.Columns;
            Assert.NotEqual(62, expectedColumns);
            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expectedColumns, connection.Columns);
            var resizeCount = connection.ResizeCount;

            await control.RestartAsync();

            Assert.Equal(expectedColumns, control.Engine.Columns);
            Assert.Equal(expectedColumns, connection.Columns);
            Assert.Equal(control.Engine.Rows, connection.Rows);
            Assert.Equal(resizeCount + 1, connection.ResizeCount);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task FailedStartDiscardsPendingResize()
    {
        var connection = new FakePtyConnection
        {
            StartGate = NewGate(),
            StartFailure = new InvalidOperationException("start failed"),
        };
        var control = CreateControl(connection);
        try
        {
            ArrangeControl(control, 800, 600);
            var start = control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 62, 19);
            await connection.StartEntered.Task;

            ArrangeControl(control, 1040, 600);
            Assert.NotEqual(62, control.Engine.Columns);

            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();
            connection.StartGate.SetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => start);
            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, connection.ResizeCount);
            Assert.True(connection.Disposed);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    [AvaloniaFact]
    public async Task CloseCancelsPendingResize()
    {
        var connection = new FakePtyConnection();
        var control = CreateControl(connection);
        try
        {
            ArrangeControl(control, 800, 600);
            await control.StartAsync(new ProfileSettings { Commandline = "cmd.exe" }, 62, 19);
            ArrangeControl(control, 1040, 600);
            Assert.NotEqual(62, control.Engine.Columns);

            await control.CloseAsync();
            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, connection.ResizeCount);
            Assert.True(connection.Disposed);
        }
        finally
        {
            if (!connection.Disposed)
            {
                await control.CloseAsync();
            }
        }
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int CellPixelWidth(TermControl control) =>
        checked((int)Math.Max(1, Math.Round(control.CellSize.Width)));

    private static int CellPixelHeight(TermControl control) =>
        checked((int)Math.Max(1, Math.Round(control.CellSize.Height)));

    private static TermControl CreateControl(FakePtyConnection connection) =>
        new() { ConnectionFactory = _ => connection };

    private static void ArrangeControl(TermControl control, double width, double height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
    }

    private sealed class FakePtyConnection : IRestartableTerminalConnection
    {
#pragma warning disable CS0067
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public event EventHandler<int>? Exited;
        public event EventHandler<Exception>? Faulted;
        public event EventHandler<TerminalExitInfo>? SessionExited;
#pragma warning restore CS0067

        private TerminalLaunchOptions? _launchOptions;

        public TaskCompletionSource? StartGate { get; init; }
        public TaskCompletionSource? RestartGate { get; init; }
        public TaskCompletionSource StartEntered { get; } = NewGate();
        public TaskCompletionSource RestartEntered { get; } = NewGate();
        public Exception? StartFailure { get; init; }
        public bool Disposed { get; private set; }
        public bool IsRunning { get; private set; }
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }
        public int ResizeCount { get; private set; }
        public TerminalConnectionCapabilities Capabilities =>
            TerminalConnectionCapabilities.Resize | TerminalConnectionCapabilities.Restart;
        public TerminalConnectionState State { get; private set; }
        public TerminalProcessMetadata? ProcessMetadata => null;
        public TerminalExitInfo? LastExitInfo => null;

        public async Task StartAsync(
            TerminalLaunchOptions options,
            CancellationToken cancellationToken = default)
        {
            StartEntered.TrySetResult();
            if (StartGate is not null)
            {
                await StartGate.Task;
            }

            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            _launchOptions = options;
            Columns = options.Columns;
            Rows = options.Rows;
            IsRunning = true;
            State = TerminalConnectionState.Connected;
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

        public void Write(ReadOnlySpan<byte> data) { }
        public void Write(string text) { }
        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public void Resize(int columns, int rows) => Resize(columns, rows, 0, 0);

        public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
        {
            Assert.True(IsRunning);
            Columns = columns;
            Rows = rows;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            ResizeCount++;
        }

        public async Task RestartAsync(
            TerminalLaunchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            RestartEntered.TrySetResult();
            if (RestartGate is not null)
            {
                await RestartGate.Task;
            }

            var launch = options ?? _launchOptions
                ?? throw new InvalidOperationException("No prior launch.");
            Columns = launch.Columns;
            Rows = launch.Rows;
            PixelWidth = 0;
            PixelHeight = 0;
            IsRunning = true;
            State = TerminalConnectionState.Connected;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            State = TerminalConnectionState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsRunning = false;
            State = TerminalConnectionState.Disposed;
            return ValueTask.CompletedTask;
        }
    }
}
