using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Devolutions.Terminal.App.Connections;
using Devolutions.Terminal.App.Views;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;
using Iseberg;
using Iseberg.Core;
using Iseberg.Editor;
using Xunit;
using static Devolutions.Terminal.UI.Tests.PowerShellIseTestHarness;

namespace Devolutions.Terminal.UI.Tests;

public sealed class IsebergTerminalBridgeTests
{
    [AvaloniaFact]
    public async Task ScriptAndNativeConsoleSharePersistentRunspace()
    {
        await InitializeRuntimeAsync();
        await SharedRunspaceCoreAsync();
    }

    [AvaloniaFact]
    public async Task NativeTerminalReplacesTranscriptInResizableHorizontalSplit()
    {
        await InitializeRuntimeAsync();
        await LayoutCoreAsync();
    }

    [AvaloniaTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task MultilinePasteWaitsForExplicitEnter(string newline)
    {
        await InitializeRuntimeAsync();
        await PasteCoreAsync(newline);
    }

    [AvaloniaFact]
    public async Task NativeReadHostAcceptsResponseAndKeepsRunspaceUsable()
    {
        await InitializeRuntimeAsync();
        await ReadHostCoreAsync(stop: false);
    }

    [AvaloniaFact]
    public async Task NativeCtrlCStopsPendingReadHostAndRestoresInput()
    {
        await InitializeRuntimeAsync();
        await ReadHostCoreAsync(stop: true);
    }

    [AvaloniaFact]
    public async Task NativeCtrlCStopsRunningCommandAndRestoresInput()
    {
        await InitializeRuntimeAsync();
        await RunningStopCoreAsync();
    }

    [AvaloniaFact]
    public async Task NativeStartupRejectsCommandsUntilWorkbenchInitializationCompletes()
    {
        await InitializeRuntimeAsync();
        await StartupInputCoreAsync();
    }

    [AvaloniaFact]
    public async Task ForcedDisposalCancelsPendingReadHostAndReleasesNativeEngine()
    {
        await InitializeRuntimeAsync();
        await PendingCloseCoreAsync(forceDispose: true);
    }

    [AvaloniaFact]
    public async Task PendingReadHostCloseCancelKeepsNativeRequestUsable()
    {
        await InitializeRuntimeAsync();
        await PendingCloseCoreAsync(forceDispose: false);
    }

    [AvaloniaTheory]
    [InlineData("caf\u00e9")]
    [InlineData("tea \ud83c\udf75")]
    public async Task SplitUtf8BytesDecodeWithoutReplacement(string value)
    {
        await InitializeRuntimeAsync();
        await Utf8CoreAsync(value);
    }

    [AvaloniaFact]
    public async Task ResizeUpdatesRawUiColumnsWithoutReplacingEngine()
    {
        await InitializeRuntimeAsync();
        await ResizeCoreAsync();
    }

    [AvaloniaTheory]
    [InlineData("Classic ISE", 0xFFFFFFFFu, 0xFF012456u, 0x00FF0000u, 0xFFFFFFFFu, 0xFF012456u, 0x00FF0000u)]
    [InlineData("Dark", 0xFFD4D4D4u, 0xFF1E1E1Eu, 0x00F48771u, 0xFFD4D4D4u, 0xFF1E1E1Eu, 0x00F48771u)]
    [InlineData("Light", 0xFF202020u, 0xFFFFFFFFu, 0x00FF0000u, 0xFF202020u, 0xFFFFFFFFu, 0x00FF0000u)]
    [InlineData("Follow DT", 0xFF202020u, 0xFFFFFFFFu, 0x00FF0000u, 0xFFD4D4D4u, 0xFF1E1E1Eu, 0x00F48771u)]
    public async Task NativeConsolePaletteUsesSelectedThemeAndTracksOnlyFollowDt(
        string theme, uint lightForeground, uint lightBackground, uint lightError,
        uint darkForeground, uint darkBackground, uint darkError)
    {
        await InitializeRuntimeAsync();
        await ThemePaletteCoreAsync(theme, lightForeground, lightBackground, lightError,
            darkForeground, darkBackground, darkError);
    }

    [AvaloniaTheory]
    [InlineData("Future Theme")]
    [InlineData("")]
    public async Task UnsupportedIseThemeIsRejectedBeforeWorkbenchConstruction(string theme)
    {
        await InitializeRuntimeAsync();
        await UnsupportedThemeCoreAsync(theme);
    }

    [AvaloniaFact]
    public async Task FollowDtThemeChangeAfterDisposalDoesNotReactivateNativeConsole()
    {
        await InitializeRuntimeAsync();
        await DisposedThemeCoreAsync();
    }

    [AvaloniaTheory]
    [InlineData("Light", true, 0xFFF7F7F7u, 0xFF202020u)]
    [InlineData("Dark", false, 0xFF252526u, 0xFFF0F0F0u)]
    public async Task CompletionAndDescriptionFollowFixedEditorThemeInsteadOfOppositeHost(
        string theme, bool darkHost, uint descriptionBackground, uint descriptionForeground)
    {
        await InitializeRuntimeAsync();
        await CompletionThemeCoreAsync(theme, darkHost, descriptionBackground, descriptionForeground);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CompletionThemeCoreAsync(
        string theme, bool darkHost, uint descriptionBackground, uint descriptionForeground)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = theme;
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        var expectedTheme = darkHost ? ThemeVariant.Light : ThemeVariant.Dark;
        host.Owner.RequestedThemeVariant = darkHost ? ThemeVariant.Dark : ThemeVariant.Light;
        var referenceList = new ListBox { ItemsSource = new[] { "Reference completion" } };
        Assert.IsAssignableFrom<Panel>(host.Owner.Content).Children.Add(
            new ThemeVariantScope { RequestedThemeVariant = expectedTheme, Child = referenceList });
        await host.InitializeAsync();
        tab.Workbench.CreateDocument("Get-DT");
        var editor = tab.Workbench.ScriptEditorView;
        editor.CaretOffset = editor.Document.TextLength;
        editor.CompletionProvider = new PopupThemeCompletionProvider();
        Assert.True(editor.FocusEditor());
        try
        {
            await editor.ShowCompletionAsync();
            var completion = editor.CompletionPopup;
            Assert.NotNull(completion);
            Assert.Same(editor.TextEditor.TextArea, completion.GetLogicalParent());
            var descriptionPopup = Assert.Single(completion.GetLogicalChildren().OfType<Popup>());
            await WaitForAsync(() => completion.IsOpen && descriptionPopup.IsOpen,
                () => "Completion list and selected item's description did not open.");
            var description = Assert.IsAssignableFrom<ContentControl>(descriptionPopup.Child);
            var descriptionRoot = TopLevel.GetTopLevel(description);
            Assert.NotNull(descriptionRoot);
            descriptionRoot.UpdateLayout();
            var list = completion.CompletionList.ListBox;
            Assert.NotNull(list);
            Assert.NotEqual(expectedTheme, host.Owner.ActualThemeVariant);
            Assert.Equal(expectedTheme, editor.TextEditor.TextArea.ActualThemeVariant);
            Assert.Equal(expectedTheme, completion.CompletionList.ActualThemeVariant);
            Assert.Equal(expectedTheme, list.ActualThemeVariant);
            Assert.Equal(expectedTheme, description.ActualThemeVariant);
            Assert.Equal(expectedTheme, referenceList.ActualThemeVariant);
            var selected = completion.CompletionList.SelectedItem;
            Assert.NotNull(selected);
            Assert.Equal("Get-DTCompletion", selected.Text);
            Assert.Equal("DT completion description", description.Content);
            Assert.True(list.IsVisible);
            Assert.True(description.IsVisible);
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(referenceList.Background).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(list.Background).Color);
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(referenceList.Foreground).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(list.Foreground).Color);
            static Color Argb(uint value) => Color.FromArgb(
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            Assert.Equal(Argb(descriptionBackground), Assert.IsAssignableFrom<ISolidColorBrush>(description.Background).Color);
            Assert.Equal(Argb(descriptionForeground), Assert.IsAssignableFrom<ISolidColorBrush>(description.Foreground).Color);
            Assert.Equal(1d, description.Opacity);
            editor.CloseCompletion();
            Assert.False(completion.IsOpen);
            Assert.False(descriptionPopup.IsOpen);
            Assert.Null(completion.GetLogicalParent());
            Assert.Null(editor.CompletionPopup);
            Assert.True(host.Owner.IsVisible);

            await editor.ShowCompletionAsync();
            var reopened = editor.CompletionPopup;
            Assert.NotNull(reopened);
            Assert.NotSame(completion, reopened);
            Assert.Same(editor.TextEditor.TextArea, reopened.GetLogicalParent());
            var reopenedDescription = Assert.Single(reopened.GetLogicalChildren().OfType<Popup>());
            await WaitForAsync(() => reopened.IsOpen && reopenedDescription.IsOpen,
                () => "Completion and description did not reopen after detaching the first popup.");
            host.Owner.Close();
            await WaitForAsync(() => !reopened.IsOpen && !reopenedDescription.IsOpen,
                () => "Closing the owner left a completion or description popup open.");
            Assert.False(host.Owner.IsVisible);
            Assert.False(reopened.IsOpen);
            Assert.False(reopenedDescription.IsOpen);
            Assert.Null(reopened.GetLogicalParent());
            Assert.Null(editor.CompletionPopup);
            Assert.Empty(host.Errors);
        }
        finally { editor.CloseCompletion(); }
    }

    private sealed class PopupThemeCompletionProvider : IEditorCompletionProvider
    {
        public Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EditorCompletionList(request.Version, new EditorTextSpan(0, request.Text.Length),
                [new EditorCompletionItem("Get-DTCompletion", "Get-DTCompletion", "DT completion description")]));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DisposedThemeCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = "Follow DT";
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        host.Owner.RequestedThemeVariant = ThemeVariant.Light;
        await host.InitializeAsync();
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var connection = NativeConnection(tab);
        await tab.DisposeAsync();
        Assert.True(tab.IsDisposed);
        Assert.Equal(SessionState.Disposed, session.Engine.State);
        Assert.Equal(TerminalConnectionState.Disposed, connection.State);
        Assert.False(tab.Terminal.IsRunning);
        Assert.Null(Record.Exception(() => host.Owner.RequestedThemeVariant = ThemeVariant.Dark));
        Assert.Equal(SessionState.Disposed, session.Engine.State);
        Assert.Equal(TerminalConnectionState.Disposed, connection.State);
        Assert.False(tab.Terminal.IsRunning);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task UnsupportedThemeCoreAsync(string theme)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = theme;
        var error = Assert.Throws<NotSupportedException>(() => new PowerShellIseTab(profile));
        Assert.Contains("color theme", error.Message, StringComparison.Ordinal);
        Assert.Equal(theme, profile.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ThemePaletteCoreAsync(
        string theme, uint lightForeground, uint lightBackground, uint lightError,
        uint darkForeground, uint darkBackground, uint darkError)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = theme;
        profile.ColorScheme = "One Half Dark";
        var originalScheme = profile.ResolveScheme();
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        host.Owner.RequestedThemeVariant = ThemeVariant.Light;
        await host.InitializeAsync();
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        await ExecuteTokenAsync(tab, "$global:dtThemeValue = 'retained'; 'DT-THEME-INITIALIZED'", "DT-THEME-INITIALIZED");
        var step = 0;
        foreach (var dark in new[] { false, true, false })
        {
            host.Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            host.Owner.UpdateLayout();
            var foreground = dark ? darkForeground : lightForeground;
            var background = dark ? darkBackground : lightBackground;
            var errorColor = dark ? darkError : lightError;
            await WaitForAsync(() => tab.Terminal.Engine.Scheme.Foreground == foreground &&
                                     tab.Terminal.Engine.Scheme.Background == background,
                () => $"Theme {theme} under {(dark ? "dark" : "light")} DT has native colors " +
                      $"{tab.Terminal.Engine.Scheme.Foreground:X8}/{tab.Terminal.Engine.Scheme.Background:X8}.");
            var palette = tab.Terminal.Engine.Scheme;
            Assert.Equal(foreground, palette.Foreground);
            Assert.Equal(background, palette.Background);
            Assert.Equal(foreground, palette.Cursor);
            Assert.Equal(originalScheme.Table, palette.Table);
            Assert.NotEqual(originalScheme.Background, palette.Background);
            var token = $"DT-THEME-ERROR-{step}";
            connection.WriteOutput(new OutputEntry(token + "\n", OutputKind.Error));
            await WaitForNativeOutputAsync(tab, token);
            var line = Assert.Single(tab.Terminal.Engine.CreateSnapshot(includeHistory: true).Buffer.Lines,
                candidate => string.Concat(candidate.Cells.Select(cell => cell.Text)).TrimEnd() == token);
            var expectedError = TermColor.FromRgb((byte)(errorColor >> 16), (byte)(errorColor >> 8), (byte)errorColor);
            Assert.All(line.Cells.Take(token.Length), cell => Assert.Equal(expectedError, cell.Attributes.Foreground));
            connection.RefreshState(acceptsCommands: true, inputDisabled: false);
            await SubmitTokenAsync(tab, $"'DT-THEME-STATE-{step}:' + $global:dtThemeValue", $"DT-THEME-STATE-{step}:retained");
            Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Same(connection, NativeConnection(tab));
            Assert.True(tab.Terminal.IsRunning);
            step++;
        }
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SharedRunspaceCoreAsync() => WithHostAsync(async (tab, _) =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        await ExecuteTokenAsync(tab, "$global:dtSharedValue = 'from-script'; 'DT-SCRIPT-ASSIGNED'", "DT-SCRIPT-ASSIGNED");
        await SubmitTokenAsync(tab, "'DT-TYPED-READ:' + $global:dtSharedValue", "DT-TYPED-READ:from-script");
        await SubmitTokenAsync(tab, "$global:dtSharedValue = 'from-console'; 'DT-CONSOLE-ASSIGNED'", "DT-CONSOLE-ASSIGNED");
        await ExecuteTokenAsync(tab, "'DT-SCRIPT-READ:' + $global:dtSharedValue", "DT-SCRIPT-READ:from-console");
        Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
        Assert.Equal(runspace, engine.LocalRunspaceId);
        Assert.Same(connection, NativeConnection(tab));
        Assert.Equal(TerminalConnectionState.Connected, connection.State);
        Assert.True(tab.Terminal.IsRunning);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task LayoutCoreAsync() => WithHostAsync(async (tab, host) =>
    {
        host.Owner.UpdateLayout();
        Assert.Same(tab.Terminal, Assert.Single(tab.Workbench.GetVisualDescendants().OfType<TermControl>()));
        Assert.Same(host.Owner, TopLevel.GetTopLevel(tab.Terminal));
        Assert.True(tab.Terminal.IsEffectivelyVisible);
        Assert.True(tab.Terminal.IsRunning);
        Assert.Equal("Iseberg PowerShell terminal", tab.Terminal.AccessibleName);
        var legacy = tab.Workbench.FindControl<Control>("ConsoleEditor");
        Assert.NotNull(legacy);
        Assert.False(legacy.IsEffectivelyVisible);
        var script = tab.Workbench.FindControl<Grid>("ScriptPane");
        var console = tab.Workbench.FindControl<Grid>("ConsolePane");
        var splitter = tab.Workbench.FindControl<GridSplitter>("PaneSplitter");
        Assert.NotNull(script);
        Assert.NotNull(console);
        Assert.NotNull(splitter);
        Assert.Same(script.Parent, console.Parent);
        Assert.Same(script.Parent, splitter.Parent);
        Assert.Equal(0, Grid.GetRow(script));
        Assert.Equal(1, Grid.GetRow(splitter));
        Assert.Equal(2, Grid.GetRow(console));
        Assert.Equal(Grid.GetColumn(script), Grid.GetColumn(console));
        Assert.True(splitter.IsEffectivelyVisible);
        var scriptPosition = script.TranslatePoint(default, host.Owner);
        var consolePosition = console.TranslatePoint(default, host.Owner);
        Assert.NotNull(scriptPosition);
        Assert.NotNull(consolePosition);
        Assert.True(scriptPosition.Value.Y + script.Bounds.Height < consolePosition.Value.Y);
        var upperHeight = script.Bounds.Height;
        var lowerHeight = console.Bounds.Height;
        var center = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), host.Owner);
        Assert.NotNull(center);
        var moved = new Point(center.Value.X, center.Value.Y + 32);
        host.Owner.MouseDown(center.Value, MouseButton.Left);
        host.Owner.MouseMove(moved);
        host.Owner.MouseUp(moved, MouseButton.Left);
        host.Owner.UpdateLayout();
        await WaitForAsync(() => script.Bounds.Height > upperHeight && console.Bounds.Height < lowerHeight,
            () => $"Divider did not resize vertically: upper {upperHeight}->{script.Bounds.Height}, lower {lowerHeight}->{console.Bounds.Height}.");
        Assert.True(script.Bounds.Height > upperHeight);
        Assert.True(console.Bounds.Height < lowerHeight);
        await SubmitTokenAsync(tab, "'DT-RESIZED-NATIVE-PANE'", "DT-RESIZED-NATIVE-PANE");
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task PasteCoreAsync(string newline) => WithHostAsync(async (tab, _) =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        await ExecuteTokenAsync(tab, "$global:dtPasteCounter = 0; 'DT-PASTE-BASE:0'", "DT-PASTE-BASE:0");
        Assert.True(tab.Terminal.Engine.BracketedPaste);
        var historyCount = session.History.Count;
        var script = "$global:dtPasteCounter += 1" + newline +
                     "'DT-PASTE-RUN:' + $global:dtPasteCounter" + newline;
        var expectedDraft = "$global:dtPasteCounter += 1\n'DT-PASTE-RUN:' + $global:dtPasteCounter\n";
        Assert.Equal(TerminalPasteResult.Written, await tab.Terminal.PasteTextAsync(script));
        Assert.Equal(expectedDraft, session.Input);
        Assert.Equal(historyCount, session.History.Count);
        Assert.Equal(SessionState.Ready, engine.State);
        Assert.DoesNotContain("DT-PASTE-RUN:1", NativeLines(tab));
        await ExecuteTokenAsync(tab, "'DT-PASTE-BEFORE:' + $global:dtPasteCounter", "DT-PASTE-BEFORE:0");
        Assert.Equal(expectedDraft, session.Input);
        Assert.Equal(historyCount, session.History.Count);
        tab.Terminal.WriteInput("\r");
        await WaitForNativeOutputAsync(tab, "DT-PASTE-RUN:1");
        await WaitForAsync(() => engine.State == SessionState.Ready && NativeConnection(tab).IsInputEnabled,
            () => $"Pasted command did not finish: {engine.State}.");
        Assert.Equal(historyCount + 1, session.History.Count);
        Assert.Equal(expectedDraft, session.History[^1]);
        Assert.Equal("", session.Input);
        await ExecuteTokenAsync(tab, "'DT-PASTE-FINAL:' + $global:dtPasteCounter", "DT-PASTE-FINAL:1");
        Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ReadHostCoreAsync(bool stop) => WithHostAsync(async (tab, host) =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var connection = NativeConnection(tab);
        var requestRaised = new TaskCompletionSource<InputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInput(InputRequest request) => requestRaised.TrySetResult(request);
        engine.InputRequested += OnInput;
        Task? execution = null;
        try
        {
            execution = tab.Workbench.ExecuteAsync(stop
                ? "$global:dtAfterStop = 0; $null = Read-Host 'DT-stop-question'; $global:dtAfterStop = 999"
                : "$global:dtReadAnswer = Read-Host 'DT-response-question'; 'DT-ANSWER:' + $global:dtReadAnswer");
            var request = await requestRaised.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await WaitForAsync(() => connection.IsInputEnabled && engine.State == SessionState.Running,
                () => $"Read-Host input unavailable: input={connection.IsInputEnabled}, state={engine.State}.");
            Assert.False(execution.IsCompleted);
            Assert.False(request.Response.Task.IsCompleted);
            Assert.Empty(host.Owner.OwnedWindows);
            tab.Terminal.WriteInput(stop ? "\u0003" : "native-response-42\r");
            if (stop)
            {
                await WaitForAsync(() => request.Response.Task.IsCompleted,
                    () => "Ctrl+C did not release the pending host input.");
                Assert.True(request.Response.Task.IsCanceled);
            }
            else
            {
                Assert.Equal("native-response-42", await request.Response.Task.WaitAsync(TimeSpan.FromSeconds(60)));
            }
            await execution.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(SessionState.Ready, engine.State);
            Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
            Assert.Same(connection, NativeConnection(tab));
            Assert.Equal(TerminalConnectionState.Connected, connection.State);
            await ExecuteTokenAsync(tab, stop
                ? "'DT-STOP-AFTER:' + $global:dtAfterStop"
                : "'DT-RESPONSE-AFTER:' + $global:dtReadAnswer",
                stop ? "DT-STOP-AFTER:0" : "DT-RESPONSE-AFTER:native-response-42");
            if (!stop) await WaitForNativeOutputAsync(tab, "DT-ANSWER:native-response-42");
            await SubmitTokenAsync(tab, "'DT-READHOST-STILL-USABLE'", "DT-READHOST-STILL-USABLE");
        }
        finally
        {
            engine.InputRequested -= OnInput;
            if (execution is { IsCompleted: false })
            {
                await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
                await execution.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task StartupInputCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        PowerShellSession? engine = null;
        IsebergTerminalConnection? connection = null;
        bool? startupInputEnabled = null;
        bool? startupComplete = null;
        string? startupText = null;
        void OnState(SessionState state)
        {
            if (state != SessionState.Ready || startupInputEnabled.HasValue) return;
            connection = NativeConnection(tab);
            startupInputEnabled = connection.IsInputEnabled;
            startupComplete = tab.Workbench.IsStarted;
            // Initial Ready is raised with the engine gate held; disabled input must not re-enter it.
            if (!startupInputEnabled.Value)
                tab.Terminal.WriteInput("$global:dtRejectedStartupInput = 999\r");
            startupText = NativeText(tab);
        }
        void OnSessionAdded(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action != NotifyCollectionChangedAction.Add || engine is not null) return;
            Assert.NotNull(args.NewItems);
            engine = Assert.Single(args.NewItems.OfType<SessionModel>()).Engine;
            engine.StateChanged += OnState;
        }
        tab.Workbench.Workbench.Sessions.CollectionChanged += OnSessionAdded;
        try
        {
            await host.InitializeAsync();
            Assert.Equal(false, startupComplete);
            Assert.Equal(false, startupInputEnabled);
            Assert.NotNull(startupText);
            Assert.DoesNotContain("dtRejectedStartupInput", startupText, StringComparison.Ordinal);
            Assert.NotNull(engine);
            Assert.NotNull(connection);
            Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
            Assert.Same(connection, NativeConnection(tab));
            Assert.True(tab.Workbench.IsStarted);
            Assert.Empty(Assert.Single(tab.Workbench.Workbench.Sessions).History);
            await ExecuteTokenAsync(tab, "'DT-STARTUP-REJECTED:' + ($null -eq $global:dtRejectedStartupInput)",
                "DT-STARTUP-REJECTED:True");
            await SubmitTokenAsync(tab, "$global:dtStartupReadyInput = 42; 'DT-STARTUP-READY:' + $global:dtStartupReadyInput",
                "DT-STARTUP-READY:42");
            Assert.Equal(SessionState.Ready, engine.State);
            Assert.Equal(TerminalConnectionState.Connected, connection.State);
            Assert.Empty(host.Errors);
        }
        finally
        {
            tab.Workbench.Workbench.Sessions.CollectionChanged -= OnSessionAdded;
            if (engine is not null) engine.StateChanged -= OnState;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task PendingCloseCoreAsync(bool forceDispose) => WithHostAsync(async (tab, host) =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        const string text = "Write-Output 'pending-input-unsaved-document'";
        var document = tab.Workbench.CreateDocument(text);
        Assert.True(document.File.IsDirty);
        var raised = new TaskCompletionSource<InputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInput(InputRequest request) => raised.TrySetResult(request);
        engine.InputRequested += OnInput;
        Task? execution = null;
        try
        {
            execution = tab.Workbench.ExecuteAsync(
                "$global:dtPendingCloseAnswer = Read-Host 'DT-pending-close'; 'DT-PENDING-CLOSE:' + $global:dtPendingCloseAnswer");
            var request = await raised.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await WaitForAsync(() => connection.HasPendingInput && connection.IsInputEnabled,
                () => "Native Read-Host was not accepting input before close.");
            Assert.False(execution.IsCompleted);
            Assert.False(request.Response.Task.IsCompleted);
            Assert.Equal(SessionState.Running, engine.State);
            Assert.Empty(host.Owner.OwnedWindows);
            if (forceDispose)
            {
                var disposal = tab.DisposeAsync();
                Assert.True(tab.IsDisposed);
                await disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(60));
                await execution.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(request.Response.Task.IsCanceled);
                Assert.Equal(SessionState.Disposed, engine.State);
                Assert.Equal(TerminalConnectionState.Disposed, connection.State);
                Assert.False(connection.HasPendingInput);
                Assert.False(connection.IsInputEnabled);
                Assert.False(tab.Terminal.IsRunning);
                Assert.False(tab.Workbench.IsEnabled);
                Assert.False(tab.Workbench.IsStarted);
                Assert.True(host.Owner.IsVisible);
                Assert.Empty(host.Owner.OwnedWindows);
                Assert.Equal(text, document.Document.Text);
                Assert.True(document.File.IsDirty);
                await tab.DisposeAsync();
            }
            else
            {
                var before = host.Owner.OwnedWindows.ToArray();
                var close = tab.RequestCloseAsync();
                var dialog = await NewDialogAsync(host.Owner, before, close);
                Assert.Equal("Stop execution", dialog.Title);
                Assert.False(connection.IsInputEnabled);
                Assert.True(connection.HasPendingInput);
                ClickCancel(dialog);
                Assert.False(await close.WaitAsync(TimeSpan.FromSeconds(60)));
                await WaitForAsync(() => connection.HasPendingInput && connection.IsInputEnabled,
                    () => "Canceled close did not re-enable the existing pending input.");
                Assert.False(request.Response.Task.IsCompleted);
                Assert.False(execution.IsCompleted);
                Assert.False(tab.IsDisposed);
                Assert.True(tab.Workbench.IsEnabled);
                Assert.True(tab.Workbench.IsStarted);
                Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
                Assert.Same(connection, NativeConnection(tab));
                Assert.Equal(runspace, engine.LocalRunspaceId);
                Assert.Equal(text, document.Document.Text);
                Assert.True(document.File.IsDirty);
                tab.Terminal.WriteInput("kept-request\r");
                Assert.Equal("kept-request", await request.Response.Task.WaitAsync(TimeSpan.FromSeconds(60)));
                await execution.WaitAsync(TimeSpan.FromSeconds(60));
                await WaitForNativeOutputAsync(tab, "DT-PENDING-CLOSE:kept-request");
                await SubmitTokenAsync(tab, "'DT-CLOSE-CANCEL-RETAINED:' + $global:dtPendingCloseAnswer",
                    "DT-CLOSE-CANCEL-RETAINED:kept-request");
                Assert.True(await tab.Workbench.SaveDocumentAsync(document,
                    Path.Combine(SettingsService.SettingsDirectory, "pending-close-cancel.ps1")));
            }
            Assert.Empty(host.Errors);
        }
        finally
        {
            engine.InputRequested -= OnInput;
            if (execution is { IsCompleted: false })
            {
                await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
                await execution.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task RunningStopCoreAsync() => WithHostAsync(async (tab, _) =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var connection = NativeConnection(tab);
        var execution = tab.Workbench.ExecuteAsync(
            "$global:dtAfterRunningStop = 0; 'DT-RUNNING-STOP-READY'; while ($true) {}; $global:dtAfterRunningStop = 999");
        try
        {
            await WaitForNativeOutputAsync(tab, "DT-RUNNING-STOP-READY");
            Assert.False(execution.IsCompleted);
            Assert.Equal(SessionState.Running, engine.State);
            Assert.False(connection.IsInputEnabled);
            tab.Terminal.WriteInput("\u0003");
            await execution.WaitAsync(TimeSpan.FromSeconds(60));
            await WaitForAsync(() => engine.State == SessionState.Ready && connection.IsInputEnabled,
                () => $"Stopped command did not restore input: state={engine.State}, input={connection.IsInputEnabled}.");
            Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
            Assert.Same(connection, NativeConnection(tab));
            Assert.Equal(TerminalConnectionState.Connected, connection.State);
            await ExecuteTokenAsync(tab, "'DT-RUNNING-STOP-AFTER:' + $global:dtAfterRunningStop", "DT-RUNNING-STOP-AFTER:0");
            await SubmitTokenAsync(tab, "'DT-RUNNING-STOP-STILL-USABLE'", "DT-RUNNING-STOP-STILL-USABLE");
        }
        finally
        {
            if (!execution.IsCompleted)
            {
                await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
                await execution.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task Utf8CoreAsync(string value) => WithHostAsync(async (tab, _) =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var connection = NativeConnection(tab);
        var bytes = Encoding.UTF8.GetBytes("$global:dtUtf8 = '" + value + "'; 'DT-UTF8:' + $global:dtUtf8\r");
        foreach (var valueByte in bytes) connection.Write(new[] { valueByte });
        await WaitForNativeOutputAsync(tab, "DT-UTF8:" + value);
        await WaitForAsync(() => session.Engine.State == SessionState.Ready && connection.IsInputEnabled,
            () => $"Fragmented UTF8 command did not finish: {session.Engine.State}.");
        await ExecuteTokenAsync(tab, "'DT-UTF8-PERSISTED:' + $global:dtUtf8", "DT-UTF8-PERSISTED:" + value);
        Assert.DoesNotContain("\ufffd", NativeText(tab), StringComparison.Ordinal);
        Assert.Equal(TerminalConnectionState.Connected, connection.State);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ResizeCoreAsync() => WithHostAsync(async (tab, host) =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        foreach (var (columns, rows) in new[] { (80, 24), (80, 24), (132, 40), (60, 18) })
        {
            connection.Resize(columns, rows);
            Assert.Equal(columns, connection.Columns);
            Assert.Equal(rows, connection.Rows);
            await ExecuteTokenAsync(tab,
                "'DT-RAWUI:' + $Host.UI.RawUI.WindowSize.Width + ':' + $Host.UI.RawUI.WindowSize.Height",
                $"DT-RAWUI:{columns}:{rows}");
            Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
        }
        var previousColumns = tab.Terminal.Engine.CreateSnapshot().Buffer.Columns;
        foreach (var (width, direction) in new[] { (1400, 1), (850, -1), (850, 0) })
        {
            host.Owner.Width = width;
            host.Owner.UpdateLayout();
            await WaitForAsync(() =>
            {
                var buffer = tab.Terminal.Engine.CreateSnapshot().Buffer;
                return Math.Sign(buffer.Columns - previousColumns) == direction &&
                       connection.Columns == buffer.Columns && connection.Rows == buffer.Rows;
            }, () => $"Window width {width} did not update native dimensions: previous columns={previousColumns}, connection={connection.Columns}x{connection.Rows}.");
            var expectedColumns = tab.Terminal.Engine.CreateSnapshot().Buffer.Columns;
            Assert.Equal(direction, Math.Sign(expectedColumns - previousColumns));
            Assert.Equal(expectedColumns, connection.Columns);
            await ExecuteTokenAsync(tab, "'DT-WINDOW-RAWUI:' + $Host.UI.RawUI.WindowSize.Width",
                $"DT-WINDOW-RAWUI:{expectedColumns}");
            Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            previousColumns = expectedColumns;
        }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task WithHostAsync(Func<PowerShellIseTab, IseTestHost, Task> test)
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        await test(tab, host);
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData(0, 0, "", "abcdef", 0)]
    [InlineData(0, 1, "a", "bcdef", 0)]
    [InlineData(2, 0, "", "abcdef", 2)]
    [InlineData(2, 3, "cde", "abf", 2)]
    [InlineData(0, 6, "abcdef", "", 0)]
    [InlineData(6, 0, "", "abcdef", 6)]
    public async Task PublicNativeSelectionAndDeletionUseExactEditableBounds(
        int start, int length, string selected, string remaining, int caret)
    {
        await InitializeRuntimeAsync();
        await SelectionContractCoreAsync(start, length, selected, remaining, caret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SelectionContractCoreAsync(int start, int length, string selected, string remaining, int caret) =>
        WithHostAsync(async (tab, _) =>
        {
            var connection = await EditableConnectionAsync(tab, "abcdef");
            SelectEditableRange(connection, start, length);
            Assert.Equal(start, connection.SelectionStart);
            Assert.Equal(length, connection.SelectionLength);
            Assert.Equal(start + length, connection.CaretOffset);
            Assert.Equal(selected, connection.SelectedInput);
            var changes = 0;
            connection.InputChanged += (_, _) => changes++;
            var revision = connection.InputRevision;
            connection.DeleteSelection();
            Assert.Equal(remaining, connection.InputText);
            Assert.Equal(caret, connection.CaretOffset);
            Assert.Equal(0, connection.SelectionLength);
            Assert.Equal("", connection.SelectedInput);
            Assert.Equal(length == 0 ? 0 : 1, changes);
            Assert.Equal(revision + (length == 0 ? 0 : 1), connection.InputRevision);
            Assert.Empty(Assert.Single(tab.Workbench.Workbench.Sessions).History);
        });

    [AvaloniaFact]
    public async Task PublicNativeSelectionExtendsLeftRightHomeAndEnd()
    {
        await InitializeRuntimeAsync();
        await SelectionExtensionContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SelectionExtensionContractCoreAsync() => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "abcdef");
        connection.MoveCaret(-1, extendSelection: true);
        AssertEditableSelection(connection, "abcdef", 5, 5, 1, "f");
        connection.MoveCaret(-1, extendSelection: true);
        AssertEditableSelection(connection, "abcdef", 4, 4, 2, "ef");
        connection.MoveCaret(1, extendSelection: true);
        AssertEditableSelection(connection, "abcdef", 5, 5, 1, "f");
        connection.MoveCaret(-1, extendSelection: true, toBoundary: true);
        AssertEditableSelection(connection, "abcdef", 0, 0, 6, "abcdef");
        connection.MoveCaret(1, extendSelection: true, toBoundary: true);
        AssertEditableSelection(connection, "abcdef", 6, 6, 0, "");
        connection.MoveCaret(-1);
        AssertEditableSelection(connection, "abcdef", 5, 5, 0, "");
        connection.MoveCaret(1, extendSelection: true, toBoundary: true);
        AssertEditableSelection(connection, "abcdef", 6, 5, 1, "f");
        connection.SelectInput();
        AssertEditableSelection(connection, "abcdef", 6, 0, 6, "abcdef");
        connection.Write("X");
        AssertEditableSelection(connection, "X", 1, 1, 0, "");
        Assert.Empty(Assert.Single(tab.Workbench.Workbench.Sessions).History);
    });

    [AvaloniaTheory]
    [InlineData("", 0, -1, false, 0)]
    [InlineData("", 0, 0, false, 0)]
    [InlineData("abcd", 0, -99, false, 0)]
    [InlineData("abcd", 1, -99, false, 0)]
    [InlineData("abcd", 1, 0, false, 2)]
    [InlineData("abcd", 3, 99, false, 4)]
    [InlineData("abcd", 4, 1, false, 4)]
    [InlineData("abcd", 2, -1, true, 0)]
    [InlineData("abcd", 2, 0, true, 4)]
    public async Task PublicNativeCaretDirectionAndBoundaryPolicy(
        string text, int start, int direction, bool boundary, int expected)
    {
        await InitializeRuntimeAsync();
        await CaretDirectionContractCoreAsync(text, start, direction, boundary, expected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CaretDirectionContractCoreAsync(string text, int start, int direction, bool boundary, int expected) =>
        WithHostAsync(async (tab, _) =>
        {
            var connection = await EditableConnectionAsync(tab, text);
            SelectEditableRange(connection, start, 0);
            var revision = connection.InputRevision;
            var changes = 0;
            connection.InputChanged += (_, _) => changes++;
            connection.MoveCaret(direction, toBoundary: boundary);
            AssertEditableSelection(connection, text, expected, expected, 0, "");
            Assert.Equal(revision + 1, connection.InputRevision);
            Assert.Equal(1, changes);
        });

    [AvaloniaTheory]
    [InlineData("A🍵B", 1, "left", "A🍵B", 0)]
    [InlineData("A🍵B", 1, "right", "A🍵B", 3)]
    [InlineData("A🍵B", 1, "delete", "AB", 1)]
    [InlineData("A🍵B", 3, "backspace", "AB", 1)]
    [InlineData("Ae\u0301B", 1, "right", "Ae\u0301B", 3)]
    [InlineData("Ae\u0301B", 3, "left", "Ae\u0301B", 1)]
    [InlineData("Ae\u0301B", 1, "delete", "AB", 1)]
    [InlineData("Ae\u0301B", 3, "backspace", "AB", 1)]
    [InlineData("", 0, "backspace", "", 0)]
    [InlineData("", 0, "delete", "", 0)]
    [InlineData("AB", 0, "backspace", "AB", 0)]
    [InlineData("AB", 2, "delete", "AB", 2)]
    public async Task NativeGraphemeMovementAndDeletionUseWholeTextElements(
        string text, int caret, string operation, string expectedText, int expectedCaret)
    {
        await InitializeRuntimeAsync();
        await GraphemeContractCoreAsync(text, caret, operation, expectedText, expectedCaret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task GraphemeContractCoreAsync(string text, int caret, string operation, string expectedText, int expectedCaret) =>
        WithHostAsync(async (tab, _) =>
        {
            var connection = await EditableConnectionAsync(tab, text);
            connection.MoveCaret(-1, toBoundary: true);
            // Test offsets are known grapheme boundaries, not arbitrary UTF-16 interiors.
            for (var step = 0; step < text.Length && connection.CaretOffset < caret; step++) connection.MoveCaret(1);
            Assert.Equal(caret, connection.CaretOffset);
            var changes = 0;
            connection.InputChanged += (_, _) => changes++;
            switch (operation)
            {
                case "left": connection.MoveCaret(-1); break;
                case "right": connection.MoveCaret(1); break;
                case "delete": connection.Write("\x1b[3~"); break;
                case "backspace": connection.Write("\x7f"); break;
                default: throw new ArgumentException("Unknown test operation.", nameof(operation));
            }
            AssertEditableSelection(connection, expectedText, expectedCaret, expectedCaret, 0, "");
            Assert.Equal(text == expectedText && operation is ("delete" or "backspace") ? 0 : 1, changes);
            Assert.DoesNotContain("\ufffd", connection.InputText, StringComparison.Ordinal);
            Assert.Empty(Assert.Single(tab.Workbench.Workbench.Sessions).History);
        });

    [AvaloniaFact]
    public async Task NativeUndoRedoBytesRestoreTextCaretAndCollapseSelection()
    {
        await InitializeRuntimeAsync();
        await UndoRedoContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task UndoRedoContractCoreAsync() => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "abcdef");
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var output = new List<OutputEntry>();
        session.Engine.Output += output.Add;
        SelectEditableRange(connection, 2, 3);
        connection.Write("X");
        AssertEditableSelection(connection, "abXf", 3, 3, 0, "");
        Assert.True(connection.CanUndo);
        Assert.False(connection.CanRedo);
        connection.Write("\x1a");
        AssertEditableSelection(connection, "abcdef", 5, 5, 0, "");
        Assert.True(connection.CanRedo);
        connection.Write("\x19");
        AssertEditableSelection(connection, "abXf", 3, 3, 0, "");
        Assert.False(connection.CanRedo);
        connection.Undo();
        AssertEditableSelection(connection, "abcdef", 5, 5, 0, "");
        connection.Write("!");
        AssertEditableSelection(connection, "abcde!f", 6, 6, 0, "");
        Assert.False(connection.CanRedo);
        var revision = connection.InputRevision;
        connection.Redo();
        AssertEditableSelection(connection, "abcde!f", 6, 6, 0, "");
        Assert.Equal(revision, connection.InputRevision);
        Assert.Empty(session.History);
        Assert.Empty(output);
    });

    [AvaloniaFact]
    public async Task NativeUndoEmptyAndSelectionDeletionTransitionsAreExact()
    {
        await InitializeRuntimeAsync();
        await UndoEmptyContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task UndoEmptyContractCoreAsync() => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "");
        var changes = 0;
        connection.InputChanged += (_, _) => changes++;
        var revision = connection.InputRevision;
        connection.Undo();
        connection.Redo();
        connection.DeleteSelection();
        Assert.Equal(0, changes);
        Assert.Equal(revision, connection.InputRevision);
        Assert.False(connection.CanUndo);
        Assert.False(connection.CanRedo);
        connection.Write("a");
        connection.Undo();
        AssertEditableSelection(connection, "", 0, 0, 0, "");
        Assert.False(connection.CanUndo);
        Assert.True(connection.CanRedo);
        connection.Redo();
        AssertEditableSelection(connection, "a", 1, 1, 0, "");
        connection.SelectInput();
        connection.DeleteSelection();
        AssertEditableSelection(connection, "", 0, 0, 0, "");
        connection.Undo();
        AssertEditableSelection(connection, "a", 1, 1, 0, "");
        connection.Redo();
        AssertEditableSelection(connection, "", 0, 0, 0, "");
        Assert.Empty(Assert.Single(tab.Workbench.Workbench.Sessions).History);
    });

    [AvaloniaTheory]
    [InlineData(0, false, "\nabcd", 1)]
    [InlineData(2, false, "ab\ncd", 3)]
    [InlineData(4, false, "abcd\n", 5)]
    [InlineData(2, true, "ab\ncd", 3)]
    public async Task NativeExplicitAndShiftEnterNewlineNeverSubmit(
        int caret, bool gesture, string text, int expectedCaret)
    {
        await InitializeRuntimeAsync();
        await NewLineContractCoreAsync(caret, gesture, text, expectedCaret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task NewLineContractCoreAsync(int caret, bool gesture, string text, int expectedCaret) =>
        WithHostAsync(async (tab, _) =>
        {
            var connection = await EditableConnectionAsync(tab, "abcd");
            var session = Assert.Single(tab.Workbench.Workbench.Sessions);
            var engine = session.Engine;
            var output = new List<OutputEntry>();
            engine.Output += output.Add;
            SelectEditableRange(connection, caret, 0);
            if (gesture) connection.Write("\x1b[13;2u");
            else connection.InsertNewLine();
            AssertEditableSelection(connection, text, expectedCaret, expectedCaret, 0, "");
            Assert.Equal(text, session.Input);
            Assert.Empty(session.History);
            Assert.Empty(output);
            Assert.Same(engine, session.Engine);
            Assert.Equal(SessionState.Ready, engine.State);
            connection.Undo();
            AssertEditableSelection(connection, "abcd", caret, caret, 0, "");
            connection.Redo();
            AssertEditableSelection(connection, text, expectedCaret, expectedCaret, 0, "");
        });

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task NativeMultilineHistoryClampsAndRestoresDraft(int entries)
    {
        await InitializeRuntimeAsync();
        await HistoryContractCoreAsync(entries);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task HistoryContractCoreAsync(int entries) => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "draft\nunfinished");
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        Assert.Empty(session.History);
        var history = entries switch
        {
            0 => Array.Empty<string>(),
            1 => new[] { "oldest\ncontinued" },
            _ => new[] { "oldest\ncontinued", "middle", "newest\nlast" }
        };
        session.History.AddRange(history);
        session.HistoryIndex = entries;
        connection.Write("\x1b[A");
        Assert.Equal(entries == 0 ? "draft\nunfinished" : entries == 1 ? "oldest\ncontinued" : "newest\nlast", connection.InputText);
        if (entries == 3)
        {
            connection.Write("\x1b[A");
            Assert.Equal("middle", connection.InputText);
            Assert.Equal(1, session.HistoryIndex);
            connection.Write("\x1b[A");
            Assert.Equal("oldest\ncontinued", connection.InputText);
        }
        connection.Write("\x1b[A");
        Assert.Equal(0, session.HistoryIndex);
        Assert.Equal(entries == 0 ? "draft\nunfinished" : "oldest\ncontinued", connection.InputText);
        if (entries == 3)
        {
            connection.Write("\x1b[B");
            Assert.Equal("middle", connection.InputText);
            connection.Write("\x1b[B");
            Assert.Equal("newest\nlast", connection.InputText);
        }
        connection.Write("\x1b[B");
        AssertEditableSelection(connection, "draft\nunfinished", 16, 16, 0, "");
        Assert.Equal(entries, session.HistoryIndex);
        connection.Write("\x1b[B");
        AssertEditableSelection(connection, "draft\nunfinished", 16, 16, 0, "");
        Assert.Equal("draft\nunfinished", session.DraftInput);
        Assert.Equal(history, session.History);
        Assert.Equal(SessionState.Ready, session.Engine.State);
    });

    [AvaloniaTheory]
    [InlineData("edit")]
    [InlineData("caret")]
    [InlineData("selection")]
    public async Task NativeCompletionRejectsStaleInputCaretAndSelectionRevision(string mutation)
    {
        await InitializeRuntimeAsync();
        await StaleCompletionContractCoreAsync(mutation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task StaleCompletionContractCoreAsync(string mutation) => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "Get-DT tail");
        var revision = connection.InputRevision;
        var completion = NativeContractCompletion(0, 6);
        switch (mutation)
        {
            case "edit": connection.Write("!"); break;
            case "caret": connection.MoveCaret(-1); break;
            case "selection": connection.SelectInput(); break;
            default: throw new ArgumentException("Unknown test mutation.", nameof(mutation));
        }
        var changes = 0;
        connection.InputChanged += (_, _) => changes++;
        Assert.False(connection.ApplyCompletion(completion, 0, revision));
        Assert.Equal(0, changes);
        Assert.Equal(revision + 1, connection.InputRevision);
        switch (mutation)
        {
            case "edit": AssertEditableSelection(connection, "Get-DT tail!", 12, 12, 0, ""); break;
            case "caret": AssertEditableSelection(connection, "Get-DT tail", 10, 10, 0, ""); break;
            case "selection": AssertEditableSelection(connection, "Get-DT tail", 11, 0, 11, "Get-DT tail"); break;
        }
    });

    [AvaloniaTheory]
    [InlineData("", 0, 0, "Get-DTCommand", 13, 0)]
    [InlineData("Get-DT tail", 0, 6, "Get-DTCommand tail", 13, 11)]
    [InlineData("prefix ", 7, 0, "prefix Get-DTCommand", 20, 7)]
    [InlineData("pre Get-DT post", 4, 6, "pre Get-DTCommand post", 17, 15)]
    public async Task NativeCompletionMatchingRevisionReplacesOnlyDeclaredSpanAndIsUndoable(
        string text, int start, int length, string expectedText, int expectedCaret, int originalCaret)
    {
        await InitializeRuntimeAsync();
        await MatchingCompletionContractCoreAsync(text, start, length, expectedText, expectedCaret, originalCaret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task MatchingCompletionContractCoreAsync(
        string text, int start, int length, string expectedText, int expectedCaret, int originalCaret) => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, text);
        var revision = connection.InputRevision;
        var changes = 0;
        connection.InputChanged += (_, _) => changes++;
        Assert.True(connection.ApplyCompletion(NativeContractCompletion(start, length), 0, revision));
        AssertEditableSelection(connection, expectedText, expectedCaret, expectedCaret, 0, "");
        Assert.Equal(revision + 1, connection.InputRevision);
        Assert.Equal(1, changes);
        Assert.Equal(expectedText, Assert.Single(tab.Workbench.Workbench.Sessions).Input);
        connection.Undo();
        AssertEditableSelection(connection, text, originalCaret, originalCaret, 0, "");
        connection.Redo();
        AssertEditableSelection(connection, expectedText, expectedCaret, expectedCaret, 0, "");
    });

    [AvaloniaTheory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(12, 0)]
    [InlineData(6, 6)]
    public async Task NativeCompletionRejectsInvalidSpanWithoutMutation(int start, int length)
    {
        await InitializeRuntimeAsync();
        await InvalidCompletionSpanContractCoreAsync(start, length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task InvalidCompletionSpanContractCoreAsync(int start, int length) => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "Get-DT tail");
        connection.Write("!");
        connection.Undo();
        Assert.True(connection.CanRedo);
        var revision = connection.InputRevision;
        var changes = 0;
        connection.InputChanged += (_, _) => changes++;
        var error = Assert.Throws<ArgumentException>(
            () => connection.ApplyCompletion(NativeContractCompletion(start, length), 0, revision));
        Assert.Equal("completion", error.ParamName);
        Assert.StartsWith("The completion span is outside the input.", error.Message, StringComparison.Ordinal);
        AssertEditableSelection(connection, "Get-DT tail", 11, 11, 0, "");
        Assert.Equal(revision, connection.InputRevision);
        Assert.Equal(0, changes);
        Assert.True(connection.CanRedo);
        connection.Redo();
        AssertEditableSelection(connection, "Get-DT tail!", 12, 12, 0, "");
    });

    [AvaloniaTheory]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task NativeCompletionRejectsInvalidIndexAndEmptySetWithoutMutation(int index, bool empty)
    {
        await InitializeRuntimeAsync();
        await InvalidCompletionIndexContractCoreAsync(index, empty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task InvalidCompletionIndexContractCoreAsync(int index, bool empty) => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "Get-DT tail");
        connection.Write("!");
        connection.Undo();
        Assert.True(connection.CanRedo);
        var revision = connection.InputRevision;
        var changes = 0;
        connection.InputChanged += (_, _) => changes++;
        var completion = empty ? new CompletionSet(0, 6, []) : NativeContractCompletion(0, 6);
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => connection.ApplyCompletion(completion, index, revision));
        Assert.Equal("index", error.ParamName);
        AssertEditableSelection(connection, "Get-DT tail", 11, 11, 0, "");
        Assert.Equal(revision, connection.InputRevision);
        Assert.Equal(0, changes);
        Assert.True(connection.CanRedo);
        connection.Redo();
        AssertEditableSelection(connection, "Get-DT tail!", 12, 12, 0, "");
    });

    [AvaloniaFact]
    public async Task NativeDisabledInputIgnoresEditsUndoSelectionAndCompletion()
    {
        await InitializeRuntimeAsync();
        await DisabledInputContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task DisabledInputContractCoreAsync() => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "kept");
        var revision = connection.InputRevision;
        var changes = 0;
        connection.InputChanged += (_, _) => changes++;
        connection.RefreshState(acceptsCommands: true, inputDisabled: true);
        connection.Write("discard");
        connection.MoveCaret(-1);
        connection.SelectInput();
        connection.DeleteSelection();
        connection.InsertNewLine();
        connection.Undo();
        connection.Redo();
        Assert.Null(await connection.RequestCompletionAsync());
        Assert.False(connection.ApplyCompletion(new CompletionSet(-1, -1, []), -1, revision));
        AssertEditableSelection(connection, "kept", 4, 4, 0, "");
        Assert.Equal(revision, connection.InputRevision);
        Assert.Equal(0, changes);
        Assert.False(connection.IsInputEnabled);
        Assert.False(connection.IsSecretInput);
        Assert.False(connection.CanUndo);
        Assert.False(connection.CanRedo);
        connection.RefreshState(acceptsCommands: true, inputDisabled: false);
        Assert.True(connection.IsInputEnabled);
        connection.Write("!");
        AssertEditableSelection(connection, "kept!", 5, 5, 0, "");
    });

    private static CompletionSet NativeContractCompletion(int start, int length) => new(start, length,
        [new System.Management.Automation.CompletionResult("Get-DTCommand", "Get-DTCommand",
            System.Management.Automation.CompletionResultType.Command, "Owned deterministic completion")]);

    private static async Task<IsebergTerminalConnection> EditableConnectionAsync(PowerShellIseTab tab, string text)
    {
        var connection = NativeConnection(tab);
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        await WaitForAsync(() => connection.IsInputEnabled && session.Engine.State == SessionState.Ready,
            () => $"Editable input not ready: input={connection.IsInputEnabled}, state={session.Engine.State}.");
        Assert.Equal("", connection.InputText);
        Assert.Equal(0, connection.CaretOffset);
        Assert.Equal(0, connection.SelectionLength);
        Assert.False(connection.IsSecretInput);
        if (text.Length > 0) connection.Write("\x1b[200~" + text + "\x1b[201~");
        Assert.Equal(text, connection.InputText);
        Assert.Equal(text.Length, connection.CaretOffset);
        return connection;
    }

    private static void SelectEditableRange(IsebergTerminalConnection connection, int start, int length)
    {
        connection.MoveCaret(-1, toBoundary: true);
        for (var index = 0; index < start; index++) connection.MoveCaret(1);
        for (var index = 0; index < length; index++) connection.MoveCaret(1, extendSelection: true);
    }

    private static void AssertEditableSelection(
        IsebergTerminalConnection connection, string text, int caret, int start, int length, string selected)
    {
        Assert.Equal(text, connection.InputText);
        Assert.Equal(caret, connection.CaretOffset);
        Assert.Equal(start, connection.SelectionStart);
        Assert.Equal(length, connection.SelectionLength);
        Assert.Equal(selected, connection.SelectedInput);
    }

    [AvaloniaTheory]
    [InlineData("A🍵B", "🍵")]
    [InlineData("Ae\u0301B", "e\u0301")]
    public async Task NativeKeyboardSelectionReplacesWholeGraphemeAndUndoRestoresCaret(string text, string selected)
    {
        await InitializeRuntimeAsync();
        await GraphemeSelectionContractCoreAsync(text, selected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task GraphemeSelectionContractCoreAsync(string text, string selected) => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, text);
        connection.Write("\x1b[H");
        connection.Write("\x1b[C");
        connection.Write("\x1b[1;2C");
        AssertEditableSelection(connection, text, 3, 1, 2, selected);
        connection.Write("\x1b[1;2D");
        AssertEditableSelection(connection, text, 1, 1, 0, "");
        connection.Write("\x1b[1;2C");
        connection.Write("X");
        AssertEditableSelection(connection, "AXB", 2, 2, 0, "");
        connection.Undo();
        AssertEditableSelection(connection, text, 3, 3, 0, "");
        connection.Redo();
        AssertEditableSelection(connection, "AXB", 2, 2, 0, "");
        Assert.Empty(Assert.Single(tab.Workbench.Workbench.Sessions).History);
    });

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", 0xFFF5F5F5u, 0xFF012456u, 0x00FF9494u)]
    [InlineData("Light Console, Dark Editor", 0xFF626262u, 0xFFFFFFFFu, 0x00E50000u)]
    [InlineData("Dark Console, Dark Editor", 0xFFF5F5F5u, 0xFF012456u, 0x00FF9494u)]
    [InlineData("Light Console, Light Editor", 0xFF626262u, 0xFFFFFFFFu, 0x00E50000u)]
    [InlineData("Monochrome Green", 0xFF00FF00u, 0xFF000000u, 0x00FF0000u)]
    [InlineData("Presentation", 0xFFF5F5F5u, 0xFF000000u, 0x00FF0000u)]
    public async Task OriginalBuiltInThemesRenderNativeTerminalColorsAndRemainFixedAcrossHostChanges(
        string name, uint foreground, uint background, uint error)
    {
        await InitializeRuntimeAsync();
        await ThemePaletteCoreAsync(name, foreground, background, error, foreground, background, error);
    }

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", true, 0xFFF7F7F7u, 0xFF202020u)]
    [InlineData("Light Console, Dark Editor", false, 0xFF252526u, 0xFFF0F0F0u)]
    [InlineData("Dark Console, Dark Editor", false, 0xFF252526u, 0xFFF0F0F0u)]
    [InlineData("Light Console, Light Editor", true, 0xFFF7F7F7u, 0xFF202020u)]
    [InlineData("Monochrome Green", false, 0xFF252526u, 0xFFF0F0F0u)]
    [InlineData("Presentation", true, 0xFFF7F7F7u, 0xFF202020u)]
    public async Task OriginalBuiltInThemesRenderCompletionAndDescriptionWithinScriptScope(
        string name, bool oppositeHostDark, uint background, uint foreground)
    {
        await InitializeRuntimeAsync();
        await CompletionThemeCoreAsync(name, oppositeHostDark, background, foreground);
    }

    [AvaloniaFact]
    public async Task NativeCompletionUsesSelectedSecondItemAndUndoRedoRestoreExactRevisionAndCaret()
    {
        await InitializeRuntimeAsync();
        await SecondCompletionContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SecondCompletionContractCoreAsync() => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "ab");
        var revision = connection.InputRevision;
        var changes = 0;
        connection.InputChanged += (_, _) => changes++;
        var completion = new CompletionSet(0, 2,
        [
            new System.Management.Automation.CompletionResult("alpha", "Alpha",
                System.Management.Automation.CompletionResultType.Command, "first"),
            new System.Management.Automation.CompletionResult("beta", "Beta",
                System.Management.Automation.CompletionResultType.Command, "second")
        ]);

        Assert.True(connection.ApplyCompletion(completion, 1, revision));
        AssertEditableSelection(connection, "beta", 4, 4, 0, "");
        Assert.Equal("beta", Assert.Single(tab.Workbench.Workbench.Sessions).Input);
        Assert.Equal(revision + 1, connection.InputRevision);
        Assert.Equal(1, changes);
        Assert.True(connection.CanUndo);
        Assert.False(connection.CanRedo);
        connection.Undo();
        AssertEditableSelection(connection, "ab", 2, 2, 0, "");
        Assert.Equal(revision + 2, connection.InputRevision);
        Assert.Equal(2, changes);
        Assert.True(connection.CanRedo);
        connection.Redo();
        AssertEditableSelection(connection, "beta", 4, 4, 0, "");
        Assert.Equal("beta", Assert.Single(tab.Workbench.Workbench.Sessions).Input);
        Assert.Equal(revision + 3, connection.InputRevision);
        Assert.Equal(3, changes);
        Assert.False(connection.CanRedo);
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionEnterPreferenceControlsActualPopupAcceptKeysAndDescription(bool acceptOnEnter)
    {
        await InitializeRuntimeAsync();
        await CompletionAcceptKeysCoreAsync(acceptOnEnter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CompletionAcceptKeysCoreAsync(bool acceptOnEnter) => WithHostAsync(async (tab, _) =>
    {
        tab.Workbench.CreateDocument("Get-DT");
        var editor = tab.Workbench.ScriptEditorView;
        editor.CaretOffset = 6;
        editor.CompletionProvider = new PopupThemeCompletionProvider();
        editor.CompletionAcceptsEnter = acceptOnEnter;
        Assert.True(editor.FocusEditor());
        try
        {
            await editor.ShowCompletionAsync();
            var popup = editor.CompletionPopup;
            Assert.NotNull(popup);
            var descriptionPopup = Assert.Single(popup.GetLogicalChildren().OfType<Popup>());
            await WaitForAsync(() => popup.IsOpen && descriptionPopup.IsOpen,
                () => "Controlled completion policy popup or description did not open.");
            Assert.Equal((acceptOnEnter ? new[] { Key.Enter, Key.Tab } : new[] { Key.Tab }).OrderBy(key => (int)key).ToArray(),
                popup.CompletionList.CompletionAcceptKeys.OrderBy(key => (int)key).ToArray());
            Assert.Equal("Get-DTCompletion", popup.CompletionList.SelectedItem!.Text);
            Assert.Equal("DT completion description", Assert.IsAssignableFrom<ContentControl>(descriptionPopup.Child).Content);
            Assert.Equal("Get-DT", editor.Document.Text);
            Assert.Equal(6, editor.CaretOffset);
        }
        finally { editor.CloseCompletion(); }
    });

    [AvaloniaTheory]
    [InlineData("origin")]
    [InlineData("interior")]
    [InlineData("last-cell")]
    [InlineData("offscreen-x")]
    [InlineData("offscreen-y")]
    public async Task NativeCompletionPopupAnchorsClampedCursorCellAndShowsMonochromeDescription(string position)
    {
        await InitializeRuntimeAsync();
        await NativeCompletionAnchorCoreAsync(position);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task NativeCompletionAnchorCoreAsync(string position)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = "Monochrome Green";
        profile.IseConsoleIntelliSense = false;
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        host.Owner.RequestedThemeVariant = ThemeVariant.Light;
        await host.InitializeAsync();
        await ExecuteTokenAsync(tab, "$global:dtAnchorOwnedValue = 42; 'DT-ANCHOR-READY'", "DT-ANCHOR-READY");
        var connection = await EditableConnectionAsync(tab, "$dtAnchorOwnedVal");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var matches = await connection.RequestCompletionAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(matches);
        var match = Assert.Single(matches.Matches);
        Assert.Equal("dtAnchorOwnedValue", match.ListItemText);
        Assert.Equal("$dtAnchorOwnedValue", match.CompletionText);
        var revision = connection.InputRevision;
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var runspace = session.Engine.LocalRunspaceId;
        var terminal = tab.Terminal;
        var popup = Assert.Single(tab.GetLogicalDescendants().OfType<Popup>(),
            candidate => ReferenceEquals(candidate.PlacementTarget, terminal));
        Assert.False(popup.IsOpen);
        Assert.True(terminal.Focus());
        Assert.True(terminal.IsKeyboardFocusWithin);
        var cell = terminal.CellSize;
        Assert.True(cell.Width > 0 && cell.Height > 0);
        Assert.True(terminal.Bounds.Width > 4 * cell.Width && terminal.Bounds.Height > 3 * cell.Height);
        var original = terminal.Engine.CreateSnapshot().Buffer;
        var column = position switch
        {
            "origin" => 0,
            "last-cell" => original.Columns - 1,
            "offscreen-x" => original.Columns + 3,
            _ => 3
        };
        var row = position switch
        {
            "origin" => 0,
            "last-cell" => original.Rows - 1,
            "offscreen-y" => original.Rows + 2,
            _ => 2
        };
        if (position is "offscreen-x" or "offscreen-y")
            terminal.Engine.Resize(original.Columns + 4, original.Rows + 3);
        terminal.Engine.Feed($"\x1b[{row + 1};{column + 1}H");
        var cursor = terminal.Engine.CreateSnapshot().Buffer;
        Assert.Equal(column, cursor.CursorX);
        Assert.Equal(row, cursor.CursorY);
        var expectedX = position == "offscreen-x" ? terminal.Bounds.Width - cell.Width : column * cell.Width;
        var expectedY = position == "offscreen-y" ? terminal.Bounds.Height - cell.Height : row * cell.Height;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPopupChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Popup.IsOpenProperty && popup.IsOpen) opened.TrySetResult();
        }
        popup.PropertyChanged += OnPopupChanged;
        try
        {
            host.Owner.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
            host.Owner.KeyRelease(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var anchor = Assert.IsType<Rect>(popup.PlacementRect);
            Assert.Same(terminal, popup.PlacementTarget);
            Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, popup.Placement);
            Assert.Equal(expectedX, anchor.X, precision: 8);
            Assert.Equal(expectedY, anchor.Y, precision: 8);
            Assert.Equal(cell.Width, anchor.Width, precision: 8);
            Assert.Equal(cell.Height, anchor.Height, precision: 8);
            Assert.Equal(0d, popup.HorizontalOffset);
            Assert.Equal(0d, popup.VerticalOffset);
            Assert.True(anchor.Right <= terminal.Bounds.Width && anchor.Bottom <= terminal.Bounds.Height);
            var border = Assert.IsType<Border>(popup.Child);
            var list = Assert.Single(border.GetVisualDescendants().OfType<ListBox>());
            var description = Assert.Single(border.GetVisualDescendants().OfType<TextBlock>(),
                candidate => AutomationProperties.GetName(candidate) == "PowerShell completion description");
            Assert.Equal("dtAnchorOwnedValue", list.SelectedItem);
            Assert.False(string.IsNullOrWhiteSpace(description.Text));
            Assert.Equal(match.ToolTip, description.Text);
            Assert.Equal(Color.Parse("#00FF00"), Assert.IsAssignableFrom<ISolidColorBrush>(description.Foreground).Color);
            Assert.Equal(Color.Parse("#000000"), Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
            Assert.Equal("$dtAnchorOwnedVal", connection.InputText);
            Assert.Equal(revision, connection.InputRevision);
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);
            Assert.Same(session, Assert.Single(tab.Workbench.Workbench.Sessions));
        }
        finally
        {
            popup.PropertyChanged -= OnPopupChanged;
            popup.IsOpen = false;
            terminal.Engine.Resize(original.Columns, original.Rows);
        }
        Assert.Empty(host.Errors);
    }

    [AvaloniaFact]
    public async Task RenderedProgressHierarchyKeepsChildIndentationUntilOwnedInputBarrierIsReleased()
    {
        await InitializeRuntimeAsync();
        await ProgressHierarchyBarrierCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ProgressHierarchyBarrierCoreAsync() => WithHostAsync(async (tab, _) =>
    {
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var entered = new TaskCompletionSource<InputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInput(InputRequest request) => entered.TrySetResult(request);
        engine.InputRequested += OnInput;
        Task? execution = null;
        InputRequest? barrier = null;
        try
        {
            execution = tab.Workbench.ExecuteAsync(
                "Write-Progress -Id 1 -Activity 'P' -Status 'running' -PercentComplete 20; " +
                "Write-Progress -Id 2 -ParentId 1 -Activity 'C' -Status 'queued' -PercentComplete 55; " +
                "$null = Read-Host 'Owned progress barrier'; " +
                "Write-Progress -Id 2 -Activity 'C' -Completed; Write-Progress -Id 1 -Activity 'P' -Completed");
            barrier = await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var panel = tab.Workbench.FindControl<Control>("ProgressPanel")!;
            var text = tab.Workbench.FindControl<TextBlock>("ProgressText")!;
            var progress = tab.Workbench.FindControl<ProgressBar>("ScriptProgress")!;
            await WaitForAsync(() => panel.IsVisible && text.Text == "P - running" + Environment.NewLine + "  C - queued",
                () => "Owned progress hierarchy was not rendered: " + text.Text);
            Assert.Equal("P - running" + Environment.NewLine + "  C - queued", text.Text);
            Assert.Equal(55d, progress.Value);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(SessionState.Running, engine.State);
            Assert.False(execution.IsCompleted);
            Assert.False(barrier.Response.Task.IsCompleted);
            Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);

            barrier.Response.TrySetResult("release");
            await execution.WaitAsync(TimeSpan.FromSeconds(60));
            await WaitForAsync(() => engine.State == SessionState.Ready && !panel.IsVisible,
                () => "Completed owned progress hierarchy did not clear.");
            Assert.False(panel.IsVisible);
        }
        finally
        {
            engine.InputRequested -= OnInput;
            barrier?.Response.TrySetResult("release");
            if (execution is { IsCompleted: false })
            {
                await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
                await execution.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    });

    [AvaloniaTheory]
    [InlineData(99, 0, 99)]
    [InlineData(100, 0, 100)]
    [InlineData(101, 1, 100)]
    public async Task NativeUndoCapacityRetainsExactBoundaryAndRedoOrderAndInvalidatesFreshBranch(
        int edits, int earliestRetainedLength, int retainedEdits)
    {
        await InitializeRuntimeAsync();
        await UndoCapacityResidualCoreAsync(edits, earliestRetainedLength, retainedEdits);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task UndoCapacityResidualCoreAsync(int edits, int earliestRetainedLength, int retainedEdits) =>
        WithHostAsync(async (tab, _) =>
        {
            var connection = await EditableConnectionAsync(tab, "");
            var session = Assert.Single(tab.Workbench.Workbench.Sessions);
            var runspace = session.Engine.LocalRunspaceId;
            const string alphabet = "abcXYZ019_";
            var expected = string.Concat(Enumerable.Range(0, edits).Select(index => alphabet[index % alphabet.Length]));
            var output = new List<OutputEntry>();
            session.Engine.Output += output.Add;
            var changes = 0;
            var redraws = 0;
            void OnChanged(object? sender, EventArgs args) => changes++;
            void OnOutput(object? sender, ReadOnlyMemory<byte> bytes) => redraws++;
            connection.InputChanged += OnChanged;
            connection.OutputReceived += OnOutput;
            try
            {
                Assert.False(connection.CanUndo);
                Assert.False(connection.CanRedo);
                var revision = connection.InputRevision;
                foreach (var character in expected) connection.Write(character.ToString());
                AssertEditableSelection(connection, expected, edits, edits, 0, "");
                Assert.Equal(revision + edits, connection.InputRevision);
                Assert.Equal(edits, changes);
                Assert.True(connection.CanUndo);
                Assert.False(connection.CanRedo);

                // Check every retained edit, including the newest and the exact oldest snapshot.
                for (var length = edits - 1; length >= earliestRetainedLength; length--)
                {
                    connection.Undo();
                    AssertEditableSelection(connection, expected[..length], length, length, 0, "");
                    Assert.Equal(expected[..length], session.Input);
                    Assert.Equal(length > earliestRetainedLength, connection.CanUndo);
                    Assert.True(connection.CanRedo);
                }
                Assert.Equal(revision + edits + retainedEdits, connection.InputRevision);
                Assert.Equal(edits + retainedEdits, changes);
                var floorRevision = connection.InputRevision;
                var floorChanges = changes;
                var floorRedraws = redraws;
                connection.Undo();
                AssertEditableSelection(connection, expected[..earliestRetainedLength],
                    earliestRetainedLength, earliestRetainedLength, 0, "");
                Assert.Equal(floorRevision, connection.InputRevision);
                Assert.Equal(floorChanges, changes);
                Assert.Equal(floorRedraws, redraws);
                Assert.False(connection.CanUndo);
                Assert.True(connection.CanRedo);

                for (var length = earliestRetainedLength + 1; length <= edits; length++)
                {
                    connection.Redo();
                    AssertEditableSelection(connection, expected[..length], length, length, 0, "");
                    Assert.Equal(expected[..length], session.Input);
                    Assert.True(connection.CanUndo);
                    Assert.Equal(length < edits, connection.CanRedo);
                }
                Assert.Equal(revision + edits + 2 * retainedEdits, connection.InputRevision);
                Assert.Equal(edits + 2 * retainedEdits, changes);

                connection.Undo();
                AssertEditableSelection(connection, expected[..^1], edits - 1, edits - 1, 0, "");
                Assert.True(connection.CanRedo);
                connection.Write("!");
                AssertEditableSelection(connection, expected[..^1] + "!", edits, edits, 0, "");
                Assert.False(connection.CanRedo);
                var branchRevision = connection.InputRevision;
                var branchChanges = changes;
                var branchRedraws = redraws;
                connection.Redo();
                AssertEditableSelection(connection, expected[..^1] + "!", edits, edits, 0, "");
                Assert.Equal(branchRevision, connection.InputRevision);
                Assert.Equal(branchChanges, changes);
                Assert.Equal(branchRedraws, redraws);

                // Replaying and branching must not revive an evicted snapshot or lose a retained one.
                for (var length = edits - 1; length >= earliestRetainedLength; length--)
                {
                    connection.Undo();
                    AssertEditableSelection(connection, expected[..length], length, length, 0, "");
                    Assert.Equal(length > earliestRetainedLength, connection.CanUndo);
                }
                Assert.Equal(branchRevision + retainedEdits, connection.InputRevision);
                Assert.Equal(branchChanges + retainedEdits, changes);
                Assert.Empty(session.History);
                Assert.Equal(0, session.HistoryIndex);
                Assert.Empty(output);
                Assert.Equal(SessionState.Ready, session.Engine.State);
                Assert.Equal(runspace, session.Engine.LocalRunspaceId);
                Assert.Same(session, Assert.Single(tab.Workbench.Workbench.Sessions));
            }
            finally
            {
                connection.InputChanged -= OnChanged;
                connection.OutputReceived -= OnOutput;
                session.Engine.Output -= output.Add;
            }
        });

    [AvaloniaTheory]
    [InlineData(0, int.MaxValue)]
    [InlineData(11, 1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(11, int.MaxValue)]
    [InlineData(int.MaxValue, 1)]
    public async Task NativeCompletionRejectsOverflowAndEndSpanAtomicallyPreservingBothEditStacks(
        int start, int length)
    {
        await InitializeRuntimeAsync();
        await CompletionOverflowResidualCoreAsync(start, length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CompletionOverflowResidualCoreAsync(int start, int length) => WithHostAsync(async (tab, _) =>
    {
        var connection = await EditableConnectionAsync(tab, "Get-DT tail");
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var runspace = session.Engine.LocalRunspaceId;
        session.History.Add("'owned prior command'");
        session.HistoryIndex = 1;
        session.DraftInput = "owned unsubmitted history draft";
        connection.Write("!");
        connection.Undo();
        SelectEditableRange(connection, 2, 3);
        AssertEditableSelection(connection, "Get-DT tail", 5, 2, 3, "t-D");
        Assert.True(connection.CanUndo);
        Assert.True(connection.CanRedo);
        var revision = connection.InputRevision;
        var transcript = session.ConsoleDocument.Text;
        var nativeText = NativeText(tab);
        var changes = 0;
        var redraws = 0;
        var faults = 0;
        var exits = 0;
        var sessionExits = 0;
        var output = new List<OutputEntry>();
        void OnChanged(object? sender, EventArgs args) => changes++;
        void OnOutput(object? sender, ReadOnlyMemory<byte> bytes) => redraws++;
        void OnFault(object? sender, Exception error) => faults++;
        void OnExit(object? sender, int code) => exits++;
        void OnSessionExit(object? sender, TerminalExitInfo info) => sessionExits++;
        connection.InputChanged += OnChanged;
        connection.OutputReceived += OnOutput;
        connection.Faulted += OnFault;
        connection.Exited += OnExit;
        connection.SessionExited += OnSessionExit;
        session.Engine.Output += output.Add;
        try
        {
            var error = Record.Exception(
                () => connection.ApplyCompletion(NativeContractCompletion(start, length), 0, revision));
            var rejected = CaptureNativeResidualEdit(connection, session);
            var rejectedChanges = changes;
            var rejectedRedraws = redraws;
            var rejectedNativeText = NativeText(tab);
            var rejectedTranscript = session.ConsoleDocument.Text;

            // Capture all witnesses before asserting, so a wrong exception cannot hide stack corruption.
            connection.Undo();
            var undone = CaptureNativeResidualEdit(connection, session);
            connection.Redo();
            var firstRedo = CaptureNativeResidualEdit(connection, session);
            connection.Redo();
            var finalRedo = CaptureNativeResidualEdit(connection, session);

            Assert.Multiple(
                () =>
                {
                    var argument = Assert.IsType<ArgumentException>(error);
                    Assert.Equal("completion", argument.ParamName);
                    Assert.StartsWith("The completion span is outside the input.", argument.Message, StringComparison.Ordinal);
                },
                () => Assert.Equal(("Get-DT tail", 5, 2, 3, "t-D", revision, true, true, "Get-DT tail"), rejected),
                () => Assert.Equal(("", 0, 0, 0, "", revision + 1, false, true, ""), undone),
                () => Assert.Equal(("Get-DT tail", 5, 5, 0, "", revision + 2, true, true, "Get-DT tail"), firstRedo),
                () => Assert.Equal(("Get-DT tail!", 12, 12, 0, "", revision + 3, true, false, "Get-DT tail!"), finalRedo),
                () => Assert.Equal(0, rejectedChanges),
                () => Assert.Equal(0, rejectedRedraws),
                () => Assert.Equal(nativeText, rejectedNativeText),
                () => Assert.Equal(transcript, rejectedTranscript),
                () => Assert.Equal(new[] { "'owned prior command'" }, session.History),
                () => Assert.Equal(1, session.HistoryIndex),
                () => Assert.Equal("owned unsubmitted history draft", session.DraftInput),
                () => Assert.Empty(output),
                () => Assert.Equal((0, 0, 0), (faults, exits, sessionExits)),
                () => Assert.Equal(TerminalConnectionState.Connected, connection.State),
                () => Assert.True(connection.IsRunning),
                () => Assert.True(connection.IsInputEnabled),
                () => Assert.Equal(SessionState.Ready, session.Engine.State),
                () => Assert.Equal(runspace, session.Engine.LocalRunspaceId),
                () => Assert.Same(session, Assert.Single(tab.Workbench.Workbench.Sessions)));
        }
        finally
        {
            connection.InputChanged -= OnChanged;
            connection.OutputReceived -= OnOutput;
            connection.Faulted -= OnFault;
            connection.Exited -= OnExit;
            connection.SessionExited -= OnSessionExit;
            session.Engine.Output -= output.Add;
        }
    });

    private static (string Text, int Caret, int SelectionStart, int SelectionLength, string Selected,
        int Revision, bool CanUndo, bool CanRedo, string ModelInput) CaptureNativeResidualEdit(
        IsebergTerminalConnection connection, SessionModel session) =>
        (connection.InputText, connection.CaretOffset, connection.SelectionStart, connection.SelectionLength,
            connection.SelectedInput, connection.InputRevision, connection.CanUndo, connection.CanRedo, session.Input);
}
