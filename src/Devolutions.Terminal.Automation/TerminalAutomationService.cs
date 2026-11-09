using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Input;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;

namespace Devolutions.Terminal.Automation;

public sealed partial class TerminalAutomationService : IAsyncDisposable
{
    public const int MaximumSessions = 16;
    public const int MaximumCells = 100_000;

    private readonly ConcurrentDictionary<string, AutomationSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly Func<IRestartableTerminalConnection> _connectionFactory;
    private readonly object _sessionsGate = new();
    private bool _disposed;

    public TerminalAutomationService(Func<IRestartableTerminalConnection>? connectionFactory = null)
    {
        _connectionFactory = connectionFactory ?? CreatePlatformConnection;
    }

    public async Task<TerminalAutomationSessionInfo> StartAsync(
        TerminalAutomationStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CommandLine))
        {
            throw new ArgumentException("A command line is required.", nameof(request));
        }

        var (columns, rows) = ValidateDimensions(request.Columns, request.Rows);
        if (request.EnvironmentVariables?.Count > 128)
        {
            throw new ArgumentException("At most 128 environment variables may be set.", nameof(request));
        }

        var connection = _connectionFactory();
        var engine = new TerminalEngine(columns, rows);
        AutomationSession session;
        try
        {
            session = new AutomationSession(Guid.NewGuid().ToString("N"), request.CommandLine, connection, engine);
        }
        catch
        {
            engine.Dispose();
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        Exception? reservationError = null;
        lock (_sessionsGate)
        {
            if (_disposed)
            {
                reservationError = new ObjectDisposedException(nameof(TerminalAutomationService));
            }
            else if (_sessions.Count >= MaximumSessions)
            {
                reservationError = new InvalidOperationException(
                    $"The automation host supports at most {MaximumSessions} concurrent sessions.");
            }
            else if (!_sessions.TryAdd(session.Id, session))
            {
                reservationError = new InvalidOperationException("Failed to allocate a unique automation session.");
            }
        }

        if (reservationError is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw reservationError;
        }

        try
        {
            await session.StartAsync(
                new TerminalLaunchOptions
                {
                    CommandLine = request.CommandLine,
                    WorkingDirectory = request.WorkingDirectory,
                    Columns = columns,
                    Rows = rows,
                    EnvironmentVariables = request.EnvironmentVariables
                        ?? new Dictionary<string, string?>(),
                    InheritEnvironment = true,
                    CloseOnExit = TerminalCloseOnExitPolicy.Never,
                },
                cancellationToken).ConfigureAwait(false);
            return session.GetInfo();
        }
        catch
        {
            _sessions.TryRemove(session.Id, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IReadOnlyList<TerminalAutomationSessionInfo> List() =>
        _sessions.Values
            .OrderBy(static session => session.Id, StringComparer.Ordinal)
            .Select(static session => session.GetInfo())
            .ToArray();

    public TerminalAutomationSnapshot Observe(string id) => GetSession(id).Observe();

    public void SendText(string id, string text) => GetSession(id).SendText(text);

    public void Paste(string id, string text) => GetSession(id).Paste(text);

    public void SendKey(string id, string key, string? modifiers = null, string? keySymbol = null) =>
        GetSession(id).SendKey(key, modifiers, keySymbol);

    public void Click(string id, int column, int row, int button = 0)
    {
        if (button is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(button), "Mouse button must be 0 (left), 1 (middle), or 2 (right).");
        }

        GetSession(id).Click(column, row, button);
    }

    public void Resize(string id, int columns, int rows) =>
        GetSession(id).Resize(columns, rows);

    public Task<TerminalAutomationWaitResult> WaitForTextAsync(
        string id,
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        GetSession(id).WaitForTextAsync(text, timeout, cancellationToken);

    public async Task CloseAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            throw new KeyNotFoundException($"Automation session '{id}' was not found.");
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        string[] sessionIds;
        lock (_sessionsGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessionIds = _sessions.Keys.ToArray();
        }

        foreach (var id in sessionIds)
        {
            if (_sessions.TryRemove(id, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private AutomationSession GetSession(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_sessions.TryGetValue(id, out var session))
        {
            throw new KeyNotFoundException($"Automation session '{id}' was not found.");
        }

        return session;
    }

    private static (int Columns, int Rows) ValidateDimensions(int columns, int rows)
    {
        columns = HostPtyProtocol.ValidateDimension(columns, nameof(columns));
        rows = HostPtyProtocol.ValidateDimension(rows, nameof(rows));
        if ((long)columns * rows > MaximumCells)
        {
            throw new ArgumentOutOfRangeException(
                nameof(columns),
                $"The terminal grid must not exceed {MaximumCells} cells.");
        }

        return (columns, rows);
    }

    private static IRestartableTerminalConnection CreatePlatformConnection()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsConnection();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return CreateUnixConnection();
        }

        throw new PlatformNotSupportedException("Headless terminal automation requires Windows, Linux, or macOS.");
    }

    [SupportedOSPlatform("windows")]
    private static IRestartableTerminalConnection CreateWindowsConnection() => new ConPtyConnection();

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static IRestartableTerminalConnection CreateUnixConnection() => new LinuxPtyConnection();

    private sealed partial class AutomationSession : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly IRestartableTerminalConnection _connection;
        private readonly ITerminalEngine _engine;
        private TaskCompletionSource _changed = NewSignal();
        private long _revision;
        private bool _disposed;
        private bool _exited;
        private bool _outputDrained;
        private int? _exitCode;
        private Exception? _fault;
        private readonly AutomationVisualRenderer _visuals = new();
        private readonly Queue<TerminalEngineDiagnostic> _diagnostics = new();
        private long _outputBytes;
        private long _parseTicks;
        private int _pressedMouseButton = -1;

        public AutomationSession(
            string id,
            string commandLine,
            IRestartableTerminalConnection connection,
            ITerminalEngine engine)
        {
            Id = id;
            CommandLine = commandLine;
            _connection = connection;
            _engine = engine;
            _engine.Resize(engine.Columns, engine.Rows,
                Math.Max(1, Math.Round(_visuals.CellSize.Width)), Math.Max(1, Math.Round(_visuals.CellSize.Height)));
            _outputDrained = connection is not ITerminalOutputCompletion;
            _connection.OutputReceived += OnOutput;
            _connection.SessionExited += OnExited;
            _connection.Faulted += OnFaulted;
            _engine.ResponseReady += OnResponse;
            _engine.Diagnostic += OnDiagnostic;
        }

        public string Id { get; }
        public string CommandLine { get; }

        public async Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken)
        {
            await _connection.StartAsync(options, cancellationToken).ConfigureAwait(false);
            try
            {
                if (_connection.IsRunning)
                {
                    _connection.Resize(options.Columns, options.Rows,
                        (int)(options.Columns * Math.Max(1, Math.Round(_visuals.CellSize.Width))),
                        (int)(options.Rows * Math.Max(1, Math.Round(_visuals.CellSize.Height))));
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException && !_connection.IsRunning)
            {
                // A short-lived process can exit between StartAsync and the initial pixel-size update.
                Trace.TraceInformation("Automation session {0} exited before accepting its initial pixel size: {1}",
                    Id, exception.Message);
            }
            if (_connection is ITerminalOutputCompletion outputCompletion)
            {
                _ = ObserveOutputCompletionAsync(outputCompletion.OutputCompletion);
            }
        }

        public TerminalAutomationSessionInfo GetInfo()
        {
            lock (_gate)
            {
                return new(
                    Id,
                    CommandLine,
                    _connection.IsRunning,
                    _engine.Columns,
                    _engine.Rows,
                    _exitCode,
                    _fault?.Message);
            }
        }

        public TerminalAutomationSnapshot Observe()
        {
            lock (_gate)
            {
                ThrowIfFaulted();
                return CreateSnapshot();
            }
        }

        public void SendText(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            lock (_gate)
            {
                EnsureRunning();
                _connection.Write(text);
            }
        }

        public void Paste(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            lock (_gate)
            {
                EnsureRunning();
                _connection.Write(_engine.WrapPaste(text));
            }
        }

        public void SendKey(string key, string? modifiers, string? keySymbol)
        {
            if (!TryResolveKey(key, out var avaloniaKey))
            {
                throw new ArgumentException($"Unsupported terminal key '{key}'.", nameof(key));
            }

            var parsedModifiers = ParseModifiers(modifiers);

            lock (_gate)
            {
                EnsureRunning();
                var symbol = ResolveKeySymbol(avaloniaKey, parsedModifiers, keySymbol);
                var sequence = KeyMapper.ToVt(avaloniaKey, parsedModifiers, default, symbol, _engine.InputMode)
                    ?? symbol;
                if (sequence is null)
                {
                    throw new ArgumentException($"Key '{key}' has no terminal encoding.", nameof(key));
                }

                _connection.Write(sequence);
            }
        }

        public void Click(int column, int row, int button)
        {
            lock (_gate)
            {
                EnsureRunning();
                if (!_engine.MouseTracking)
                {
                    throw new InvalidOperationException("The application has not enabled terminal mouse tracking.");
                }

                if (_pressedMouseButton >= 0)
                {
                    throw new InvalidOperationException("Release the held mouse button before sending a click.");
                }

                if (column < 0 || column >= _engine.Columns || row < 0 || row >= _engine.Rows)
                {
                    throw new ArgumentOutOfRangeException(nameof(column), "Mouse coordinates must be inside the terminal grid.");
                }

                var press = TerminalInteractionModel.BuildMouseSequence(
                    button, column, row, released: false, _engine.SgrMouse, KeyModifiers.None);
                var release = TerminalInteractionModel.BuildMouseSequence(
                    button, column, row, released: true, _engine.SgrMouse, KeyModifiers.None);
                _connection.Write(press + release);
            }
        }

        public void Resize(int columns, int rows)
        {
            (columns, rows) = ValidateDimensions(columns, rows);
            lock (_gate)
            {
                EnsureRunning();
                _connection.Resize(columns, rows,
                    (int)(columns * Math.Max(1, Math.Round(_visuals.CellSize.Width))),
                    (int)(rows * Math.Max(1, Math.Round(_visuals.CellSize.Height))));
                _engine.Resize(columns, rows,
                    Math.Max(1, Math.Round(_visuals.CellSize.Width)), Math.Max(1, Math.Round(_visuals.CellSize.Height)));
                _revision++;
                SignalChanged();
            }
        }

        public async Task<TerminalAutomationWaitResult> WaitForTextAsync(
            string text,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero and at most five minutes.");
            }

            var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (true)
            {
                Task changed;
                TerminalAutomationSnapshot snapshot;
                lock (_gate)
                {
                    ThrowIfFaulted();
                    snapshot = CreateSnapshot();
                    if (snapshot.Text.Contains(text, StringComparison.Ordinal))
                    {
                        return new(true, snapshot);
                    }

                    if (_exited && _outputDrained)
                    {
                        return new(false, snapshot);
                    }

                    changed = _changed.Task;
                }

                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return new(false, Observe());
                }

                var remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
                try
                {
                    await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return new(false, Observe());
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _connection.OutputReceived -= OnOutput;
                _connection.SessionExited -= OnExited;
                _connection.Faulted -= OnFaulted;
                _engine.ResponseReady -= OnResponse;
                _engine.Diagnostic -= OnDiagnostic;
                SignalChanged();
            }

            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _engine.Dispose();
                _visuals.Dispose();
            }
        }

        private void OnOutput(object? sender, ReadOnlyMemory<byte> data)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var started = Stopwatch.GetTimestamp();
                _engine.Feed(data.Span);
                if (!_engine.MouseTracking)
                {
                    _pressedMouseButton = -1;
                }

                _outputBytes += data.Length;
                _parseTicks += Stopwatch.GetTimestamp() - started;
                _revision++;
                SignalChanged();
            }
        }

        private void OnResponse(object? sender, byte[] response)
        {
            lock (_gate)
            {
                if (!_disposed && _connection.IsRunning)
                {
                    _connection.Write(response);
                }
            }
        }

        private void OnExited(object? sender, TerminalExitInfo info)
        {
            lock (_gate)
            {
                _exited = true;
                _exitCode = info.ExitCode;
                _revision++;
                SignalChanged();
            }
        }

        private async Task ObserveOutputCompletionAsync(Task outputCompletion)
        {
            Exception? error = null;
            try
            {
                await outputCompletion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _outputDrained = true;
                _fault ??= error;
                _revision++;
                SignalChanged();
            }
        }

        private void OnFaulted(object? sender, Exception exception)
        {
            lock (_gate)
            {
                _fault = exception;
                _revision++;
                SignalChanged();
            }
        }

        private TerminalAutomationSnapshot CreateSnapshot()
        {
            var snapshot = _engine.CreateSnapshot();
            var terminal = snapshot.Buffer;
            var screen = terminal.Lines
                .Select(static line => CreateRow(line.Cells))
                .ToArray();
            var text = string.Join(
                '\n',
                screen.Select(static row => row.Text.TrimEnd()));
            return new TerminalAutomationSnapshot(
                Id,
                _revision,
                text,
                screen,
                _engine.Title,
                _engine.WorkingDirectory,
                _connection.IsRunning,
                _exitCode,
                terminal.Columns,
                terminal.Rows,
                terminal.CursorX,
                terminal.CursorY,
                _engine.CursorVisible,
                _engine.AlternateBufferActive,
                _engine.ApplicationCursorKeys,
                _engine.BracketedPaste,
                _engine.MouseTracking,
                _engine.SgrMouse)
            {
                MouseTrackingMode = _engine.MouseTrackingMode,
                InputMode = _engine.InputMode,
                Images = snapshot.Images
                    .Where(image => image.AlternateBuffer == snapshot.AlternateBufferActive)
                    .Select(static image => new TerminalAutomationImage(
                    image.Id, image.Protocol.ToString(), image.AnchorColumn, image.AnchorRow,
                    image.Sixel?.Width ?? (image.Kitty is { Data.Width: > 0 } kittyWidth ? kittyWidth.Data.Width : null),
                    image.Sixel?.Height ?? (image.Kitty is { Data.Height: > 0 } kittyHeight ? kittyHeight.Data.Height : null))).ToArray(),
                Diagnostics = _diagnostics.ToArray(),
            };
        }

        private void OnDiagnostic(object? sender, TerminalEngineDiagnostic diagnostic)
        {
            lock (_gate)
            {
                if (_diagnostics.Count == 32)
                {
                    _diagnostics.Dequeue();
                }

                _diagnostics.Enqueue(diagnostic);
            }
        }

        private static TerminalAutomationRow CreateRow(IReadOnlyList<Cell> cells)
        {
            var text = string.Concat(cells.Select(static cell => cell.Text));
            var visibleCells = new List<TerminalAutomationCell>();
            var styles = new List<TerminalAutomationStyleRun>();
            for (var column = 0; column < cells.Count; column++)
            {
                var cell = cells[column];
                if (!cell.IsWideContinuation && cell.Text != " ")
                {
                    var width = column + 1 < cells.Count && cells[column + 1].IsWideContinuation ? 2 : 1;
                    visibleCells.Add(new TerminalAutomationCell(column, cell.Text, width));
                }

                var attributes = cell.Attributes;
                var isStyled = attributes.Flags != CellFlags.None ||
                               attributes.Foreground.Kind != ColorKind.Default ||
                               attributes.Background.Kind != ColorKind.Default;
                if (!isStyled)
                {
                    continue;
                }

                if (styles.Count > 0)
                {
                    var previous = styles[^1];
                    if (previous.StartColumn + previous.Length == column &&
                        previous.Style == attributes.Flags &&
                        previous.Foreground == attributes.Foreground &&
                        previous.Background == attributes.Background)
                    {
                        styles[^1] = previous with { Length = previous.Length + 1 };
                        continue;
                    }
                }

                styles.Add(new TerminalAutomationStyleRun(
                    column,
                    1,
                    attributes.Flags,
                    attributes.Foreground,
                    attributes.Background));
            }

            return new TerminalAutomationRow(text, visibleCells, styles);
        }

        private void EnsureRunning()
        {
            ThrowIfFaulted();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connection.IsRunning)
            {
                throw new InvalidOperationException("The terminal process has exited.");
            }
        }

        private void ThrowIfFaulted()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fault is not null)
            {
                throw new InvalidOperationException("The terminal session failed.", _fault);
            }
        }

        private void SignalChanged()
        {
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static bool TryResolveKey(string key, out Key resolved)
        {
            var normalized = key.Trim().ToLowerInvariant() switch
            {
                "arrowup" => nameof(Key.Up),
                "arrowdown" => nameof(Key.Down),
                "arrowleft" => nameof(Key.Left),
                "arrowright" => nameof(Key.Right),
                "enter" => nameof(Key.Return),
                "esc" => nameof(Key.Escape),
                "backspace" => nameof(Key.Back),
                _ => key,
            };
            return Enum.TryParse(normalized, ignoreCase: true, out resolved);
        }

        private static string? ResolveKeySymbol(Key key, KeyModifiers modifiers, string? keySymbol)
        {
            if (keySymbol is not null)
            {
                if (!Rune.TryGetRuneAt(keySymbol, 0, out var rune) || rune.Utf16SequenceLength != keySymbol.Length)
                {
                    throw new ArgumentException("A key symbol must contain exactly one Unicode scalar.", nameof(keySymbol));
                }

                return keySymbol;
            }

            var shifted = modifiers.HasFlag(KeyModifiers.Shift);
            if (key is >= Key.A and <= Key.Z)
            {
                var letter = (char)('a' + key - Key.A);
                return shifted ? char.ToUpperInvariant(letter).ToString() : letter.ToString();
            }

            if (key is >= Key.D0 and <= Key.D9)
            {
                const string shiftedDigits = ")!@#$%^&*(";
                var digit = key - Key.D0;
                return shifted ? shiftedDigits[digit].ToString() : digit.ToString();
            }

            if (key is >= Key.NumPad0 and <= Key.NumPad9)
            {
                return ((char)('0' + key - Key.NumPad0)).ToString();
            }

            if (key == Key.Space)
            {
                return " ";
            }

            return key.ToString() switch
            {
                "OemSemicolon" => shifted ? ":" : ";",
                "OemPlus" => shifted ? "+" : "=",
                "OemComma" => shifted ? "<" : ",",
                "OemMinus" => shifted ? "_" : "-",
                "OemPeriod" => shifted ? ">" : ".",
                "OemQuestion" => shifted ? "?" : "/",
                "OemTilde" => shifted ? "~" : "`",
                "OemOpenBrackets" => shifted ? "{" : "[",
                "OemPipe" => shifted ? "|" : "\\",
                "OemCloseBrackets" => shifted ? "}" : "]",
                "OemQuotes" => shifted ? "\"" : "'",
                _ => null,
            };
        }
    }
}
