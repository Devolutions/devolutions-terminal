using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Devolutions.Terminal.App.Connections;
using Devolutions.Terminal.App.Views;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Iseberg;
using Iseberg.Core;
using Xunit;
using static Devolutions.Terminal.UI.Tests.PowerShellIseTestHarness;

namespace Devolutions.Terminal.UI.Tests;

public sealed class IsebergNativeContinuationTests
{
    [AvaloniaTheory]
    [InlineData("if ($true) {", "'DT-CONTINUATION-RUN:' + $global:dtContinuationCount }")]
    [InlineData("(", "'DT-CONTINUATION-RUN:' + $global:dtContinuationCount)")]
    [InlineData("$null = @(", "'owned'); 'DT-CONTINUATION-RUN:' + $global:dtContinuationCount")]
    [InlineData("'", "'; 'DT-CONTINUATION-RUN:' + $global:dtContinuationCount")]
    public async Task IncompleteEnterContinuesAndCompletedDraftExecutesOnce(string opening, string closing)
    {
        await InitializeRuntimeAsync();
        await IncompleteEnterCoreAsync(opening, closing);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task IncompleteEnterCoreAsync(string opening, string closing) => WithNativeHostAsync(async tab =>
    {
        await ExecuteTokenAsync(tab, "$global:dtContinuationCount = 40; 'DT-CONTINUATION-BASE:40'", "DT-CONTINUATION-BASE:40");
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        var history = session.History.ToArray();
        var commands = new List<OutputEntry>();
        void OnOutput(OutputEntry entry) => commands.Add(entry);
        engine.Output += OnOutput;
        try
        {
            var initial = "$global:dtContinuationCount += 1; " + opening;
            connection.Write(initial);
            for (var continuation = 1; continuation <= 2; continuation++)
            {
                connection.Write("\r");
                Assert.Equal(initial + new string('\n', continuation), connection.InputText);
                Assert.Equal(connection.InputText, session.Input);
                Assert.Equal(initial.Length + continuation, connection.CaretOffset);
                Assert.Equal(history, session.History);
                Assert.Equal(SessionState.Ready, engine.State);
                Assert.True(connection.IsInputEnabled);
                Assert.Empty(commands);
            }
            await ExecuteTokenAsync(tab, "'DT-CONTINUATION-STILL:' + $global:dtContinuationCount", "DT-CONTINUATION-STILL:40");
            commands.Clear();
            connection.Write(closing + "\r");
            await WaitForNativeOutputAsync(tab, "DT-CONTINUATION-RUN:41");
            await WaitForAsync(() => engine.State == SessionState.Ready && connection.IsInputEnabled,
                () => "Completed continuation did not return to ready input.");
            var expected = initial + "\n\n" + closing;
            Assert.Equal(history.Append(expected), session.History);
            Assert.Single(commands, entry => entry.Kind == OutputKind.Command);
            Assert.Equal("", connection.InputText);
            Assert.Equal("", session.Input);
            Assert.Equal(0, connection.CaretOffset);
            Assert.False(connection.CanUndo);
            Assert.False(connection.CanRedo);
            Assert.Same(engine, session.Engine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
        }
        finally { engine.Output -= OnOutput; }
        await ExecuteTokenAsync(tab, "'DT-CONTINUATION-FINAL:' + $global:dtContinuationCount", "DT-CONTINUATION-FINAL:41");
    });

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData(" \t\n")]
    public async Task EmptyAndWhitespaceEnterDoNotExecuteOrChangeHistory(string draft)
    {
        await InitializeRuntimeAsync();
        await WhitespaceEnterCoreAsync(draft);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task WhitespaceEnterCoreAsync(string draft) => WithNativeHostAsync(async tab =>
    {
        await ExecuteTokenAsync(tab, "$global:dtWhitespaceSentinel = 23; 'DT-WHITESPACE-BASE:23'", "DT-WHITESPACE-BASE:23");
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var connection = NativeConnection(tab);
        session.History.Add("'independent prior command'");
        session.HistoryIndex = 1;
        var history = session.History.ToArray();
        var historyIndex = session.HistoryIndex;
        var entries = new List<OutputEntry>();
        void OnOutput(OutputEntry entry) => entries.Add(entry);
        session.Engine.Output += OnOutput;
        try
        {
            connection.Write("\x1b[200~" + draft + "\x1b[201~");
            Assert.Equal(draft, connection.InputText);
            connection.Write("\r");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal("", connection.InputText);
            Assert.Equal(0, connection.CaretOffset);
            Assert.Equal("", session.Input);
            Assert.Equal(history, session.History);
            Assert.Equal(historyIndex, session.HistoryIndex);
            Assert.Equal(SessionState.Ready, session.Engine.State);
            Assert.True(connection.IsInputEnabled);
            Assert.Empty(entries);
        }
        finally { session.Engine.Output -= OnOutput; }
        await SubmitTokenAsync(tab, "'DT-WHITESPACE-AFTER:' + $global:dtWhitespaceSentinel", "DT-WHITESPACE-AFTER:23");
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPlainAndSecretInputIgnoreHistoryAndCompletionWithoutResolvingResponse(bool secret)
    {
        await InitializeRuntimeAsync();
        await PendingNavigationCoreAsync(secret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task PendingNavigationCoreAsync(bool secret) => WithNativeHostAsync(async tab =>
    {
        await SubmitTokenAsync(tab, "'DT-HISTORY-FIRST'", "DT-HISTORY-FIRST");
        await SubmitTokenAsync(tab, "'DT-HISTORY-SECOND'", "DT-HISTORY-SECOND");
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var connection = NativeConnection(tab);
        connection.Write("\x1b[200~draft\nunfinished\x1b[201~");
        var history = session.History.ToArray();
        var historyIndex = session.HistoryIndex;
        var entered = new TaskCompletionSource<InputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInput(InputRequest request) => entered.TrySetResult(request);
        engine.InputRequested += OnInput;
        Task? execution = null;
        InputRequest? request = null;
        try
        {
            execution = tab.Workbench.ExecuteAsync(secret
                ? "$null = $Host.UI.ReadLineAsSecureString(); 'DT-PENDING-DONE'"
                : "$null = $Host.UI.ReadLine(); 'DT-PENDING-DONE'");
            request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await WaitForAsync(() => connection.HasPendingInput && connection.IsInputEnabled,
                () => "Owned host input was not editable.");
            connection.Write("owned-response");
            var revision = connection.InputRevision;
            foreach (var arrow in new[] { "\x1b[A", "\x1b[B", "\x1bOA", "\x1bOB" })
            {
                connection.Write(arrow);
                Assert.Equal(secret ? "" : "owned-response", connection.InputText);
                Assert.Equal(secret ? 0 : 14, connection.CaretOffset);
                Assert.Equal(historyIndex, session.HistoryIndex);
                Assert.Equal(history, session.History);
                Assert.Equal("draft\nunfinished", session.Input);
                Assert.Equal(revision, connection.InputRevision);
                Assert.False(request.Response.Task.IsCompleted);
                Assert.False(execution.IsCompleted);
            }
            connection.InsertNewLine();
            Assert.Null(await connection.RequestCompletionAsync());
            await connection.CompleteAsync();
            Assert.False(connection.ApplyCompletion(new CompletionSet(-1, -1, []), -1, revision));
            Assert.Equal(revision, connection.InputRevision);
            Assert.Equal(SessionState.Running, engine.State);
            Assert.Equal(secret, connection.IsSecretInput);
            if (secret)
            {
                Assert.False(connection.CanUndo);
                Assert.False(connection.CanRedo);
                Assert.DoesNotContain("owned-response", NativeText(tab), StringComparison.Ordinal);
                Assert.Contains("**************", NativeText(tab), StringComparison.Ordinal);
            }
            connection.Write("\r");
            Assert.Equal("owned-response", await request.Response.Task.WaitAsync(TimeSpan.FromSeconds(60)));
            await execution.WaitAsync(TimeSpan.FromSeconds(60));
            await WaitForAsync(() => connection.IsInputEnabled && !connection.HasPendingInput,
                () => "Input response did not restore the retained draft.");
            Assert.Equal("draft\nunfinished", connection.InputText);
            Assert.Equal(16, connection.CaretOffset);
            Assert.Equal(history, session.History);
            Assert.Equal(historyIndex, session.HistoryIndex);
            Assert.Same(engine, session.Engine);
            await WaitForNativeOutputAsync(tab, "DT-PENDING-DONE");
        }
        finally
        {
            engine.InputRequested -= OnInput;
            request?.Response.TrySetCanceled();
            if (execution is { IsCompleted: false })
            {
                await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
                await execution.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    });

    [AvaloniaFact]
    public async Task MinimumAndRepeatedPositiveResizeExposeExactRawUiWithoutReplacingRunspace()
    {
        await InitializeRuntimeAsync();
        await MinimumResizeCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task MinimumResizeCoreAsync() => WithNativeHostAsync(async tab =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        foreach (var (columns, rows, bufferRows) in new[]
        {
            (1, 1, 3000), (1, 1, 3000), (1, 7, 3000), (9, 1, 3000),
            (37, 3000, 3000), (37, 3001, 3001), (80, 24, 3000)
        })
        {
            connection.Resize(columns, rows);
            Assert.Equal(columns, connection.Columns);
            Assert.Equal(rows, connection.Rows);
            await ExecuteTokenAsync(tab,
                "'DT-MINIMUM:' + $Host.UI.RawUI.WindowSize.Width + ':' + $Host.UI.RawUI.WindowSize.Height + ':' + " +
                "$Host.UI.RawUI.BufferSize.Width + ':' + $Host.UI.RawUI.BufferSize.Height + ':' + " +
                "$Host.UI.RawUI.MaxWindowSize.Width + ':' + $Host.UI.RawUI.MaxWindowSize.Height",
                $"DT-MINIMUM:{columns}:{rows}:{columns}:{bufferRows}:{columns}:{rows}");
            Assert.Same(connection, NativeConnection(tab));
            Assert.Same(engine, session.Engine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
        }
    });

    [AvaloniaTheory]
    [InlineData(0, 24, "columns", 0)]
    [InlineData(-1, 24, "columns", -1)]
    [InlineData(80, 0, "rows", 0)]
    [InlineData(80, -1, "rows", -1)]
    [InlineData(0, 0, "columns", 0)]
    public async Task InvalidResizeRejectsExactAxisWithoutChangingDimensionsOrRunspace(
        int columns, int rows, string parameter, int actualValue)
    {
        await InitializeRuntimeAsync();
        await InvalidResizeCoreAsync(columns, rows, parameter, actualValue);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task InvalidResizeCoreAsync(int columns, int rows, string parameter, int actualValue) =>
        WithNativeHostAsync(async tab =>
        {
            var connection = NativeConnection(tab);
            var session = Assert.Single(tab.Workbench.Workbench.Sessions);
            var runspace = session.Engine.LocalRunspaceId;
            connection.Resize(37, 11);
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => connection.Resize(columns, rows));
            Assert.Equal(parameter, error.ParamName);
            Assert.Equal(actualValue, error.ActualValue);
            Assert.Equal(37, connection.Columns);
            Assert.Equal(11, connection.Rows);
            Assert.Equal(TerminalConnectionState.Connected, connection.State);
            await ExecuteTokenAsync(tab, "'DT-RESIZE-KEPT:' + $Host.UI.RawUI.WindowSize.Width + ':' + $Host.UI.RawUI.WindowSize.Height",
                "DT-RESIZE-KEPT:37:11");
            Assert.Same(connection, NativeConnection(tab));
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);
        });

    [AvaloniaTheory]
    [InlineData("default")]
    [InlineData("options")]
    [InlineData("canceled")]
    public async Task RestartRejectsPreciselyWithoutResettingPersistentSession(string mode)
    {
        await InitializeRuntimeAsync();
        await RestartCoreAsync(mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task RestartCoreAsync(string mode) => WithNativeHostAsync(async tab =>
    {
        await ExecuteTokenAsync(tab, "$global:dtRestartSentinel = 79; 'DT-RESTART-BASE:79'", "DT-RESTART-BASE:79");
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        connection.Write("retained draft");
        var revision = connection.InputRevision;
        var options = mode == "options" ? new TerminalLaunchOptions { CommandLine = "never-launch", Columns = 1, Rows = 1 } : null;
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => connection.RestartAsync(options,
            mode == "canceled" ? new CancellationToken(true) : default));
        Assert.Equal("Duplicate or reopen the Iseberg tab to create a fresh runspace.", error.Message);
        Assert.Equal(TerminalConnectionCapabilities.Resize, connection.Capabilities);
        Assert.Equal(TerminalConnectionState.Connected, connection.State);
        Assert.True(connection.IsRunning);
        Assert.True(connection.IsInputEnabled);
        Assert.Equal("retained draft", connection.InputText);
        Assert.Equal(revision, connection.InputRevision);
        Assert.Same(session, Assert.Single(tab.Workbench.Workbench.Sessions));
        Assert.Same(engine, session.Engine);
        Assert.Equal(runspace, engine.LocalRunspaceId);
        connection.SelectInput();
        connection.DeleteSelection();
        await SubmitTokenAsync(tab, "'DT-RESTART-AFTER:' + $global:dtRestartSentinel", "DT-RESTART-AFTER:79");
    });

    [AvaloniaTheory]
    [InlineData("abcdef", 0, 0, "", "abcdef", 0)]
    [InlineData("abcdef", 0, 1, "a", "bcdef", 0)]
    [InlineData("abcdef", 2, 3, "cde", "abf", 2)]
    [InlineData("abcdef", 0, 6, "abcdef", "", 0)]
    [InlineData("abcdef", 6, 0, "", "abcdef", 6)]
    [InlineData("A🍵B", 1, 1, "🍵", "AB", 1)]
    public async Task NativeCutUsesOnlyEditableSelectionAndOwnedHeadlessClipboard(
        string text, int start, int elements, string selected, string remaining, int caret)
    {
        await InitializeRuntimeAsync();
        await CutCoreAsync(text, start, elements, selected, remaining, caret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CutCoreAsync(string text, int start, int elements, string selected, string remaining, int caret) =>
        WithOwnedConsoleAsync(async (workbench, console, owner, connection) =>
        {
            await workbench.ExecuteAsync("'DT-CUT-PROTECTED'");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var protectedLine = console.Terminal.Engine.CreateSnapshot(includeHistory: true).Buffer.Lines.Single(line =>
                string.Concat(line.Cells.Select(cell => cell.Text)).TrimEnd() == "DT-CUT-PROTECTED");
            var protectedCells = protectedLine.Cells.ToArray();
            var clipboard = owner.Clipboard!;
            await clipboard.SetTextAsync("owned clipboard sentinel");
            connection.Write(text);
            connection.MoveCaret(-1, toBoundary: true);
            for (var index = 0; index < start; index++) connection.MoveCaret(1);
            for (var index = 0; index < elements; index++) connection.MoveCaret(1, extendSelection: true);
            var priorRevision = connection.InputRevision;
            Assert.Equal(selected, connection.SelectedInput);
            Assert.Equal(selected.Length > 0, console.CanCut);
            await console.CutAsync();
            Assert.Equal(selected.Length > 0 ? selected : "owned clipboard sentinel", await clipboard.TryGetTextAsync());
            Assert.Equal(remaining, connection.InputText);
            Assert.Equal(caret, connection.CaretOffset);
            Assert.Equal(0, connection.SelectionLength);
            Assert.Equal(priorRevision + (selected.Length > 0 ? 1 : 0), connection.InputRevision);
            var afterCutLine = console.Terminal.Engine.CreateSnapshot(includeHistory: true).Buffer.Lines.Single(line =>
                string.Concat(line.Cells.Select(cell => cell.Text)).TrimEnd() == "DT-CUT-PROTECTED");
            Assert.Equal(protectedCells, afterCutLine.Cells);
            Assert.Empty(workbench.Workbench.SelectedSession!.History);
            if (selected.Length > 0)
            {
                console.Undo();
                Assert.Equal(text, connection.InputText);
                Assert.Equal(start + selected.Length, connection.CaretOffset);
                console.Redo();
                Assert.Equal(remaining, connection.InputText);
                Assert.Equal(caret, connection.CaretOffset);
                Assert.Equal(selected, await clipboard.TryGetTextAsync());
            }
        });

    [AvaloniaTheory]
    [InlineData("transcript")]
    [InlineData("disabled")]
    [InlineData("secret")]
    [InlineData("clipboard-unavailable")]
    public async Task NativeCutProtectedDisabledSecretAndClipboardFailureNeverDeleteInput(string mode)
    {
        await InitializeRuntimeAsync();
        await CutRejectedCoreAsync(mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CutRejectedCoreAsync(string mode) => WithOwnedConsoleAsync(async (workbench, console, owner, connection) =>
    {
        var clipboard = owner.Clipboard!;
        await clipboard.SetTextAsync("owned protected sentinel");
        var session = Assert.Single(workbench.Workbench.Sessions);
        InputRequest? request = null;
        Task? execution = null;
        var entered = new TaskCompletionSource<InputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInput(InputRequest value) => entered.TrySetResult(value);
        session.Engine.InputRequested += OnInput;
        try
        {
            if (mode == "secret")
            {
                execution = workbench.ExecuteAsync("$null = $Host.UI.ReadLineAsSecureString()");
                request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
                await WaitForAsync(() => connection.HasPendingInput && connection.IsInputEnabled, () => "Secret input not ready.");
            }
            connection.Write("keep");
            if (mode == "transcript")
            {
                console.Terminal.SelectAll();
                Assert.True(console.Terminal.HasSelection);
            }
            else connection.SelectInput();
            if (mode == "disabled") console.RefreshState(true, true);
            var revision = connection.InputRevision;
            if (mode == "clipboard-unavailable")
            {
                Assert.True(console.CanCut);
                owner.Content = null;
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => console.CutAsync());
                Assert.Equal("The console clipboard is unavailable.", error.Message);
                Assert.Equal("keep", connection.SelectedInput);
                owner.Content = workbench;
            }
            else
            {
                Assert.False(console.CanCut);
                await console.CutAsync();
            }
            Assert.Equal(mode == "secret" ? "" : "keep", connection.InputText);
            Assert.Equal(revision, connection.InputRevision);
            Assert.Equal("owned protected sentinel", await clipboard.TryGetTextAsync());
            if (request is not null)
            {
                Assert.False(request.Response.Task.IsCompleted);
                connection.Write("\r");
                Assert.Equal("keep", await request.Response.Task.WaitAsync(TimeSpan.FromSeconds(60)));
                await execution!.WaitAsync(TimeSpan.FromSeconds(60));
            }
            console.RefreshState(true, false);
        }
        finally
        {
            owner.Content = workbench;
            session.Engine.InputRequested -= OnInput;
            request?.Response.TrySetCanceled();
            if (execution is { IsCompleted: false })
            {
                await session.Engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
                await execution.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    });

    [AvaloniaFact]
    public async Task NativeEditableSyntaxUsesIndependentTokenColorsAndPreferenceRedrawPreservesDraft()
    {
        await InitializeRuntimeAsync();
        await NativeSyntaxCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task NativeSyntaxCoreAsync() => WithNativeHostAsync(async tab =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var connection = NativeConnection(tab);
        var theme = EditorThemePresets.Classic();
        foreach (var (key, value) in new[]
        {
            ("Foreground", "#102030"), ("Variable", "#214365"), ("String", "#325476"),
            ("Number", "#436587"), ("Comment", "#547698"), ("Command", "#6587A9"),
            ("Parameter", "#7698BA"), ("Keyword", "#87A9CB"), ("Type", "#98BADC")
        }) theme.Colors["Console." + key] = value;
        connection.ApplyTheme(theme);
        theme.Colors["Console.Variable"] = "#FFFFFF";
        const string draft = "$x = 42; Get-Item -Path 'a' # owned";
        connection.Write(draft);
        void AssertColors()
        {
            var line = Assert.Single(tab.Terminal.Engine.CreateSnapshot().Buffer.Lines, candidate =>
                string.Concat(candidate.Cells.Select(cell => cell.Text)).Contains(draft, StringComparison.Ordinal));
            var offset = string.Concat(line.Cells.Select(cell => cell.Text)).IndexOf(draft, StringComparison.Ordinal);
            Assert.Equal(TermColor.FromRgb(0x21, 0x43, 0x65), line.Cells[offset].Attributes.Foreground);
            Assert.Equal(TermColor.FromRgb(0x10, 0x20, 0x30), line.Cells[offset + 3].Attributes.Foreground);
            Assert.Equal(TermColor.FromRgb(0x43, 0x65, 0x87), line.Cells[offset + 5].Attributes.Foreground);
            Assert.Equal(TermColor.FromRgb(0x65, 0x87, 0xA9), line.Cells[offset + 9].Attributes.Foreground);
            Assert.Equal(TermColor.FromRgb(0x76, 0x98, 0xBA), line.Cells[offset + 18].Attributes.Foreground);
            Assert.Equal(TermColor.FromRgb(0x32, 0x54, 0x76), line.Cells[offset + 24].Attributes.Foreground);
            Assert.Equal(TermColor.FromRgb(0x54, 0x76, 0x98), line.Cells[offset + 28].Attributes.Foreground);
        }
        AssertColors();
        var revision = connection.InputRevision;
        var history = session.History.ToArray();
        foreach (var enabled in new[] { false, true })
        {
            connection.ApplyPreferences(new UserSettings { ConsoleIntelliSense = enabled });
            AssertColors();
            Assert.Equal(draft, connection.InputText);
            Assert.Equal(draft.Length, connection.CaretOffset);
            Assert.Equal(revision, connection.InputRevision);
            Assert.Equal(history, session.History);
            Assert.Equal(SessionState.Ready, session.Engine.State);
        }
        Assert.Equal("preferences", Assert.Throws<ArgumentNullException>(() => connection.ApplyPreferences(null!)).ParamName);
        AssertColors();
        connection.SelectInput();
        connection.Write("if ($true) { [int]$x = 42 } # owned");
        var keywordLine = Assert.Single(tab.Terminal.Engine.CreateSnapshot().Buffer.Lines, candidate =>
            string.Concat(candidate.Cells.Select(cell => cell.Text)).Contains("if ($true) { [int]$x = 42 } # owned", StringComparison.Ordinal));
        var rendered = string.Concat(keywordLine.Cells.Select(cell => cell.Text));
        Assert.Equal(TermColor.FromRgb(0x87, 0xA9, 0xCB), keywordLine.Cells[rendered.IndexOf("if (", StringComparison.Ordinal)].Attributes.Foreground);
        Assert.Equal(TermColor.FromRgb(0x98, 0xBA, 0xDC), keywordLine.Cells[rendered.IndexOf("int]", StringComparison.Ordinal)].Attributes.Foreground);
        Assert.Equal(history, session.History);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    });

    [AvaloniaTheory]
    [InlineData(false, false, "dtPolicyOwnedAlpha")]
    [InlineData(false, true, "dtPolicyOwnedBeta")]
    [InlineData(true, false, "dtPolicyOwnedAlpha")]
    [InlineData(true, true, "dtPolicyOwnedBeta")]
    public async Task NativeManualCompletionRemainsAvailableWhenAutomaticIntelliSenseIsDisabled(
        bool automatic, bool backwards, string selected)
    {
        await InitializeRuntimeAsync();
        await ManualCompletionCoreAsync(automatic, backwards, selected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ManualCompletionCoreAsync(bool automatic, bool backwards, string selected) =>
        WithOwnedConsoleAsync(async (workbench, console, owner, connection) =>
        {
            await workbench.ExecuteAsync("$global:dtPolicyOwnedAlpha = 11; $global:dtPolicyOwnedBeta = 22");
            workbench.Scripting.Options.ShowIntellisenseInConsolePane = automatic;
            connection.Write("$dtPolicyOwned");
            Assert.True(console.Terminal.Focus());
            var revision = connection.InputRevision;
            var runspace = workbench.Workbench.SelectedSession!.Engine.LocalRunspaceId;
            await console.CompleteAsync(backwards);
            var popup = ConsolePopup(console);
            Assert.True(popup.IsOpen);
            var list = Assert.Single(popup.Child!.GetVisualDescendants().OfType<ListBox>());
            Assert.Equal(new[] { "dtPolicyOwnedAlpha", "dtPolicyOwnedBeta" }, list.Items.Cast<string>().ToArray());
            Assert.Equal(selected, list.SelectedItem);
            Assert.Equal("$dtPolicyOwned", connection.InputText);
            Assert.Equal(revision, connection.InputRevision);
            workbench.Scripting.Options.ShowIntellisenseInConsolePane = false;
            Assert.False(popup.IsOpen);
            Assert.Equal("$dtPolicyOwned", connection.InputText);
            // Explicit invocation is supported independently of the automatic-popup preference.
            await console.CompleteAsync(backwards);
            Assert.True(popup.IsOpen);
            owner.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            owner.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.False(popup.IsOpen);
            Assert.Equal("$" + selected, connection.InputText);
            Assert.Equal(1 + selected.Length, connection.CaretOffset);
            Assert.Equal(revision + 1, connection.InputRevision);
            console.Undo();
            Assert.Equal("$dtPolicyOwned", connection.InputText);
            Assert.Equal(runspace, workbench.Workbench.SelectedSession.Engine.LocalRunspaceId);
            Assert.Empty(workbench.Workbench.SelectedSession.History);
        });

    [AvaloniaFact]
    public async Task NativeAutomaticCompletionCanBeDisabledAndReenabledOnTheSameEngine()
    {
        await InitializeRuntimeAsync();
        await AutomaticCompletionCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task AutomaticCompletionCoreAsync() => WithOwnedConsoleAsync(async (workbench, console, _, connection) =>
    {
        await workbench.ExecuteAsync("$global:dtAutomaticOwnedObject = [pscustomobject]@{ OwnedAlpha = 11; OwnedBeta = 22 }");
        var popup = ConsolePopup(console);
        workbench.Scripting.Options.ShowIntellisenseInConsolePane = false;
        Assert.True(console.Terminal.Focus());
        connection.Write("$dtAutomaticOwnedObject");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(popup.IsOpen);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPopupChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Popup.IsOpenProperty && popup.IsOpen) opened.TrySetResult();
        }
        popup.PropertyChanged += OnPopupChanged;
        try
        {
            workbench.Scripting.Options.ShowIntellisenseInConsolePane = true;
            Assert.True(console.Terminal.IsKeyboardFocusWithin);
            connection.Write(".");
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var list = Assert.Single(popup.Child!.GetVisualDescendants().OfType<ListBox>());
            Assert.Contains("OwnedAlpha", list.Items.Cast<string>());
            Assert.Contains("OwnedBeta", list.Items.Cast<string>());
            Assert.Equal("$dtAutomaticOwnedObject.", connection.InputText);
            workbench.Scripting.Options.ShowIntellisenseInConsolePane = false;
            Assert.False(popup.IsOpen);
            connection.Write("OwnedAlpha");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(popup.IsOpen);
            Assert.Equal("$dtAutomaticOwnedObject.OwnedAlpha", connection.InputText);
            Assert.Empty(workbench.Workbench.SelectedSession!.History);
        }
        finally { popup.PropertyChanged -= OnPopupChanged; }
    });

    [AvaloniaTheory]
    [InlineData("\n")]
    [InlineData("\n'owned draft'")]
    public async Task LeadingNewlinePasteAfterNativeEnterPreservesExactUnsubmittedDraft(string draft)
    {
        await InitializeRuntimeAsync();
        await LeadingNewlinePasteCoreAsync(draft);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task LeadingNewlinePasteCoreAsync(string draft) => WithNativeHostAsync(async tab =>
    {
        await SubmitTokenAsync(tab, "'DT-LEADING-PASTE-BASE'", "DT-LEADING-PASTE-BASE");
        var connection = NativeConnection(tab);
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var history = session.History.ToArray();
        Assert.Equal(TerminalPasteResult.Written, await tab.Terminal.PasteTextAsync(draft));
        Assert.Equal(draft, connection.InputText);
        Assert.Equal(draft, session.Input);
        Assert.Equal(draft.Length, connection.CaretOffset);
        Assert.Equal(history, session.History);
        Assert.Equal(SessionState.Ready, session.Engine.State);
        Assert.True(connection.IsInputEnabled);
    });

    [AvaloniaTheory]
    [InlineData("unfocused")]
    [InlineData("empty")]
    [InlineData("no-matches")]
    [InlineData("pending")]
    [InlineData("disposed")]
    public async Task NativeManualCompletionIgnoresUnavailableUnfocusedAndEmptyResults(string mode)
    {
        await InitializeRuntimeAsync();
        await UnavailableCompletionCoreAsync(mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task UnavailableCompletionCoreAsync(string mode) =>
        WithOwnedConsoleAsync(async (workbench, console, _, connection) =>
        {
            var session = Assert.Single(workbench.Workbench.Sessions);
            await workbench.ExecuteAsync("$global:dtUnavailableOwnedValue = 32");
            var popup = ConsolePopup(console);
            InputRequest? request = null;
            Task? execution = null;
            var entered = new TaskCompletionSource<InputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnInput(InputRequest value) => entered.TrySetResult(value);
            session.Engine.InputRequested += OnInput;
            try
            {
                if (mode == "pending")
                {
                    execution = workbench.ExecuteAsync("$null = $Host.UI.ReadLine()");
                    request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
                    await WaitForAsync(() => connection.HasPendingInput && connection.IsInputEnabled,
                        () => "Owned input barrier did not arrive.");
                }
                if (mode != "empty") connection.Write(mode == "no-matches" ? "$dtNoSuchOwnedVariable98765" : "$dtUnavailableOwnedVal");
                if (mode == "unfocused")
                {
                    Assert.True(workbench.ScriptEditorView.FocusEditor());
                    Assert.False(console.Terminal.IsKeyboardFocusWithin);
                }
                else Assert.True(console.Terminal.Focus());
                var draft = connection.InputText;
                var caret = connection.CaretOffset;
                var revision = connection.InputRevision;
                if (mode == "disposed") await workbench.DisposeAsync();
                await console.CompleteAsync();
                Assert.False(popup.IsOpen);
                Assert.Equal(mode == "disposed" ? "" : draft, connection.InputText);
                Assert.Equal(caret, connection.CaretOffset);
                Assert.Equal(revision, connection.InputRevision);
                Assert.Empty(session.History);
                if (request is not null)
                {
                    Assert.False(request.Response.Task.IsCompleted);
                    connection.Write("\r");
                    Assert.Equal("$dtUnavailableOwnedVal", await request.Response.Task.WaitAsync(TimeSpan.FromSeconds(60)));
                    await execution!.WaitAsync(TimeSpan.FromSeconds(60));
                }
            }
            finally
            {
                session.Engine.InputRequested -= OnInput;
                request?.Response.TrySetCanceled();
                if (execution is { IsCompleted: false })
                {
                    await session.Engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    await execution.WaitAsync(TimeSpan.FromSeconds(60));
                }
            }
        });

    [AvaloniaFact]
    public async Task BracketedLeadingLfAfterEnterIsNotMistakenForCrLfContinuation()
    {
        await InitializeRuntimeAsync();
        await BracketedLeadingLfCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task BracketedLeadingLfCoreAsync() => WithNativeHostAsync(async tab =>
    {
        await SubmitTokenAsync(tab, "'DT-BRACKET-LF-BASE'", "DT-BRACKET-LF-BASE");
        var connection = NativeConnection(tab);
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var history = session.History.ToArray();
        connection.Write("\x1b[200~\n'owned draft'\x1b[201~");
        Assert.Equal("\n'owned draft'", connection.InputText);
        Assert.Equal(14, connection.CaretOffset);
        Assert.Equal(history, session.History);
        Assert.Equal(SessionState.Ready, session.Engine.State);
    });

    [AvaloniaFact]
    public async Task MinimumNativeBufferAndRawUiResizeKeepTheOriginalEngineUsable()
    {
        await InitializeRuntimeAsync();
        await MinimumNativeBufferCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task MinimumNativeBufferCoreAsync() => WithNativeHostAsync(async tab =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var connection = NativeConnection(tab);
        var runspace = session.Engine.LocalRunspaceId;
        var original = tab.Terminal.Engine.CreateSnapshot().Buffer;
        var output = new List<OutputEntry>();
        void OnOutput(OutputEntry entry) => output.Add(entry);
        session.Engine.Output += OnOutput;
        try
        {
            // The VT engine owns the display buffer; transport Resize independently updates RawUI.
            tab.Terminal.Engine.Resize(1, 1);
            connection.Resize(1, 1);
            var minimum = tab.Terminal.Engine.CreateSnapshot().Buffer;
            Assert.Equal(1, minimum.Columns);
            Assert.Equal(1, minimum.Rows);
            Assert.Equal(1, connection.Columns);
            Assert.Equal(1, connection.Rows);
            await tab.Workbench.ExecuteAsync(
                "'DT-NATIVE-MINIMUM:' + $Host.UI.RawUI.WindowSize.Width + ':' + $Host.UI.RawUI.WindowSize.Height + ':' + " +
                "$Host.UI.RawUI.BufferSize.Width + ':' + $Host.UI.RawUI.BufferSize.Height");
            Assert.Contains(output, entry => entry.Kind == OutputKind.Output &&
                entry.Text.TrimEnd('\r', '\n') == "DT-NATIVE-MINIMUM:1:1:1:3000");
            Assert.Same(connection, NativeConnection(tab));
            Assert.Same(session, Assert.Single(tab.Workbench.Workbench.Sessions));
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);
            Assert.Equal(SessionState.Ready, session.Engine.State);
        }
        finally
        {
            session.Engine.Output -= OnOutput;
            tab.Terminal.Engine.Resize(original.Columns, original.Rows);
            connection.Resize(original.Columns, original.Rows);
        }
        await SubmitTokenAsync(tab, "'DT-MINIMUM-STILL-USABLE'", "DT-MINIMUM-STILL-USABLE");
    });

    [AvaloniaFact]
    public async Task ParenthesizedContinuationCompletedAfterFirstEnterExecutesExactlyOnce()
    {
        await InitializeRuntimeAsync();
        await ParenthesizedContinuationCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ParenthesizedContinuationCoreAsync() => WithNativeHostAsync(async tab =>
    {
        await ExecuteTokenAsync(tab, "$global:dtParenthesisCounter = 15; 'DT-PARENTHESIS-BASE:15'", "DT-PARENTHESIS-BASE:15");
        var connection = NativeConnection(tab);
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var runspace = session.Engine.LocalRunspaceId;
        connection.Write("$global:dtParenthesisCounter += 1; (");
        connection.Write("\r");
        Assert.Equal("$global:dtParenthesisCounter += 1; (\n", connection.InputText);
        Assert.Empty(session.History);
        Assert.Equal(SessionState.Ready, session.Engine.State);
        connection.Write("'DT-PARENTHESIS-RUN:' + $global:dtParenthesisCounter)\r");
        await WaitForNativeOutputAsync(tab, "DT-PARENTHESIS-RUN:16");
        await WaitForAsync(() => session.Engine.State == SessionState.Ready && connection.IsInputEnabled,
            () => "Completed parenthesized command did not finish.");
        Assert.Equal("$global:dtParenthesisCounter += 1; (\n'DT-PARENTHESIS-RUN:' + $global:dtParenthesisCounter)",
            Assert.Single(session.History));
        Assert.Equal("", connection.InputText);
        Assert.Equal(0, connection.CaretOffset);
        Assert.Equal(runspace, session.Engine.LocalRunspaceId);
        await ExecuteTokenAsync(tab, "'DT-PARENTHESIS-FINAL:' + $global:dtParenthesisCounter", "DT-PARENTHESIS-FINAL:16");
    });

    [AvaloniaFact]
    public async Task CompleteInvalidInputPublishesOneParseErrorWithoutEnteringContinuation()
    {
        await InitializeRuntimeAsync();
        await CompleteInvalidInputCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CompleteInvalidInputCoreAsync() => WithNativeHostAsync(async tab =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var connection = NativeConnection(tab);
        var output = new List<OutputEntry>();
        void OnOutput(OutputEntry entry) => output.Add(entry);
        session.Engine.Output += OnOutput;
        try
        {
            const string script = "1 + * 2";
            connection.Write(script + "\r");
            await WaitForAsync(() => session.Engine.State == SessionState.Ready && connection.IsInputEnabled &&
                output.Any(entry => entry.Kind == OutputKind.Error), () => "Invalid native command did not report its parse error.");
            Assert.Equal(script, Assert.Single(session.History));
            Assert.Single(output, entry => entry.Kind == OutputKind.Command);
            var error = Assert.Single(output, entry => entry.Kind == OutputKind.Error);
            Assert.Contains("You must provide a value expression following the '+' operator.", error.Text, StringComparison.Ordinal);
            Assert.Equal("", connection.InputText);
            Assert.Equal("", session.Input);
            Assert.Equal(0, connection.CaretOffset);
        }
        finally { session.Engine.Output -= OnOutput; }
        await SubmitTokenAsync(tab, "'DT-INVALID-STILL-USABLE'", "DT-INVALID-STILL-USABLE");
    });

    private static Popup ConsolePopup(DtIsebergConsole console) =>
        Assert.Single(console.View.GetLogicalDescendants().OfType<Popup>(),
            popup => ReferenceEquals(popup.PlacementTarget, console.Terminal));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task WithNativeHostAsync(Func<PowerShellIseTab, Task> test)
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        await test(tab);
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task WithOwnedConsoleAsync(Func<WorkbenchControl, DtIsebergConsole, Window, IsebergTerminalConnection, Task> test)
    {
        using var environment = new IseTestEnvironment();
        var terminal = new TermControl();
        var console = new DtIsebergConsole(terminal, terminal, environment.Profile());
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            Console = console, ShowSessionTabs = false, EnableCommandsPane = false,
            SnippetDirectory = Path.Combine(environment.DirectoryPath, "snippets"),
            StartingDirectory = environment.DirectoryPath,
            Preferences = new UserSettings
            {
                LoadProfiles = false, CheckForUpdates = false, AutoSaveMinutes = 0, ConsoleIntelliSense = false
            }
        });
        var errors = new List<Exception>();
        workbench.ErrorOccurred += (_, error) => errors.Add(error.Exception);
        var owner = new Window { Width = 1100, Height = 800, Content = workbench };
        try
        {
            owner.Show();
            await workbench.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
            owner.UpdateLayout();
            var connection = Assert.IsType<IsebergTerminalConnection>(terminal.ConnectionFactory!(terminal.Profile!));
            await WaitForAsync(() => connection.IsInputEnabled, () => "Owned console did not become editable.");
            await test(workbench, console, owner, connection);
            Assert.Empty(errors);
        }
        finally
        {
            try { await workbench.DisposeAsync(); }
            finally { owner.Content = null; owner.Close(); }
        }
    }
}
