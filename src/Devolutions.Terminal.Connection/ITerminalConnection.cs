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

    // Bytes the terminal sends on its own rather than because the user typed: replies to
    // the program's queries (DA, DSR, DECRQM, OSC colour reports) and mode reports. A
    // connection that shares one program between several viewers or forwards input to a
    // host that tells the two apart (and may answer some queries itself) overrides this;
    // everyone else writes them like any other input.
    void WriteResponse(ReadOnlySpan<byte> data) => Write(data);

    // Keyboard focus moved to or away from the terminal. A connection that carries focus
    // itself (to a host that reports it to the program when the program asked for it)
    // returns true, and the terminal then writes no CSI I / CSI O of its own. Called on
    // every change, whether or not the program enabled focus reporting.
    bool ReportFocus(bool focused) => false;
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

public interface ITerminalOutputCompletion
{
    // Completes when the current session has delivered its remaining output.
    Task OutputCompletion { get; }
}
