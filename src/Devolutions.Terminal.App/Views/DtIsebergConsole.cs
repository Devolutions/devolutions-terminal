#if POWERSHELL_ISE
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Devolutions.Terminal.App.Connections;
using Devolutions.Terminal.Settings;
using Iseberg;
using Iseberg.Core;

namespace Devolutions.Terminal.App.Views;

internal sealed class DtIsebergConsole : IWorkbenchConsole
{
    private readonly ProfileSettings profile;
    private readonly Popup completionPopup;
    private readonly ListBox completionList;
    private readonly TextBlock completionDescription;
    private readonly Border completionBorder;
    private readonly DispatcherTimer completionTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private IsebergTerminalConnection? _connection;
    private EditorTheme? _theme;
    private string? _fontFamily;
    private double? _fontSizeDips;
    private WorkbenchControl? workbench;
    private UserSettings preferences = new();
    private CancellationTokenSource? completionCancellation;
    private CompletionSet? completions;
    private int completionRevision;
    private bool acceptingCompletion;
    public DtIsebergConsole(TermControl terminal, Control view, ProfileSettings profile)
    {
        Terminal = terminal;
        this.profile = profile;
        completionList = new ListBox { MaxHeight = 220, MinWidth = 250, Focusable = false };
        completionDescription = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Margin = new Thickness(8) };
        completionBorder = new Border
        {
            BorderThickness = new Thickness(1), MaxWidth = 500,
            Child = new StackPanel { Children = { completionList, completionDescription } }
        };
        AutomationProperties.SetName(completionList, "PowerShell console completions");
        AutomationProperties.SetName(completionDescription, "PowerShell completion description");
        completionPopup = new Popup
        {
            PlacementTarget = terminal, Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true, Child = completionBorder
        };
        View = new Grid { Children = { view, completionPopup } };
        terminal.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        completionPopup.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        completionList.SelectionChanged += (_, _) => RefreshDescription();
        completionList.DoubleTapped += (_, _) => AcceptCompletion();
        completionTimer.Tick += async (_, _) =>
        {
            completionTimer.Stop();
            await ObserveCompletionAsync(automatic: true);
        };
    }
    public TermControl Terminal { get; }
    public Control View { get; }
    public bool HasSelection => _connection?.SelectionLength > 0 || Terminal.HasSelection;
    public bool IsInputEnabled => _connection?.IsInputEnabled == true;
    public bool HasPendingInput => _connection?.HasPendingInput == true;
    public bool CanUndo => _connection?.CanUndo == true;
    public bool CanRedo => _connection?.CanRedo == true;
    public bool CanCut => IsInputEnabled && _connection?.SelectedInput.Length > 0;
    public string InputText => _connection?.InputText ?? "";
    public int CaretOffset => _connection?.CaretOffset ?? 0;

    public async Task StartAsync(WorkbenchControl workbench, SessionModel session)
    {
        this.workbench = workbench;
        var connection = new IsebergTerminalConnection(workbench, session, () => Terminal.Focus());
        _connection = connection;
        connection.InputChanged += OnInputChanged;
        Terminal.ConnectionFactory = _ => connection;
        Terminal.AccessibleName = "Iseberg PowerShell terminal";
        await Terminal.StartAsync(profile, 80, 24);
        if (_fontSizeDips is { } size) SetFontSize(size);
        if (_theme is not null) ApplyAppearance(_theme, _fontFamily ?? profile.FontFace);
        connection.Clear();
    }

    public void Write(OutputEntry entry) => _connection?.WriteOutput(entry);
    public void RefreshState(bool acceptsCommands, bool inputDisabled)
    {
        _connection?.RefreshState(acceptsCommands, inputDisabled);
        if (!IsInputEnabled) CloseCompletion();
    }
    public void Focus() => Terminal.Focus();
    public void Clear() => _connection?.Clear();
    public async Task CopyAsync()
    {
        if (_connection?.SelectedInput is { Length: > 0 } selected)
            await (TopLevel.GetTopLevel(Terminal)?.Clipboard ?? throw new InvalidOperationException("The console clipboard is unavailable."))
                .SetTextAsync(selected);
        else await Terminal.CopyAsync();
    }
    public async Task CutAsync()
    {
        if (!CanCut) return;
        await CopyAsync();
        _connection!.DeleteSelection();
    }
    public void Undo() => _connection?.Undo();
    public void Redo() => _connection?.Redo();
    public async Task PasteAsync() => await Terminal.PasteAsync();
    public void SelectAll()
    {
        if (IsInputEnabled) _connection?.SelectInput();
        else Terminal.SelectAll();
    }
    public async Task CompleteAsync(bool backwards = false)
    {
        await ShowCompletionAsync();
        if (completionPopup.IsOpen && backwards) completionList.SelectedIndex = completionList.ItemCount - 1;
    }
    public void ApplyPreferences(UserSettings preferences)
    {
        this.preferences = preferences.Copy();
        _connection?.ApplyPreferences(preferences);
        if (!preferences.ConsoleIntelliSense) CloseCompletion();
    }

    private void OnInputChanged(object? sender, EventArgs args)
    {
        if (acceptingCompletion) return;
        CloseCompletion();
        if (preferences.ConsoleIntelliSense && Terminal.IsKeyboardFocusWithin && IsInputEnabled && !HasPendingInput &&
            CaretOffset > 0 && "$-:. [\\/".Contains(InputText[CaretOffset - 1]))
            completionTimer.Start();
    }

    private void CloseCompletion()
    {
        completionTimer.Stop();
        completionCancellation?.Cancel();
        completionPopup.IsOpen = false;
        completions = null;
    }

    private async Task ObserveCompletionAsync(bool automatic = false)
    {
        try { await ShowCompletionAsync(automatic); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (workbench is { IsDisposed: false } owner) await owner.ReportConsoleErrorAsync(error);
            else System.Diagnostics.Trace.TraceError("Console completion failed during close: {0}", error);
        }
    }

    private async Task ShowCompletionAsync(bool automatic = false)
    {
        var connection = _connection;
        if (connection is null || !IsInputEnabled || HasPendingInput || workbench?.IsDisposed != false) return;
        completionCancellation?.Cancel();
        completionCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(preferences.IntelliSenseTimeoutSeconds));
        completionCancellation = cancellation;
        var revision = connection.InputRevision;
        try
        {
            IReadOnlySet<CompletionResultType>? filter = null;
            if (automatic)
            {
                var analysis = await connection.RequestAnalysisAsync(cancellation.Token);
                if (analysis is null || revision != connection.InputRevision) return;
                filter = EditorAnalysis.CompletionFilter(analysis, InputText, CaretOffset);
                if (filter is null) return;
            }
            var result = await connection.RequestCompletionAsync(cancellation.Token);
            if (result is not null && filter is not null)
                result = result with { Matches = result.Matches.Where(match => filter.Contains(match.ResultType)).ToArray() };
            if (result is null || result.Matches.Count == 0 || revision != connection.InputRevision ||
                workbench.IsDisposed || !Terminal.IsKeyboardFocusWithin || !IsInputEnabled) return;
            completions = result;
            completionRevision = revision;
            completionList.ItemsSource = result.Matches.Select(match => match.ListItemText).ToArray();
            completionList.SelectedIndex = 0;
            RefreshDescription();
            var buffer = Terminal.Engine.CreateSnapshot().Buffer;
            completionPopup.PlacementRect = new Rect(
                Math.Clamp(buffer.CursorX * Terminal.CellSize.Width, 0, Math.Max(0, Terminal.Bounds.Width - Terminal.CellSize.Width)),
                Math.Clamp(buffer.CursorY * Terminal.CellSize.Height, 0, Math.Max(0, Terminal.Bounds.Height - Terminal.CellSize.Height)),
                Terminal.CellSize.Width, Terminal.CellSize.Height);
            completionPopup.IsOpen = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (revision == connection.InputRevision && IsInputEnabled)
                System.Diagnostics.Trace.TraceWarning("PowerShell console completion canceled or timed out.");
        }
        finally
        {
            if (ReferenceEquals(completionCancellation, cancellation)) completionCancellation = null;
        }
    }

    private void RefreshDescription() => completionDescription.Text =
        completions is { } matches && completionList.SelectedIndex is var index && index >= 0 && index < matches.Matches.Count
            ? matches.Matches[index].ToolTip : "";

    private void AcceptCompletion()
    {
        if (completions is not { } matches || _connection is not { } connection || completionList.SelectedIndex < 0) return;
        acceptingCompletion = true;
        try { connection.ApplyCompletion(matches, completionList.SelectedIndex, completionRevision); }
        finally { acceptingCompletion = false; CloseCompletion(); Terminal.Focus(); }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled || !IsInputEnabled || _connection is not { } connection) return;
        var command = args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = args.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (completionPopup.IsOpen)
        {
            if (args.Key is Key.Up or Key.Down)
            {
                completionList.SelectedIndex = (completionList.SelectedIndex + (args.Key == Key.Up ? -1 : 1) + completionList.ItemCount) % completionList.ItemCount;
                args.Handled = true; return;
            }
            if (args.Key == Key.Escape) { CloseCompletion(); args.Handled = true; return; }
            if (args.Key == Key.Tab || args.Key == Key.Enter && preferences.ConsoleCompletionOnEnter)
            {
                AcceptCompletion(); args.Handled = true; return;
            }
            if (args.Key == Key.Enter) CloseCompletion();
        }
        if (args.Key == Key.Enter && shift) { connection.InsertNewLine(); args.Handled = true; return; }
        if (shift && args.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            connection.MoveCaret(args.Key is Key.Left or Key.Home ? -1 : 1, extendSelection: true,
                toBoundary: args.Key is Key.Home or Key.End);
            args.Handled = true; return;
        }
        if (!command) return;
        try
        {
            switch (args.Key)
            {
                case Key.Z: if (shift) Redo(); else Undo(); break;
                case Key.Y: Redo(); break;
                case Key.A: SelectAll(); break;
                case Key.X: await CutAsync(); break;
                case Key.C when connection.SelectionLength > 0: await CopyAsync(); break;
                default: return;
            }
            args.Handled = true;
        }
        catch (Exception error)
        {
            args.Handled = true;
            if (workbench is { IsDisposed: false } owner) await owner.ReportConsoleErrorAsync(error);
        }
    }
    public void SetFontSize(double size)
    {
        _fontSizeDips = size;
        var points = size * 3.0 / 4;
        if (_connection is not null && Math.Abs(Terminal.FontSize - points) > 0.01)
            Terminal.AdjustFontSize(points - Terminal.FontSize);
    }
    public void ApplyAppearance(EditorTheme theme, string fontFamily)
    {
        _theme = theme;
        _fontFamily = fontFamily;
        if (_connection is null) return;
        var appearance = profile.WithOverrides(new NewTerminalArgs());
        appearance.Font = new FontSettings { Face = fontFamily, Size = (_fontSizeDips ?? profile.FontSize) * 3.0 / 4, Weight = profile.FontWeight };
        Terminal.ApplyAppearance(appearance);
        static uint Rgb(string value)
        {
            var color = Color.Parse(value);
            return (uint)((color.R << 16) | (color.G << 8) | color.B);
        }
        Terminal.Engine.Scheme = profile.ResolveScheme()
            .WithForeground(Rgb(theme.Colors["Console.Foreground"]))
            .WithBackground(Rgb(theme.Colors["Console.Background"]))
            .WithCursor(Rgb(theme.Colors["Console.Foreground"]));
        _connection.ApplyTheme(theme);
        completionBorder.Background = new SolidColorBrush(Color.Parse(theme.Colors["Console.Background"]));
        completionBorder.BorderBrush = new SolidColorBrush(Color.Parse(theme.Colors["Console.Foreground"]));
        completionList.Foreground = completionDescription.Foreground = new SolidColorBrush(Color.Parse(theme.Colors["Console.Foreground"]));
        Terminal.InvalidateVisual();
    }
    public async ValueTask DisposeAsync()
    {
        CloseCompletion();
        completionCancellation?.Dispose();
        if (_connection is not null) _connection.InputChanged -= OnInputChanged;
        Terminal.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        completionPopup.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        await Terminal.CloseAsync();
    }
}
#endif
