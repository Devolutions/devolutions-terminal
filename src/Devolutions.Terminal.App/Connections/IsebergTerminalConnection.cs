#if POWERSHELL_ISE
using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using Devolutions.Terminal.Connection;
using Iseberg;
using Iseberg.Core;

namespace Devolutions.Terminal.App.Connections;

/// <summary>VT transport and line editing over an existing Iseberg runspace, not a second shell process.</summary>
public sealed class IsebergTerminalConnection : IRestartableTerminalConnection
{
    private const string Escape = "\u001b";
    private readonly WorkbenchControl _workbench;
    private readonly SessionModel _session;
    private readonly Action _focusInput;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _escapeSequence = new();
    private InputRequest? _input;
    private string _inputDraft = "";
    private int _caret;
    private int _revision;
    private bool _promptShown;
    private string _prefix = "";
    private bool _acceptsCommands;
    private bool _inputDisabled = true;
    private bool _pasting;
    private bool _sawCarriageReturn;
    private bool _disposed;
    private string? _completionOriginal;
    private string? _completionResult;
    private CompletionSet? _completions;
    private int _completionIndex;
    private int _completionCaret;
    private EditorTheme _theme = EditorThemePresets.Classic();
    private readonly List<InputEdit> _undo = [];
    private readonly List<InputEdit> _redo = [];
    private int? _selectionAnchor;
    private char? _pendingHighSurrogate;
    private bool _syntaxHighlighting = true;
    private bool _submissionPending;
    private readonly CancellationTokenSource _inputOperations = new();
    private sealed record InputEdit(string Text, int Caret);

    public IsebergTerminalConnection(WorkbenchControl workbench, SessionModel session, Action? focusInput = null)
    {
        _workbench = workbench;
        _session = session;
        _focusInput = focusInput ?? (() => { });
    }

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;
    public event EventHandler<TerminalExitInfo>? SessionExited;
    public event EventHandler<Exception>? Faulted;
    public bool IsRunning { get; private set; }
    public int Columns { get; private set; } = 80;
    public int Rows { get; private set; } = 24;
    public TerminalConnectionCapabilities Capabilities => TerminalConnectionCapabilities.Resize;
    public TerminalConnectionState State { get; private set; } = TerminalConnectionState.NotConnected;
    public TerminalProcessMetadata? ProcessMetadata => null;
    public TerminalExitInfo? LastExitInfo { get; private set; }
    public bool IsInputEnabled => IsRunning && !_inputDisabled && !_submissionPending && _session.Engine.State != SessionState.Failed &&
        (_input is not null || _acceptsCommands && (!_session.IsConsoleSubmissionPending ||
            _session.Engine.IsDebuggerPaused || _session.Engine.IsNestedPromptActive));
    public bool HasPendingInput => _input is not null;
    public string InputText => _input?.Secret == true ? "" : _line.ToString();
    public int CaretOffset => _input?.Secret == true ? 0 : _caret;
    public int InputRevision => _revision;
    public bool IsSecretInput => _input?.Secret == true;
    public bool CanUndo => IsInputEnabled && _input?.Secret != true && _undo.Count > 0;
    public bool CanRedo => IsInputEnabled && _input?.Secret != true && _redo.Count > 0;
    public int SelectionStart => Math.Min(_selectionAnchor ?? _caret, _caret);
    public int SelectionLength => Math.Abs((_selectionAnchor ?? _caret) - _caret);
    public string SelectedInput => _input?.Secret == true ? "" : _line.ToString(SelectionStart, SelectionLength);
    public event EventHandler? InputChanged;
    public void ApplyTheme(EditorTheme theme) { _theme = theme.Copy(); Redraw(); }
    public void ApplyPreferences(UserSettings preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Redraw();
    }

    public void InsertNewLine()
    {
        if (!IsInputEnabled || _input is not null) return;
        Insert("\n");
    }

    public void MoveCaret(int direction, bool extendSelection = false, bool toBoundary = false)
    {
        if (!IsInputEnabled) return;
        _selectionAnchor = extendSelection ? _selectionAnchor ?? _caret : null;
        _caret = toBoundary ? direction < 0 ? 0 : _line.Length : direction < 0 ? PreviousBoundary(_caret) : NextBoundary(_caret);
        ++_revision;
        _completions = null;
        Redraw();
        InputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectInput()
    {
        if (!IsInputEnabled) return;
        _selectionAnchor = 0;
        _caret = _line.Length;
        ++_revision;
        Redraw();
        InputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelection()
    {
        if (!IsInputEnabled || SelectionLength == 0) return;
        RememberEdit();
        RemoveSelection();
        Changed();
    }

    public void Undo() => RestoreEdit(_undo, _redo);
    public void Redo() => RestoreEdit(_redo, _undo);
    private void RestoreEdit(List<InputEdit> source, List<InputEdit> destination)
    {
        if (!IsInputEnabled || _input?.Secret == true || source.Count == 0) return;
        destination.Add(new(_line.ToString(), _caret));
        var edit = source[^1];
        source.RemoveAt(source.Count - 1);
        _line.Clear().Append(edit.Text);
        _caret = edit.Caret;
        Changed();
    }

    private void RememberEdit()
    {
        if (_input?.Secret == true || _pasting) return;
        _undo.Add(new(_line.ToString(), _caret));
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void RemoveSelection()
    {
        var start = SelectionStart;
        var length = SelectionLength;
        if (length > 0) { _line.Remove(start, length); _caret = start; }
        _selectionAnchor = null;
    }

    public async Task<CompletionSet?> RequestCompletionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInputEnabled || _input is not null) return null;
        var text = InputText;
        var caret = _caret;
        var revision = _revision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inputOperations.Token);
        var result = await _session.Engine.CompleteAsync(text, caret, cancellation.Token);
        return IsInputEnabled && _input is null && revision == _revision ? result : null;
    }

    public async Task<ScriptAnalysis?> RequestAnalysisAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInputEnabled || _input is not null) return null;
        var revision = _revision;
        var text = InputText;
        var cached = _session.Console.InputAnalysis;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inputOperations.Token);
        var result = cached ?? await _session.Engine.AnalyzeAsync(text, cancellationToken: cancellation.Token);
        return IsInputEnabled && _input is null && revision == _revision && InputText == text ? result : null;
    }

    public bool ApplyCompletion(CompletionSet completion, int index, int expectedRevision)
    {
        if (!IsInputEnabled || _input is not null || expectedRevision != _revision) return false;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, completion.Matches.Count);
        if (completion.Start < 0 || completion.Start > _line.Length ||
            completion.Length < 0 || completion.Length > _line.Length - completion.Start)
            throw new ArgumentException("The completion span is outside the input.", nameof(completion));
        RememberEdit();
        _line.Remove(completion.Start, completion.Length).Insert(completion.Start, completion.Matches[index].CompletionText);
        _caret = completion.Start + completion.Matches[index].CompletionText.Length;
        Changed();
        return true;
    }

    public Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows,
        CancellationToken cancellationToken = default) => StartAsync(
            new TerminalLaunchOptions { CommandLine = commandLine, WorkingDirectory = workingDirectory, Columns = columns, Rows = rows },
            cancellationToken);

    public Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != TerminalConnectionState.NotConnected) throw new InvalidOperationException("The Iseberg transport has already started.");
        Resize(options.Columns, options.Rows);
        _session.Engine.Output += WriteOutput;
        _session.Engine.InputRequested += OnInputRequested;
        _session.Console.AnalysisChanged += OnAnalysisChanged;
        State = TerminalConnectionState.Connected;
        IsRunning = true;
        Emit(Escape + "[?2004h");
        return Task.CompletedTask;
    }

    public Task RestartAsync(TerminalLaunchOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Duplicate or reopen the Iseberg tab to create a fresh runspace.");

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsRunning) throw new InvalidOperationException("The Iseberg terminal is not connected.");
        var bytes = data.ToArray();
        if (Dispatcher.UIThread.CheckAccess()) ConsumeObserved(bytes);
        else Dispatcher.UIThread.Post(() => { if (IsRunning) ConsumeObserved(bytes); });
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Columns = columns;
        Rows = rows;
        _session.Engine.SetTerminalSize(columns, rows);
    }

    public void RefreshState(bool acceptsCommands, bool inputDisabled)
    {
        if (!IsRunning) return;
        _acceptsCommands = acceptsCommands;
        _inputDisabled = inputDisabled;
        if (_workbench.IsDisposed) _inputOperations.Cancel();
        if (!IsInputEnabled || _input is not null) return;
        if (!_promptShown)
        {
            Emit(Escape + "]133;D;0\a" + Escape + "]133;A\a");
            ShowPrompt(_session.Engine.IsNestedPromptActive ? _session.Engine.NestedPrompt :
                _session.Engine.IsDebuggerPaused ? _session.Engine.DebugPrompt : _session.Engine.Prompt);
        }
    }

    public void WriteOutput(OutputEntry entry)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => WriteOutput(entry));
            return;
        }
        if (!IsRunning) return;
        if (_promptShown) HidePrompt();
        if (entry.Kind == OutputKind.Command)
        {
            _acceptsCommands = false;
            Emit(Escape + "]133;B\a" + Escape + "]133;C\a");
        }
        var style = entry.Style;
        var codes = new List<string>();
        if (style?.Foreground is { } foreground) codes.Add(ColorCode(foreground, 38));
        else if (entry.Kind is OutputKind.Error or OutputKind.Warning or OutputKind.Verbose or OutputKind.Debug)
            codes.Add(ColorCode(_theme.Colors["Stream." + entry.Kind], 38));
        if (style?.Background is { } background) codes.Add(ColorCode(background, 48));
        if (style?.Bold == true) codes.Add("1");
        if (style?.Underline == true) codes.Add("4");
        if (style?.Inverse == true) codes.Add("7");
        if (codes.Count > 0) Emit(Escape + "[" + string.Join(';', codes) + "m");
        Emit(entry.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
        if (codes.Count > 0) Emit(Escape + "[0m");
    }

    private static string ColorCode(string value, int target)
    {
        var color = Color.Parse(value);
        return $"{target};2;{color.R};{color.G};{color.B}";
    }

    public void Clear()
    {
        if (!IsRunning) return;
        _promptShown = false;
        Emit(Escape + "[2J" + Escape + "[3J" + Escape + "[H");
        RefreshState(_acceptsCommands, _inputDisabled);
    }

    private void OnInputRequested(InputRequest request) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsRunning || _workbench.IsDisposed) { request.Response.TrySetCanceled(); return; }
            HidePrompt();
            _inputDraft = _line.ToString();
            _line.Clear();
            _caret = 0;
            _input = request;
            _undo.Clear(); _redo.Clear();
            _workbench.IsEnabled = true;
            if (request.Message is not ("Enter a value:" or "Enter a secure value:"))
            {
                Emit("\r\n" + request.Caption + "\r\n" + request.Message.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n");
                for (var index = 0; index < request.Choices.Count; index++)
                    Emit($"[{index}] {request.Choices[index].Label}  {request.Choices[index].Help}\r\n");
            }
            ShowPrompt(request.Choices.Count > 0 ? "Choice: " : "");
            if (!_inputDisabled) _focusInput();
            _ = request.Response.Task.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_input, request))
                {
                    HidePrompt();
                    _input = null;
                    ReplaceLine(_inputDraft);
                }
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });

    private void ConsumeObserved(byte[] data)
    {
        try { Consume(data); }
        catch (Exception error)
        {
            State = TerminalConnectionState.Failed;
            Faulted?.Invoke(this, error);
        }
    }

    private void Consume(byte[] data)
    {
        var characters = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
        var count = _decoder.GetChars(data, characters, flush: false);
        for (var index = 0; index < count; index++) Consume(characters[index]);
    }

    private void Consume(char character)
    {
        if (_escapeSequence.Length > 0)
        {
            _escapeSequence.Append(character);
            if (_escapeSequence.Length == 2 && character is '[' or 'O') return;
            if (_escapeSequence.Length > 2 && character is >= '\x40' and <= '\x7e')
            {
                var sequence = _escapeSequence.ToString();
                _escapeSequence.Clear();
                if (sequence == Escape + "[200~") { _sawCarriageReturn = false; RememberEdit(); _pasting = true; return; }
                if (sequence == Escape + "[201~") { _pasting = false; Redraw(); return; }
                if (_pasting) Insert(sequence);
                else HandleEscape(sequence);
            }
            else if (_escapeSequence.Length == 2 || _escapeSequence.Length > 64)
            {
                _escapeSequence.Clear();
            }
            return;
        }
        if (character == '\x1b') { _escapeSequence.Append(character); return; }
        if (!IsInputEnabled && (character != '\x03' || _inputDisabled)) return;
        if (char.IsHighSurrogate(character)) { _pendingHighSurrogate = character; return; }
        if (_pendingHighSurrogate is { } high)
        {
            _pendingHighSurrogate = null;
            Insert(new string([high, character]));
            return;
        }
        if (character == '\n' && _sawCarriageReturn) { _sawCarriageReturn = false; return; }
        _sawCarriageReturn = character == '\r';
        if (_pasting)
        {
            Insert(character is '\r' or '\n' ? "\n" : character.ToString());
            return;
        }
        if (character == '\x03')
        {
            HidePrompt();
            Emit("^C\r\n");
            _input?.Response.TrySetCanceled();
            ReplaceLine("");
            Observe(_session.Engine.StopAsync());
            return;
        }
        switch (character)
        {
            case '\r': case '\n': Submit(); break;
            case '\t': Observe(CompleteAsync()); break;
            case '\b': case '\x7f': Backspace(); break;
            case '\x01': MoveCaret(-1, toBoundary: true); break;
            case '\x05': MoveCaret(1, toBoundary: true); break;
            case '\x0b': RememberEdit(); _line.Remove(_caret, _line.Length - _caret); Changed(); break;
            case '\x15': RememberEdit(); _line.Remove(0, _caret); _caret = 0; Changed(); break;
            case '\x17':
                while (_caret > 0 && char.IsWhiteSpace(_line[_caret - 1])) Backspace();
                while (_caret > 0 && !char.IsWhiteSpace(_line[_caret - 1])) Backspace();
                break;
            case '\x0c': Clear(); break;
            case '\x1a': Undo(); break;
            case '\x19': Redo(); break;
            default:
                if (!char.IsControl(character)) Insert(character.ToString());
                break;
        }
    }

    private void HandleEscape(string sequence)
    {
        if (!IsInputEnabled) return;
        switch (sequence)
        {
            case "\x1b[A": case "\x1bOA": NavigateHistory(-1); break;
            case "\x1b[B": case "\x1bOB": NavigateHistory(1); break;
            case "\x1b[C": case "\x1bOC": MoveCaret(1); break;
            case "\x1b[D": case "\x1bOD": MoveCaret(-1); break;
            case "\x1b[H": case "\x1bOH": case "\x1b[1~": MoveCaret(-1, toBoundary: true); break;
            case "\x1b[F": case "\x1bOF": case "\x1b[4~": MoveCaret(1, toBoundary: true); break;
            case "\x1b[1;2C": MoveCaret(1, extendSelection: true); break;
            case "\x1b[1;2D": MoveCaret(-1, extendSelection: true); break;
            case "\x1b[13;2u": InsertNewLine(); break;
            case "\x1b[3~":
                if (SelectionLength > 0) DeleteSelection();
                else if (_caret < _line.Length) { RememberEdit(); _line.Remove(_caret, NextBoundary(_caret) - _caret); Changed(); }
                break;
            case "\x1b[Z": Observe(CompleteAsync(backwards: true)); break;
        }
    }

    private int PreviousBoundary(int offset) =>
        StringInfo.ParseCombiningCharacters(_line.ToString()).LastOrDefault(boundary => boundary < offset);
    private int NextBoundary(int offset) =>
        StringInfo.ParseCombiningCharacters(_line.ToString()).FirstOrDefault(boundary => boundary > offset, _line.Length);
    private void Backspace()
    {
        if (SelectionLength > 0) { DeleteSelection(); return; }
        if (_caret == 0) return;
        RememberEdit();
        var previous = PreviousBoundary(_caret);
        _line.Remove(previous, _caret - previous);
        _caret = previous;
        Changed();
    }

    private void Insert(string text)
    {
        RememberEdit();
        RemoveSelection();
        _line.Insert(_caret, text);
        _caret += text.Length;
        Changed();
    }

    private void ReplaceLine(string text)
    {
        _line.Clear().Append(text);
        _caret = _line.Length;
        Changed();
    }

    private void Changed()
    {
        ++_revision;
        _completions = null;
        _selectionAnchor = null;
        if (_input is null) _session.Input = _line.ToString();
        Redraw();
        InputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NavigateHistory(int delta)
    {
        if (_input is not null) return;
        RememberEdit();
        if (_session.HistoryIndex == _session.History.Count) _session.DraftInput = _line.ToString();
        _session.HistoryIndex = Math.Clamp(_session.HistoryIndex + delta, 0, _session.History.Count);
        ReplaceLine(_session.HistoryIndex == _session.History.Count ? _session.DraftInput : _session.History[_session.HistoryIndex]);
    }

    private void Submit()
    {
        if (_submissionPending) return;
        var text = _line.ToString();
        if (_input is { } request)
        {
            Emit("\r\n");
            _promptShown = false;
            _input = null;
            ReplaceLine(_inputDraft);
            request.Response.TrySetResult(text);
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            Emit("\r\n");
            _promptShown = false;
            ReplaceLine("");
            RefreshState(_acceptsCommands, _inputDisabled);
            return;
        }
        _submissionPending = true;
        Observe(SubmitAnalyzedAsync(text, _revision));
    }

    private async Task SubmitAnalyzedAsync(string text, int revision)
    {
        try
        {
            var analysis = await _session.Engine.AnalyzeAsync(text, cancellationToken: _inputOperations.Token);
            if (!IsRunning || _disposed || _input is not null || _revision != revision || text != _line.ToString()) return;
            if (analysis.Errors.Any(error => error.IncompleteInput ||
                error.ErrorId is "MissingEndParenthesisInExpression" or "MissingEndParenthesisInMethodCall"))
            {
                _submissionPending = false;
                Insert("\n");
                return;
            }
            _acceptsCommands = false;
            HidePrompt();
            ReplaceLine("");
            _undo.Clear(); _redo.Clear();
            _submissionPending = false;
            await _workbench.SubmitConsoleInputAsync(text);
        }
        finally { _submissionPending = false; InputChanged?.Invoke(this, EventArgs.Empty); }
    }

    public async Task CompleteAsync(bool backwards = false)
    {
        if (!IsInputEnabled || _input is not null) return;
        var text = _line.ToString();
        if (_completions is not null && text == _completionResult && _caret == _completionCaret)
        {
            _completionIndex = (_completionIndex + (backwards ? _completions.Matches.Count - 1 : 1)) % _completions.Matches.Count;
        }
        else
        {
            var revision = _revision;
            var caret = _caret;
            var result = await _session.Engine.CompleteAsync(text, _caret, _inputOperations.Token);
            if (!IsInputEnabled || _input is not null || revision != _revision || caret != _caret || result.Matches.Count == 0) return;
            _completionOriginal = text;
            _completions = result;
            _completionIndex = backwards ? result.Matches.Count - 1 : 0;
        }
        var matches = _completions ?? throw new InvalidOperationException("The completion set is unavailable.");
        var original = _completionOriginal ?? throw new InvalidOperationException("The original completion input is unavailable.");
        var replacement = matches.Matches[_completionIndex].CompletionText;
        RememberEdit();
        _line.Clear().Append(original.Remove(matches.Start, matches.Length).Insert(matches.Start, replacement));
        _caret = matches.Start + replacement.Length;
        _completionCaret = _caret;
        _completionResult = _line.ToString();
        _selectionAnchor = null;
        _session.Input = _completionResult;
        ++_revision;
        Redraw();
        InputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowPrompt(string prefix)
    {
        _prefix = prefix;
        _promptShown = true;
        Emit(Escape + "[s");
        Redraw();
    }

    private void HidePrompt()
    {
        if (!_promptShown) return;
        Emit(Escape + "[u" + Escape + "[J");
        _promptShown = false;
    }

    private string DisplayInput(string value, bool highlighted = false)
    {
        if (_input?.Secret == true) return new string('*', StringInfo.ParseCombiningCharacters(value).Length);
        var display = new StringBuilder();
        Token[] tokens = [];
        if (highlighted && _syntaxHighlighting && _input is null && _session.Console.InputAnalysis is { } analysis)
            tokens = analysis.Tokens;
        string? activeColor = null;
        var tokenIndex = 0;
        var selected = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (highlighted)
            {
                var isSelected = index >= SelectionStart && index < SelectionStart + SelectionLength;
                if (selected != isSelected) { display.Append(Escape).Append(isSelected ? "[7m" : "[27m"); selected = isSelected; }
                while (tokenIndex < tokens.Length && tokens[tokenIndex].Extent.EndOffset <= index) tokenIndex++;
                var token = tokenIndex < tokens.Length && tokens[tokenIndex].Extent.StartOffset <= index ? tokens[tokenIndex] : null;
                var key = token?.Kind switch
                {
                    TokenKind.Comment => "Comment",
                    TokenKind.StringExpandable or TokenKind.StringLiteral or TokenKind.HereStringExpandable or TokenKind.HereStringLiteral => "String",
                    TokenKind.Variable or TokenKind.SplattedVariable => "Variable",
                    TokenKind.Number => "Number",
                    TokenKind.Parameter => "Parameter",
                    _ when token?.TokenFlags.HasFlag(TokenFlags.Keyword) == true => "Keyword",
                    _ when token?.TokenFlags.HasFlag(TokenFlags.CommandName) == true => "Command",
                    _ when token?.TokenFlags.HasFlag(TokenFlags.TypeName) == true => "Type",
                    _ => "Foreground"
                };
                var color = _theme.Colors["Console." + key];
                if (activeColor != color) { display.Append(Escape).Append('[').Append(ColorCode(color, 38)).Append('m'); activeColor = color; }
            }
            if (character == '\n') display.Append("\r\n>> ");
            else if (character == '\t') display.Append("    ");
            else if (char.IsControl(character)) display.Append('^').Append((char)(character + 64));
            else display.Append(character);
        }
        if (highlighted) display.Append(Escape).Append("[0m");
        return display.ToString();
    }

    private void Redraw()
    {
        if (!_promptShown || !IsRunning) return;
        var text = _line.ToString();
        Emit(Escape + "[u" + Escape + "[J" + _prefix + DisplayInput(text, highlighted: true));
        if (_caret != text.Length) Emit(Escape + "[u" + _prefix + DisplayInput(text[.._caret], highlighted: true));
    }

    private void OnAnalysisChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Redraw();
        else Dispatcher.UIThread.Post(Redraw);
    }

    private void Observe(Task task) => _ = ObserveAsync(task);
    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) when (_inputOperations.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (IsRunning) await _workbench.ReportConsoleErrorAsync(error);
            else System.Diagnostics.Trace.TraceError("Iseberg terminal operation failed during close: {0}", error);
        }
        finally
        {
            if (IsRunning) RefreshState(
                _workbench.IsStarted && (_session.Engine.State == SessionState.Ready || _session.Engine.IsDebuggerPaused || _session.Engine.IsNestedPromptActive),
                _inputDisabled);
        }
    }

    private void Emit(string text) => OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRunning) return Task.CompletedTask;
        _inputOperations.Cancel();
        IsRunning = false;
        State = TerminalConnectionState.Closed;
        _session.Engine.Output -= WriteOutput;
        _session.Engine.InputRequested -= OnInputRequested;
        _session.Console.AnalysisChanged -= OnAnalysisChanged;
        _input?.Response.TrySetCanceled();
        _input = null;
        _line.Clear();
        _undo.Clear(); _redo.Clear();
        InputChanged?.Invoke(this, EventArgs.Empty);
        LastExitInfo = new(null, 0, TerminalExitReason.Closed, false, DateTimeOffset.UtcNow);
        SessionExited?.Invoke(this, LastExitInfo);
        Exited?.Invoke(this, 0);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await CloseAsync();
        _inputOperations.Dispose();
        _disposed = true;
        State = TerminalConnectionState.Disposed;
    }
}
#endif
