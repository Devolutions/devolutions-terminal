namespace Devolutions.Terminal.Connection;

public interface ITerminalConnection : IAsyncDisposable
{
    event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    event EventHandler<int>? Exited;
    event EventHandler<Exception>? Faulted;

    bool IsRunning { get; }
    int Columns { get; }
    int Rows { get; }

    Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default);
    Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default);
    void Write(ReadOnlySpan<byte> data);
    void Write(string text);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    void Resize(int columns, int rows);
    void Resize(int columns, int rows, int pixelWidth, int pixelHeight) =>
        Resize(columns, rows);

    // Clears screen state the connection keeps on its side (ConPTY has its own buffer)
    // after the terminal clears its buffer. keepCursorRow moves the cursor's row to the
    // top instead of erasing it. Plain byte-stream connections have nothing to clear.
    void ClearBuffer(bool keepCursorRow)
    {
    }
}

public interface IRestartableTerminalConnection : ITerminalConnection
{
    event EventHandler<TerminalExitInfo>? SessionExited;

    TerminalConnectionCapabilities Capabilities { get; }
    TerminalConnectionState State { get; }
    TerminalProcessMetadata? ProcessMetadata { get; }
    TerminalExitInfo? LastExitInfo { get; }

    Task RestartAsync(TerminalLaunchOptions? options = null, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}
