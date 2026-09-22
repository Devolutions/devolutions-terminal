using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Devolutions.Terminal.Connection;

/// <summary>
/// In-process line-oriented shell used when no local PTY exists (browser WASM).
/// Speaks UTF-8 and a small command set so TermControl + the built-in VT engine
/// still have a usable session.
/// </summary>
public sealed class BrowserShellConnection : IRestartableTerminalConnection
{
    public const string ShellName = "dt-wasm";

    private const string Prompt = "\u001b[32mdt-wasm\u001b[0m:\u001b[34m~\u001b[0m$ ";
    private const string ClearSequence = "\u001b[H\u001b[2J";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Decoder _decoder = Utf8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly List<string> _history = [];
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal)
    {
        ["README.md"] =
            "Devolutions Terminal in the browser.\n" +
            "This session is an in-process shell. ConPTY, Unix PTY, Ghostty,\n" +
            "and the desktop window chrome are not available under WebAssembly.\n",
        ["help.txt"] =
            "help, echo, cat, ls, clear, date, uname, whoami, env, history, color, about, exit\n",
    };

    private CancellationTokenSource? _lifetime;
    private TerminalLaunchOptions? _lastOptions;
    private bool _escape;
    private bool _csi;
    private bool _sawCarriageReturn;
    private bool _hasStarted;
    private bool _disposed;
    private bool _pendingExit;
    private char[] _decodeBuffer = new char[128];

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;
    public event EventHandler<TerminalExitInfo>? SessionExited;
    public event EventHandler<Exception>? Faulted;

    public bool IsRunning { get; private set; }
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public TerminalConnectionCapabilities Capabilities { get; } =
        TerminalConnectionCapabilities.Resize | TerminalConnectionCapabilities.Restart;
    public TerminalConnectionState State { get; private set; } =
        TerminalConnectionState.NotConnected;
    public TerminalProcessMetadata? ProcessMetadata { get; private set; }
    public TerminalExitInfo? LastExitInfo { get; private set; }

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

    public async Task StartAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options, cancellationToken);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_hasStarted)
                {
                    throw new InvalidOperationException(
                        "The browser shell has already been started. Use RestartAsync to replace its session.");
                }
            }

            StartCore(options);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(
        TerminalLaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var restartOptions = options ?? _lastOptions
                ?? throw new InvalidOperationException("No previous browser shell launch options are available.");
            ValidateOptions(restartOptions, cancellationToken);
            StopCore(TerminalExitReason.Closed);
            cancellationToken.ThrowIfCancellationRequested();
            StartCore(restartOptions);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore(TerminalExitReason.Closed);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var pendingExit = false;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsRunning)
            {
                throw new InvalidOperationException("The browser shell has not been started.");
            }

            Consume(data);
            pendingExit = _pendingExit;
            _pendingExit = false;
        }

        if (pendingExit)
        {
            StopCore(TerminalExitReason.ProcessExited);
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0)
        {
            Write(Utf8.GetBytes(text));
        }
    }

    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Columns = ValidateDimension(columns, nameof(columns));
            Rows = ValidateDimension(rows, nameof(rows));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            StopCore(TerminalExitReason.Disposed);
            lock (_stateLock)
            {
                State = TerminalConnectionState.Disposed;
            }
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }

    private void StartCore(TerminalLaunchOptions options)
    {
        lock (_stateLock)
        {
            State = TerminalConnectionState.Connecting;
            LastExitInfo = null;
            _line.Clear();
            _history.Clear();
            _decoder.Reset();
            _escape = false;
            _csi = false;
            _sawCarriageReturn = false;
            _pendingExit = false;
            Columns = options.Columns;
            Rows = options.Rows;
            _lifetime = new CancellationTokenSource();
            ProcessMetadata = new TerminalProcessMetadata(
                Guid.NewGuid(),
                Environment.ProcessId,
                string.IsNullOrWhiteSpace(options.CommandLine) ? ShellName : options.CommandLine,
                string.IsNullOrWhiteSpace(options.WorkingDirectory) ? "/" : options.WorkingDirectory,
                DateTimeOffset.UtcNow);
            _lastOptions = options;
            _hasStarted = true;
            IsRunning = true;
            State = TerminalConnectionState.Connected;
        }

        Emit(
            "\u001b[1;36mDevolutions Terminal\u001b[0m \u001b[90m· browser wasm\u001b[0m\r\n" +
            "\u001b[90mBuilt-in VT engine. Local PTY, Ghostty, and desktop chrome are unavailable.\u001b[0m\r\n" +
            "\r\nType \u001b[1mhelp\u001b[0m for commands.\r\n\r\n");
        Emit(Prompt);
    }

    private void StopCore(TerminalExitReason reason)
    {
        TerminalProcessMetadata? metadata;
        lock (_stateLock)
        {
            if (!IsRunning && State is not TerminalConnectionState.Connecting)
            {
                return;
            }

            metadata = ProcessMetadata;
            IsRunning = false;
            State = TerminalConnectionState.Closing;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
        }

        var info = new TerminalExitInfo(
            metadata,
            reason == TerminalExitReason.ProcessExited ? 0 : null,
            reason,
            false,
            DateTimeOffset.UtcNow);
        lock (_stateLock)
        {
            LastExitInfo = info;
            State = TerminalConnectionState.Closed;
        }

        try
        {
            SessionExited?.Invoke(this, info);
            Exited?.Invoke(this, info.ExitCode ?? -1);
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    private void Consume(ReadOnlySpan<byte> data)
    {
        var charCount = _decoder.GetCharCount(data, flush: false);
        if (charCount > _decodeBuffer.Length)
        {
            _decodeBuffer = new char[charCount];
        }

        var written = _decoder.GetChars(data, _decodeBuffer, flush: false);
        for (var i = 0; i < written; i++)
        {
            HandleChar(_decodeBuffer[i]);
        }
    }

    private void HandleChar(char value)
    {
        if (_csi)
        {
            if (value is >= '@' and <= '~')
            {
                _csi = false;
                _escape = false;
            }

            return;
        }

        if (_escape)
        {
            if (value is '[' or 'O')
            {
                _csi = true;
                return;
            }

            _escape = false;
            return;
        }

        switch (value)
        {
            case '\u001b':
                _escape = true;
                _sawCarriageReturn = false;
                return;
            case '\r':
                _sawCarriageReturn = true;
                ExecuteLine();
                return;
            case '\n':
                if (!_sawCarriageReturn)
                {
                    ExecuteLine();
                }

                _sawCarriageReturn = false;
                return;
            case '\b':
            case '\u007f':
                _sawCarriageReturn = false;
                Backspace();
                return;
            case '\u0003':
                _sawCarriageReturn = false;
                _line.Clear();
                Emit("^C\r\n");
                Emit(Prompt);
                return;
            case '\u000c':
                _sawCarriageReturn = false;
                _line.Clear();
                Emit(ClearSequence);
                Emit(Prompt);
                return;
            default:
                _sawCarriageReturn = false;
                if (value < ' ' || value == '\u007f')
                {
                    return;
                }

                _line.Append(value);
                Emit(value.ToString());
                return;
        }
    }

    private void Backspace()
    {
        if (_line.Length == 0)
        {
            return;
        }

        _line.Length--;
        Emit("\b \b");
    }

    private void ExecuteLine()
    {
        var command = _line.ToString();
        _line.Clear();
        Emit("\r\n");
        if (!string.IsNullOrWhiteSpace(command))
        {
            _history.Add(command);
        }

        try
        {
            if (RunCommand(command.Trim()))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Emit($"dt-wasm: {ex.Message}\r\n");
            Faulted?.Invoke(this, ex);
        }

        Emit(Prompt);
    }

    private bool RunCommand(string command)
    {
        if (command.Length == 0)
        {
            return false;
        }

        var parts = SplitArgs(command);
        var name = parts[0];
        var args = parts.Length > 1 ? parts[1..] : [];
        switch (name)
        {
            case "help":
                Emit(
                    "Commands:\r\n" +
                    "  help              this list\r\n" +
                    "  echo [text]       print arguments\r\n" +
                    "  cat <file>        print a virtual file\r\n" +
                    "  ls                list virtual files\r\n" +
                    "  clear             clear the screen\r\n" +
                    "  date              UTC timestamp\r\n" +
                    "  uname             host identity\r\n" +
                    "  whoami            wasm user\r\n" +
                    "  env               environment snapshot\r\n" +
                    "  history           command history\r\n" +
                    "  color             ANSI color demo\r\n" +
                    "  about             build information\r\n" +
                    "  exit              close the session\r\n");
                return false;
            case "echo":
                Emit(string.Join(' ', args) + "\r\n");
                return false;
            case "cat":
                if (args.Length == 0)
                {
                    Emit("cat: missing file operand\r\n");
                    return false;
                }

                if (!_files.TryGetValue(args[0], out var contents))
                {
                    Emit($"cat: {args[0]}: No such file\r\n");
                    return false;
                }

                Emit(contents.Replace("\n", "\r\n", StringComparison.Ordinal));
                if (!contents.EndsWith('\n'))
                {
                    Emit("\r\n");
                }

                return false;
            case "ls":
                Emit(string.Join("  ", _files.Keys.Order(StringComparer.Ordinal)) + "\r\n");
                return false;
            case "clear":
                Emit(ClearSequence);
                return false;
            case "date":
                Emit(DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture) + "\r\n");
                return false;
            case "uname":
                Emit($"{ShellName} {Environment.Version} {RuntimeInformation.OSDescription}\r\n");
                return false;
            case "whoami":
                Emit("wasm-user\r\n");
                return false;
            case "env":
                Emit($"TERM=xterm-256color\r\nSHELL={ShellName}\r\nHOME=/\r\n");
                return false;
            case "history":
                for (var i = 0; i < _history.Count; i++)
                {
                    Emit($"{i + 1,4}  {_history[i]}\r\n");
                }

                return false;
            case "color":
                EmitColorDemo();
                return false;
            case "about":
                Emit(
                    "Devolutions Terminal browser host\r\n" +
                    $"  engine     built-in VT\r\n" +
                    $"  transport  {ShellName} in-process shell\r\n" +
                    $"  runtime    {Environment.Version}\r\n" +
                    $"  os         {RuntimeInformation.OSDescription}\r\n");
                return false;
            case "exit":
                Emit("logout\r\n");
                _pendingExit = true;
                return true;
            default:
                Emit($"{ShellName}: {name}: command not found\r\n");
                return false;
        }
    }

    private void EmitColorDemo()
    {
        for (var i = 0; i < 8; i++)
        {
            Emit($"\u001b[{30 + i}m██\u001b[0m");
        }

        Emit("\r\n");
        for (var i = 0; i < 8; i++)
        {
            Emit($"\u001b[{90 + i}m██\u001b[0m");
        }

        Emit("\r\n");
    }

    private void Emit(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        OutputReceived?.Invoke(this, Utf8.GetBytes(text));
    }

    private static string[] SplitArgs(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }

    private static void ValidateOptions(TerminalLaunchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        _ = ValidateDimension(options.Columns, nameof(options.Columns));
        _ = ValidateDimension(options.Rows, nameof(options.Rows));
    }

    private static int ValidateDimension(int value, string parameterName)
    {
        if (value is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Browser shell dimensions must be between 1 and {short.MaxValue}.");
        }

        return value;
    }
}
