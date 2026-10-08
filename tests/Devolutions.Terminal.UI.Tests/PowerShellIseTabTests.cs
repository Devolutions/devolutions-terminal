using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Devolutions.Terminal.App.Connections;
using Devolutions.Terminal.App.Views;
using Xunit;
using static Devolutions.Terminal.UI.Tests.PowerShellIseTestHarness;

namespace Devolutions.Terminal.UI.Tests;

public sealed class PowerShellIseTabTests
{
    [AvaloniaFact]
    public async Task RuntimeInitializationReturnsCachedSuccessfulTask()
    {
        await InitializeRuntimeAsync();
        var first = PowerShellIseRuntime.InitializeAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Same(first, PowerShellIseRuntime.InitializeAsync());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructionDoesNotStartSession(bool loadProfiles)
    {
        await InitializeRuntimeAsync();
        await ConstructionCoreAsync(loadProfiles);
    }

    [AvaloniaFact]
    public async Task InitializationRequiresShownHostAndReusesTask()
    {
        await InitializeRuntimeAsync();
        await InitializationCoreAsync();
    }

    [AvaloniaFact]
    public async Task InsertionIntoShownHostAttachesWorkbenchBeforeLayout()
    {
        await InitializeRuntimeAsync();
        await DynamicAttachmentCoreAsync();
    }

    [AvaloniaTheory]
    [InlineData("NewSession", Key.T, RawInputModifiers.Control)]
    [InlineData("NewRemoteSession", Key.None, RawInputModifiers.None)]
    [InlineData("CloseSession", Key.W, RawInputModifiers.Control | RawInputModifiers.Shift)]
    public async Task HostedSessionActionsCannotCreateOrCloseHiddenSessions(
        string action, Key shortcut, RawInputModifiers modifiers)
    {
        await InitializeRuntimeAsync();
        await HostedActionsCoreAsync(action, shortcut, modifiers);
    }

    [AvaloniaFact]
    public async Task ExecuteWritesActualConsoleOutput()
    {
        await InitializeRuntimeAsync();
        await OutputCoreAsync();
    }

    [AvaloniaFact]
    public async Task TwoWorkbenchesHaveIsolatedPersistentVariables()
    {
        await InitializeRuntimeAsync();
        await IsolationCoreAsync();
    }

    [AvaloniaFact]
    public async Task DisposeIsIdempotentAndDisposesOwnedEngines()
    {
        await InitializeRuntimeAsync();
        await DisposalCoreAsync();
    }

    [AvaloniaFact]
    public async Task RequestCloseDisposesSessionsButDoesNotCloseOwner()
    {
        await InitializeRuntimeAsync();
        await CloseCoreAsync();
    }

    [AvaloniaFact]
    public async Task DirtyCloseCancellationPreservesDocumentAndEngine()
    {
        await InitializeRuntimeAsync();
        await DirtyCancellationCoreAsync();
    }

    [AvaloniaTheory]
    [InlineData("Monochrome Green")]
    [InlineData(Devolutions.Terminal.Settings.IsebergThemes.FollowDt)]
    public async Task ThemeRefreshDuringPendingAndCanceledDirtyCloseKeepsHostReadableAndDisposedWrappersInvalid(
        string preset)
    {
        await InitializeRuntimeAsync();
        await ClosingThemeRefreshCoreAsync(preset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ClosingThemeRefreshCoreAsync(string preset)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = preset;
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        host.Owner.RequestedThemeVariant = ThemeVariant.Light;
        await host.InitializeAsync();
        const string text = "Write-Output 'theme-close-dirty'";
        var document = tab.Workbench.CreateDocument(text);
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var runspace = session.Engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        var root = tab.Workbench.Scripting;
        var file = root.CurrentFile!;
        var editor = file.Editor;
        var options = root.Options;
        var scope = Assert.IsType<ThemeVariantScope>(tab.Child);
        var refreshes = 0;
        tab.Workbench.ThemeSelectionChanged += (_, _) => refreshes++;
        var contrast = Iseberg.DesktopTheme.HighContrast;
        Task<bool>? pending = null;
        try
        {
            Assert.Equal(text, editor.Text);
            Assert.True(document.File.IsDirty);
            var before = host.Owner.OwnedWindows.ToArray();
            pending = tab.PrepareCloseAsync();
            var dialog = await NewDialogAsync(host.Owner, before, pending);
            Assert.False(pending.IsCompleted);
            Assert.False(tab.IsDisposed);
            Assert.Equal("The workbench is closing.",
                Assert.Throws<InvalidOperationException>(() => tab.Workbench.GetScriptingSettings()).Message);
            Assert.Throws<InvalidOperationException>(() => options.FontSize);
            Assert.Equal(preset, tab.Workbench.SelectedThemePreset);

            Refresh(false);
            Assert.Null(Record.Exception(() => host.Owner.RequestedThemeVariant = ThemeVariant.Dark));
            Refresh(true);
            Assert.False(pending.IsCompleted);
            Assert.Same(dialog, Assert.Single(host.Owner.OwnedWindows));
            Assert.Equal(text, document.Document.Text);
            Assert.True(document.File.IsDirty);
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);

            ClickCancel(dialog);
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.True(tab.Workbench.IsEnabled);
            Assert.True(tab.Workbench.IsStarted);
            Assert.False(tab.IsDisposed);
            Assert.Equal(preset, tab.Workbench.GetScriptingSettings().ThemePreset);
            Assert.Equal(text, editor.Text);
            Assert.Same(document, session.SelectedFile);
            Assert.Contains(document, session.Files);
            Assert.True(document.File.IsDirty);
            Assert.False(tab.Workbench.ScriptEditorView.TextEditor.IsReadOnly);
            Assert.Same(connection, NativeConnection(tab));
            Refresh(true);
            const string edited = text + "\n# still editable";
            editor.Text = edited;
            Assert.Equal(edited, document.Document.Text);
            await SubmitTokenAsync(tab, "'DT-THEME-CLOSE-CANCELED'", "DT-THEME-CLOSE-CANCELED");
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);

            Assert.True(await tab.Workbench.SaveDocumentAsync(document,
                Path.Combine(environment.DirectoryPath, "theme-close.ps1")));
            Assert.False(document.File.IsDirty);
            Assert.True(await tab.PrepareCloseAsync().WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.False(tab.Workbench.IsEnabled);
            Assert.Equal("The workbench is closing.",
                Assert.Throws<InvalidOperationException>(() => tab.Workbench.GetScriptingSettings()).Message);
            Assert.Equal(preset, tab.Workbench.SelectedThemePreset);
            Refresh(true);
            tab.CancelClosePreparation();
            Assert.True(tab.Workbench.IsEnabled);
            Assert.Equal(preset, tab.Workbench.GetScriptingSettings().ThemePreset);
            Refresh(true);
            Assert.Empty(host.Errors);

            await tab.DisposeAsync();
            Assert.True(tab.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => root.CurrentFile);
            Assert.Throws<ObjectDisposedException>(() => options.FontSize);
            Assert.Throws<ObjectDisposedException>(() => options.FontSize = 18);
            Assert.Throws<ObjectDisposedException>(() => file.DisplayName);
            Assert.Throws<ObjectDisposedException>(() => editor.Text);
            Assert.Throws<ObjectDisposedException>(() => editor.Text = "must not revive");
            var closedRefreshes = refreshes;
            Assert.Null(Record.Exception(() => Iseberg.DesktopTheme.Refresh(false)));
            Assert.Equal(closedRefreshes, refreshes);
            Assert.Equal(edited, document.Document.Text);
            Assert.Equal(Iseberg.Core.SessionState.Disposed, session.Engine.State);
        }
        finally
        {
            if (pending is { IsCompleted: false })
            {
                foreach (var owned in host.Owner.OwnedWindows.ToArray()) owned.Close();
                await pending.WaitAsync(TimeSpan.FromSeconds(60));
            }
            await tab.DisposeAsync();
            Iseberg.DesktopTheme.Refresh(contrast);
        }

        void Refresh(bool dark)
        {
            var previous = refreshes;
            Assert.Null(Record.Exception(() => Iseberg.DesktopTheme.Refresh(false)));
            Assert.True(refreshes > previous);
            Assert.Equal(preset, tab.Workbench.SelectedThemePreset);
            var fixedGreen = preset == "Monochrome Green";
            Assert.Equal(fixedGreen ? ThemeVariant.Dark : ThemeVariant.Default, scope.RequestedThemeVariant);
            Assert.Equal(Avalonia.Media.Color.Parse(fixedGreen ? "#000000" : dark ? "#1E1E1E" : "#FFFFFF"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(tab.Workbench.ScriptEditorView.TextEditor.Background).Color);
            Assert.Equal(Avalonia.Media.Color.Parse(fixedGreen ? "#00FF00" : dark ? "#D4D4D4" : "#000000"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(tab.Workbench.ScriptEditorView.TextEditor.Foreground).Color);
        }
    }

    [AvaloniaFact]
    public async Task CancelClosePreparationReenablesWithoutDisposal()
    {
        await InitializeRuntimeAsync();
        await PreparationCoreAsync();
    }

    [AvaloniaFact]
    public async Task GroupPreparationCancellationKeepsBothAttachedWorkbenchesUsable()
    {
        await InitializeRuntimeAsync();
        await AttachedGroupCancellationCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ConstructionCoreAsync(bool loadProfiles)
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile(loadProfiles));
        try
        {
            Assert.False(tab.Workbench.IsStarted);
            Assert.False(tab.IsDisposed);
            Assert.Empty(tab.Workbench.Workbench.Sessions);
            var scope = Assert.IsType<ThemeVariantScope>(tab.Child);
            Assert.Same(tab.Workbench, scope.Child);
            Assert.Equal(ThemeVariant.Light, scope.RequestedThemeVariant);
            Assert.Contains(tab.Workbench, tab.GetVisualDescendants());
        }
        finally { await tab.DisposeAsync(); }
        Assert.True(tab.IsDisposed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task InitializationCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        Assert.Throws<InvalidOperationException>(() => { _ = tab.InitializeAsync(); });
        Assert.Empty(tab.Workbench.Workbench.Sessions);
        host.Owner.Show();
        var underlying = tab.Workbench.InitializeAsync();
        var first = tab.InitializeAsync();
        Assert.Same(first, tab.InitializeAsync());
        await first.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Same(first, tab.InitializeAsync());
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        Assert.True(tab.Workbench.IsStarted);
        Assert.Same(underlying, tab.Workbench.InitializeAsync());
        Assert.Equal(environment.ProfileId, System.Text.Json.JsonSerializer.Deserialize<string>(
            await File.ReadAllTextAsync(Path.Combine(tab.StateDirectory, "profile.json"))));
        Assert.Same(session, tab.Workbench.Workbench.SelectedSession);
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OutputCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        await ExecuteTokenAsync(tab, "Write-Output 'DT-ISE-OUTPUT-TOKEN'", "DT-ISE-OUTPUT-TOKEN");
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DynamicAttachmentCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        var panel = Assert.IsAssignableFrom<Panel>(host.Owner.Content);
        Assert.True(panel.Children.Remove(tab));
        host.Owner.Show();
        Assert.Null(TopLevel.GetTopLevel(tab.Workbench));
        panel.Children.Add(tab);
        Assert.Same(host.Owner, TopLevel.GetTopLevel(tab.Workbench));
        Assert.Empty(tab.Workbench.Workbench.Sessions);
        await tab.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        Assert.True(tab.Workbench.IsStarted);
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        await ExecuteTokenAsync(tab, "'DT-SYNCHRONOUS-ATTACHMENT'", "DT-SYNCHRONOUS-ATTACHMENT");
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task HostedActionsCoreAsync(string action, Key shortcut, RawInputModifiers modifiers)
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var document = session.SelectedFile;
        var errors = new List<Exception>();
        tab.Workbench.ErrorOccurred += (_, error) => errors.Add(error.Exception);
        await tab.Workbench.ExecuteAsync("$global:dtActionToken = 'retained'").WaitAsync(TimeSpan.FromSeconds(60));
        var menu = tab.Workbench.FindControl<Menu>("WorkbenchMenu");
        Assert.NotNull(menu);
        var item = Assert.Single(MenuItems(menu), candidate => Equals(candidate.Tag, action));
        Assert.False(item.IsVisible);
        var sessionTabs = tab.Workbench.FindControl<Control>("SessionTabs");
        Assert.NotNull(sessionTabs);
        Assert.False(sessionTabs.IsVisible);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await AssertBlockedAsync(1);
        if (shortcut != Key.None)
        {
            tab.FocusContent();
            var physicalKey = shortcut == Key.T ? PhysicalKey.T : PhysicalKey.W;
            host.Owner.KeyPress(shortcut, modifiers, physicalKey, null);
            host.Owner.KeyRelease(shortcut, modifiers, physicalKey, null);
            await AssertBlockedAsync(2);
        }
        await ExecuteTokenAsync(tab, "'DT-SESSION-ACTION:' + $global:dtActionToken", "DT-SESSION-ACTION:retained");

        async Task AssertBlockedAsync(int expectedErrors)
        {
            await WaitForAsync(
                () => errors.Count >= expectedErrors || tab.Workbench.Workbench.Sessions.Count != 1 ||
                      !ReferenceEquals(session, tab.Workbench.Workbench.SelectedSession) || host.Owner.OwnedWindows.Count > 0,
                () => $"No host rejection for {action}; errors={errors.Count}, sessions={tab.Workbench.Workbench.Sessions.Count}.");
            Assert.Equal(expectedErrors, errors.Count);
            Assert.All(errors, error =>
                Assert.Equal("This feature is disabled by the workbench host.", Assert.IsType<NotSupportedException>(error).Message));
            Assert.Equal(expectedErrors, host.Errors.Count);
            Assert.Same(session, Assert.Single(tab.Workbench.Workbench.Sessions));
            Assert.Same(session, tab.Workbench.Workbench.SelectedSession);
            Assert.Same(document, session.SelectedFile);
            Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
            Assert.True(tab.Workbench.IsStarted);
            Assert.False(tab.IsDisposed);
            Assert.Empty(host.Owner.OwnedWindows);
        }
    }

    private static IEnumerable<MenuItem> MenuItems(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var descendant in MenuItems(item)) yield return descendant;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task IsolationCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var first = new PowerShellIseTab(environment.Profile());
        var second = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(first, second);
        await host.InitializeAsync();
        var a = first.Workbench.Workbench.SelectedSession!;
        var b = second.Workbench.Workbench.SelectedSession!;
        Assert.NotSame(first.Workbench, second.Workbench);
        Assert.NotSame(a.Engine, b.Engine);
        Assert.NotEqual(a.Engine.LocalRunspaceId, b.Engine.LocalRunspaceId);
        await first.Workbench.ExecuteAsync("$global:dtRegressionValue='alpha'");
        await ExecuteTokenAsync(first, "'DT-A-FIRST:' + $global:dtRegressionValue", "DT-A-FIRST:alpha");
        await ExecuteTokenAsync(second,
            "if ($null -eq $global:dtRegressionValue) { 'DT-B-UNSET' } else { 'DT-B-LEAK:' + $global:dtRegressionValue }", "DT-B-UNSET");
        Assert.DoesNotContain("DT-B-LEAK:alpha", NativeText(second), StringComparison.Ordinal);
        await second.Workbench.ExecuteAsync("$global:dtRegressionValue='beta'");
        await ExecuteTokenAsync(second, "'DT-B-SECOND:' + $global:dtRegressionValue", "DT-B-SECOND:beta");
        await ExecuteTokenAsync(first, "'DT-A-AFTER:' + $global:dtRegressionValue", "DT-A-AFTER:alpha");
        Assert.DoesNotContain("DT-B-SECOND:beta", NativeText(first), StringComparison.Ordinal);
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DisposalCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var first = Assert.Single(tab.Workbench.Workbench.Sessions);
        var connection = NativeConnection(tab);
        await Assert.ThrowsAsync<NotSupportedException>(() => tab.Workbench.CreateSessionAsync());
        Assert.Same(first, Assert.Single(tab.Workbench.Workbench.Sessions));
        IHostedTabContent hosted = tab;
        var disposal = hosted.DisposeAsync();
        Assert.True(hosted.IsDisposed);
        Assert.True(tab.Workbench.IsDisposed);
        Assert.False(tab.Workbench.IsStarted);
        Assert.False(tab.Workbench.IsEnabled);
        await disposal;
        await tab.Workbench.DisposeAsync();
        await tab.DisposeAsync();
        await tab.DisposeAsync();
        Assert.True(tab.IsDisposed);
        Assert.False(tab.Workbench.IsStarted);
        Assert.Equal(Iseberg.Core.SessionState.Disposed, first.Engine.State);
        Assert.False(tab.Terminal.IsRunning);
        Assert.Equal(Devolutions.Terminal.Connection.TerminalConnectionState.Disposed, connection.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tab.Workbench.ExecuteAsync("'must not execute'"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CloseCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var session = tab.Workbench.Workbench.SelectedSession!;
        Assert.True(await tab.Workbench.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(Iseberg.Core.SessionState.Disposed, session.Engine.State);
        Assert.True(tab.Workbench.IsDisposed);
        Assert.False(tab.Workbench.IsStarted);
        Assert.True(host.Owner.IsVisible);
        Assert.True(await tab.RequestCloseAsync());
        await tab.DisposeAsync();
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DirtyCancellationCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var session = tab.Workbench.Workbench.SelectedSession!;
        var terminal = tab.Terminal;
        var connection = NativeConnection(tab);
        var runspace = session.Engine.LocalRunspaceId;
        await SubmitTokenAsync(tab, "$global:dtCancelValue = 'kept'; 'DT-BEFORE-DIRTY-CANCEL'", "DT-BEFORE-DIRTY-CANCEL");
        const string text = "Write-Output 'unsaved-content'";
        var document = tab.Workbench.CreateDocument(text);
        Assert.True(document.File.IsDirty);
        var before = host.Owner.OwnedWindows.ToArray();
        var request = tab.Workbench.RequestCloseAsync();
        var dialog = await NewDialogAsync(host.Owner, before, request);
        ClickCancel(dialog);
        Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.NotNull(tab.Parent);
        Assert.True(tab.Workbench.IsEnabled);
        Assert.True(tab.Workbench.IsStarted);
        Assert.False(tab.IsDisposed);
        Assert.Contains(document, session.Files);
        Assert.Same(document, session.SelectedFile);
        Assert.Equal(text, document.Document.Text);
        Assert.Same(terminal, tab.Terminal);
        Assert.Same(connection, NativeConnection(tab));
        Assert.Equal(runspace, session.Engine.LocalRunspaceId);
        Assert.True(terminal.IsRunning);
        await ExecuteTokenAsync(tab, "'DT-ISE-AFTER-CANCEL'", "DT-ISE-AFTER-CANCEL");
        await SubmitTokenAsync(tab, "'DT-NATIVE-CANCEL:' + $global:dtCancelValue", "DT-NATIVE-CANCEL:kept");
        Assert.True(await tab.Workbench.SaveDocumentAsync(document, Path.Combine(environment.DirectoryPath, "cancelled.ps1")));
        Assert.False(document.File.IsDirty);
        Assert.Empty(host.Errors);
        Assert.True(await tab.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.False(terminal.IsRunning);
        Assert.True(tab.IsDisposed);
        Assert.Equal(Iseberg.Core.SessionState.Disposed, session.Engine.State);
        Assert.Equal(Devolutions.Terminal.Connection.TerminalConnectionState.Disposed, connection.State);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task PreparationCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var session = tab.Workbench.Workbench.SelectedSession!;
        Assert.True(await tab.Workbench.PrepareCloseAsync().WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.False(tab.Workbench.IsEnabled);
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        tab.Workbench.CancelClosePreparation();
        Assert.True(tab.Workbench.IsEnabled);
        Assert.True(tab.Workbench.IsStarted);
        Assert.False(tab.IsDisposed);
        Assert.True(await tab.PrepareCloseAsync());
        Assert.False(tab.Workbench.IsEnabled);
        tab.CancelClosePreparation();
        Assert.True(tab.Workbench.IsEnabled);
        await ExecuteTokenAsync(tab, "'DT-ISE-AFTER-PREPARE-CANCEL'", "DT-ISE-AFTER-PREPARE-CANCEL");
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AttachedGroupCancellationCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var first = new PowerShellIseTab(environment.Profile());
        var second = new PowerShellIseTab(environment.Profile());
        await using var host = new IseTestHost(first, second);
        await host.InitializeAsync();
        var firstSession = Assert.Single(first.Workbench.Workbench.Sessions);
        var secondSession = Assert.Single(second.Workbench.Workbench.Sessions);
        const string text = "Write-Output 'attached-group-unsaved-content'";
        var document = second.Workbench.CreateDocument(text);
        Assert.True(document.File.IsDirty);
        try
        {
            Assert.True(await first.PrepareCloseAsync().WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.False(first.Workbench.IsEnabled);
            Assert.False(first.IsDisposed);
            var before = host.Owner.OwnedWindows.ToArray();
            var request = second.PrepareCloseAsync();
            var dialog = await NewDialogAsync(host.Owner, before, request);
            Assert.False(first.Workbench.IsEnabled);
            Assert.Equal(Iseberg.Core.SessionState.Ready, firstSession.Engine.State);
            ClickCancel(dialog);
            Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.False(first.Workbench.IsEnabled);
            Assert.False(first.IsDisposed);
            Assert.False(second.IsDisposed);
        }
        finally
        {
            first.CancelClosePreparation();
            second.CancelClosePreparation();
        }
        Assert.All(new[] { first, second }, tab =>
        {
            Assert.Same(host.Owner, TopLevel.GetTopLevel(tab.Workbench));
            Assert.True(tab.Workbench.IsEnabled);
            Assert.True(tab.Workbench.IsStarted);
            Assert.False(tab.IsDisposed);
        });
        Assert.Same(firstSession, Assert.Single(first.Workbench.Workbench.Sessions));
        Assert.Same(secondSession, Assert.Single(second.Workbench.Workbench.Sessions));
        Assert.Contains(document, secondSession.Files);
        Assert.Same(document, secondSession.SelectedFile);
        Assert.Equal(text, document.Document.Text);
        Assert.True(document.File.IsDirty);
        await ExecuteTokenAsync(first, "'DT-ATTACHED-GROUP-FIRST'", "DT-ATTACHED-GROUP-FIRST");
        await ExecuteTokenAsync(second, "'DT-ATTACHED-GROUP-SECOND'", "DT-ATTACHED-GROUP-SECOND");
        Assert.True(await second.Workbench.SaveDocumentAsync(document,
            Path.Combine(environment.DirectoryPath, "attached-group-cancelled.ps1")));
        Assert.Empty(host.Errors);
    }

    [AvaloniaFact]
    public async Task SuppliedWorkspaceIdentityAndRootAreStableBeforeInitialization()
    {
        await InitializeRuntimeAsync();
        await WorkspaceIdentityContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task WorkspaceIdentityContractCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var root = Path.Combine(environment.DirectoryPath, "owned-state");
        var expected = Path.Combine(root, "Workspaces", "12345678123412341234123456789abc");
        var first = new PowerShellIseTab(environment.Profile(), workspaceId: id, stateDirectory: root);
        var repeated = new PowerShellIseTab(environment.Profile(), workspaceId: id, stateDirectory: root);
        try
        {
            Assert.Equal(id, first.WorkspaceId);
            Assert.Equal(id, repeated.WorkspaceId);
            Assert.Equal(expected, first.StateDirectory);
            Assert.Equal(expected, repeated.StateDirectory);
            Assert.False(first.Workbench.IsStarted);
            Assert.False(repeated.Workbench.IsStarted);
            Assert.Empty(first.Workbench.Workbench.Sessions);
            Assert.Empty(repeated.Workbench.Workbench.Sessions);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            await first.DisposeAsync();
            await repeated.DisposeAsync();
        }
    }

    [AvaloniaTheory]
    [InlineData(true, "workspaceId")]
    [InlineData(false, "stateDirectory")]
    public async Task WorkspaceIdentityRejectsEmptyIdOrRelativeRoot(bool emptyId, string parameter)
    {
        await InitializeRuntimeAsync();
        WorkspaceValidationContractCore(emptyId, parameter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WorkspaceValidationContractCore(bool emptyId, string parameter)
    {
        using var environment = new IseTestEnvironment();
        var error = Assert.Throws<ArgumentException>(() => new PowerShellIseTab(environment.Profile(),
            workspaceId: emptyId ? Guid.Empty : Guid.Parse("12345678-1234-1234-1234-123456789abc"),
            stateDirectory: emptyId ? environment.DirectoryPath : "relative-state"));
        Assert.Equal(parameter, error.ParamName);
        Assert.Empty(Directory.GetFiles(environment.DirectoryPath));
    }

    [AvaloniaFact]
    public async Task ExplicitRecoveryKeepsTwoLiveTabStoresIndependentAndDisposalReleasesOnlyOne()
    {
        await InitializeRuntimeAsync();
        await IndependentRecoveryContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task IndependentRecoveryContractCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var root = Path.Combine(environment.DirectoryPath, "owned-state");
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var first = new PowerShellIseTab(environment.Profile(), workspaceId: firstId, stateDirectory: root);
        var second = new PowerShellIseTab(environment.Profile(), workspaceId: secondId, stateDirectory: root);
        await DisableRecoveryAutosaveAsync(first);
        await DisableRecoveryAutosaveAsync(second);
        await using var host = new IseTestHost(first, second);
        await host.InitializeAsync();
        Assert.Equal(Path.Combine(root, "Workspaces", "11111111111111111111111111111111"), first.StateDirectory);
        Assert.Equal(Path.Combine(root, "Workspaces", "22222222222222222222222222222222"), second.StateDirectory);
        var a = Assert.Single(first.Workbench.Workbench.Sessions);
        var b = Assert.Single(second.Workbench.Workbench.Sessions);
        Assert.NotSame(a.Engine, b.Engine);
        var firstDocument = first.Workbench.CreateDocument("first draft\r\ncafé");
        first.Workbench.ScriptEditorView.CaretOffset = 5;
        var secondDocument = second.Workbench.CreateDocument("second draft\nline");
        second.Workbench.ScriptEditorView.CaretOffset = 6;
        await first.Workbench.SaveRecoveryAsync();
        var firstSettings = Path.Combine(first.StateDirectory, "settings.json");
        var secondSettings = Path.Combine(second.StateDirectory, "settings.json");
        var firstSnapshot = Path.Combine(firstSettings + ".recovery", firstDocument.RecoveryId.ToString("N") + ".json");
        var secondSnapshot = Path.Combine(secondSettings + ".recovery", secondDocument.RecoveryId.ToString("N") + ".json");
        var firstBytes = await File.ReadAllBytesAsync(firstSnapshot);
        Assert.False(Directory.Exists(secondSettings + ".recovery"));
        await second.Workbench.SaveRecoveryAsync();
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstSnapshot));
        var secondBytes = await File.ReadAllBytesAsync(secondSnapshot);
        var secondMetadata = await File.ReadAllBytesAsync(secondSettings + ".workbench.json");
        firstDocument.Document.Text = "first revised";
        first.Workbench.ScriptEditorView.CaretOffset = 3;
        await first.Workbench.SaveRecoveryAsync();
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(secondSnapshot));
        Assert.Equal(secondMetadata, await File.ReadAllBytesAsync(secondSettings + ".workbench.json"));
        Assert.Equal("second draft\nline", secondDocument.Document.Text);
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(firstSettings));
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(secondSettings));
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(Iseberg.Core.SessionState.Disposed, a.Engine.State);
        using (var reacquired = new Iseberg.Core.WorkbenchPersistenceLease(firstSettings))
        {
            var released = Assert.Single(await new Iseberg.Core.ScriptRecovery(firstSettings + ".recovery").ReadAsync());
            Assert.Equal(firstDocument.RecoveryId, released.Id);
            Assert.Equal("first revised", released.Text);
            Assert.Equal("Untitled2.ps1", released.Name);
            Assert.Equal("PowerShell 1", released.SessionName);
            Assert.Equal(0, released.OwnerProcessId);
            var state = await new Iseberg.Core.WorkbenchStateStore(firstSettings + ".workbench.json").LoadAsync();
            Assert.NotNull(state);
            Assert.Equal(0, state.OwnerProcessId);
            Assert.Equal(new[] { 0, 3 }, Assert.Single(state.Sessions).Documents.Select(doc => doc.CaretOffset).ToArray());
        }
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(secondSettings));
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(secondSnapshot));
        Assert.Empty(await new Iseberg.Core.ScriptRecovery(secondSettings + ".recovery").ReadAsync());
        Assert.Equal(Iseberg.Core.SessionState.Ready, b.Engine.State);
        Assert.True(NativeConnection(second).IsInputEnabled);
        Assert.Empty(host.Errors);
    }

    [AvaloniaFact]
    public async Task DirtyCloseCancelRetainsExplicitRecoveryBytesLeaseAndEngine()
    {
        await InitializeRuntimeAsync();
        await RecoveryCancellationContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecoveryCancellationContractCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var tab = new PowerShellIseTab(environment.Profile(),
            workspaceId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            stateDirectory: Path.Combine(environment.DirectoryPath, "cancel-state"));
        await DisableRecoveryAutosaveAsync(tab);
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var connection = NativeConnection(tab);
        var document = tab.Workbench.CreateDocument("cancel keeps this draft");
        tab.Workbench.ScriptEditorView.CaretOffset = 4;
        await tab.Workbench.SaveRecoveryAsync();
        var settingsPath = Path.Combine(tab.StateDirectory, "settings.json");
        var snapshotPath = Path.Combine(settingsPath + ".recovery", document.RecoveryId.ToString("N") + ".json");
        var snapshot = await File.ReadAllBytesAsync(snapshotPath);
        var metadata = await File.ReadAllBytesAsync(settingsPath + ".workbench.json");
        var before = host.Owner.OwnedWindows.ToArray();
        var close = tab.RequestCloseAsync();
        var dialog = await NewDialogAsync(host.Owner, before, close);
        ClickCancel(dialog);
        Assert.False(await close.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(snapshot, await File.ReadAllBytesAsync(snapshotPath));
        Assert.Equal(metadata, await File.ReadAllBytesAsync(settingsPath + ".workbench.json"));
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(settingsPath));
        Assert.Empty(await new Iseberg.Core.ScriptRecovery(settingsPath + ".recovery").ReadAsync());
        Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
        Assert.Same(document, session.SelectedFile);
        Assert.Equal("cancel keeps this draft", document.Document.Text);
        Assert.True(document.File.IsDirty);
        Assert.Equal(4, tab.Workbench.ScriptEditorView.CaretOffset);
        Assert.Same(connection, NativeConnection(tab));
        Assert.True(connection.IsInputEnabled);
        Assert.True(tab.Workbench.IsEnabled);
        Assert.False(tab.IsDisposed);
        await ExecuteTokenAsync(tab, "'DT-RECOVERY-CANCEL-ENGINE-RETAINED'", "DT-RECOVERY-CANCEL-ENGINE-RETAINED");
        Assert.Equal(snapshot, await File.ReadAllBytesAsync(snapshotPath));
        Assert.Empty(host.Errors);
    }

    private static Task DisableRecoveryAutosaveAsync(PowerShellIseTab tab) =>
        new Iseberg.Core.UserSettings
        {
            AutoSaveMinutes = 0, LoadProfiles = false, CheckForUpdates = false, ShowCommands = false
        }.SaveAsync(Path.Combine(tab.StateDirectory, "settings.json"));

    [AvaloniaFact]
    public async Task SameWorkspaceReopenRecoveryCancelPreservesReleasedSnapshot()
    {
        await InitializeRuntimeAsync();
        await ReopenRecoveryCancelContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ReopenRecoveryCancelContractCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var id = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var root = Path.Combine(environment.DirectoryPath, "reopen-state");
        var first = new PowerShellIseTab(environment.Profile(), workspaceId: id, stateDirectory: root);
        await DisableRecoveryAutosaveAsync(first);
        string snapshotPath;
        await using (var firstHost = new IseTestHost(first))
        {
            await firstHost.InitializeAsync();
            var draft = first.Workbench.CreateDocument("preserve released draft");
            draft.File.SetEncoding(new Iseberg.Core.ScriptEncoding(65001, true));
            first.Workbench.ScriptEditorView.CaretOffset = 7;
            await first.Workbench.SaveRecoveryAsync();
            snapshotPath = Path.Combine(first.StateDirectory, "settings.json.recovery", draft.RecoveryId.ToString("N") + ".json");
        }
        var releasedBytes = await File.ReadAllBytesAsync(snapshotPath);
        var reopened = new PowerShellIseTab(environment.Profile(), workspaceId: id, stateDirectory: root);
        await using var host = new IseTestHost(reopened);
        host.Owner.Show();
        var before = host.Owner.OwnedWindows.ToArray();
        var initialization = reopened.InitializeAsync();
        var dialog = await NewDialogAsync(host.Owner, before, initialization);
        ClickCancel(dialog);
        await initialization.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(id, reopened.WorkspaceId);
        Assert.Equal(first.StateDirectory, reopened.StateDirectory);
        Assert.Equal(releasedBytes, await File.ReadAllBytesAsync(snapshotPath));
        var session = Assert.Single(reopened.Workbench.Workbench.Sessions);
        Assert.Equal("PowerShell 1", session.Name);
        Assert.Equal(new[] { "Untitled1.ps1", "Untitled2.ps1" }, session.Files.Select(doc => doc.File.Name).ToArray());
        Assert.All(session.Files, doc =>
        {
            Assert.Equal("", doc.Document.Text);
            Assert.False(doc.File.IsDirty);
        });
        Assert.Equal("Untitled2.ps1", session.SelectedFile!.File.Name);
        Assert.Equal(new Iseberg.Core.ScriptEncoding(65001, true, "UTF-8 BOM"), session.SelectedFile.File.EncodingChoice);
        var retained = Assert.Single(await new Iseberg.Core.ScriptRecovery(
            Path.Combine(reopened.StateDirectory, "settings.json.recovery")).ReadAsync());
        Assert.Equal("preserve released draft", retained.Text);
        Assert.Equal("Untitled2.ps1", retained.Name);
        Assert.Equal(0, retained.OwnerProcessId);
        Assert.Equal(new Iseberg.Core.ScriptEncoding(65001, true, "UTF-8 BOM"), retained.Encoding);
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(
            Path.Combine(reopened.StateDirectory, "settings.json")));
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        Assert.False(reopened.IsDisposed);
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData("recovered draft", 65001)]
    [InlineData("", 65001)]
    [InlineData("caf\u00e9\r\n\u03b2\ud83d\ude00\n", 1200)]
    public async Task RecoverUntitledDraftReplacesPlaceholderAndTransfersSnapshotToLiveOwner(string text, int codePage)
    {
        await InitializeRuntimeAsync();
        await RecoverUntitledContractCoreAsync(text, codePage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecoverUntitledContractCoreAsync(string text, int codePage)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var root = Path.Combine(environment.DirectoryPath, "recover-untitled");
        var first = new PowerShellIseTab(profile, stateDirectory: root);
        var encoding = new Iseberg.Core.ScriptEncoding(codePage, true);
        Guid originalId;
        await using (var firstHost = new IseTestHost(first))
        {
            await firstHost.InitializeAsync();
            var draft = first.Workbench.CreateDocument(text);
            draft.File.SetEncoding(encoding);
            first.Workbench.ScriptEditorView.CaretOffset = 0;
            Assert.True(draft.File.IsDirty);
            await first.Workbench.SaveRecoveryAsync();
            originalId = draft.RecoveryId;
        }
        var originalPath = RecoverySnapshotPath(first, originalId);
        var released = JsonSerializer.Deserialize<Iseberg.Core.RecoveredScript>(await File.ReadAllTextAsync(originalPath))!;
        Assert.Equal(0, released.OwnerProcessId);
        Assert.Equal(text, released.Text);

        var reopened = new PowerShellIseTab(profile, workspaceId: first.WorkspaceId, stateDirectory: root);
        await using var host = new IseTestHost(reopened);
        Iseberg.ScriptTab[] placeholders = [];
        Iseberg.Core.PowerShellSession? restoredEngine = null;
        await ChooseRecoveryAsync(host, reopened, "Recover", () =>
        {
            var restored = Assert.Single(reopened.Workbench.Workbench.Sessions);
            placeholders = restored.Files.ToArray();
            restoredEngine = restored.Engine;
            Assert.Equal(new[] { "Untitled1.ps1", "Untitled2.ps1" }, placeholders.Select(file => file.File.Name).ToArray());
            Assert.All(placeholders, file => Assert.False(file.File.IsDirty));
        });

        var session = Assert.Single(reopened.Workbench.Workbench.Sessions);
        Assert.Equal(2, session.Files.Count);
        Assert.Same(placeholders[0], session.Files[0]);
        Assert.DoesNotContain(placeholders[1], session.Files);
        var recovered = session.Files[1];
        Assert.Same(recovered, session.SelectedFile);
        Assert.Equal("Untitled2.ps1", recovered.File.Name);
        Assert.Equal(text, recovered.Document.Text);
        Assert.Equal(text, reopened.Workbench.ScriptEditorView.Document.Text);
        Assert.Equal(encoding, recovered.File.EncodingChoice);
        Assert.True(recovered.File.IsDirty);
        Assert.Null(recovered.File.Path);
        Assert.NotEqual(originalId, recovered.RecoveryId);
        Assert.False(File.Exists(originalPath));
        Assert.Equal(new[] { RecoverySnapshotPath(reopened, recovered.RecoveryId) },
            Directory.GetFiles(Path.Combine(reopened.StateDirectory, "settings.json.recovery"), "*.json"));
        var live = JsonSerializer.Deserialize<Iseberg.Core.RecoveredScript>(
            await File.ReadAllTextAsync(RecoverySnapshotPath(reopened, recovered.RecoveryId)))!;
        Assert.Equal(recovered.RecoveryId, live.Id);
        Assert.Equal(Environment.ProcessId, live.OwnerProcessId);
        Assert.Equal("PowerShell 1", live.SessionName);
        Assert.Equal("Untitled2.ps1", live.Name);
        Assert.Equal(text, live.Text);
        Assert.Equal(encoding, live.Encoding);
        Assert.Empty(await new Iseberg.Core.ScriptRecovery(
            Path.Combine(reopened.StateDirectory, "settings.json.recovery")).ReadAsync());
        Assert.Same(restoredEngine, session.Engine);
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(
            Path.Combine(reopened.StateDirectory, "settings.json")));
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DiscardRecoveryDeletesOnlyOwnedSnapshotsAndRetainsRestoredDocuments(int draftCount)
    {
        await InitializeRuntimeAsync();
        await DiscardRecoveryContractCoreAsync(draftCount);
    }

    [AvaloniaTheory]
    [InlineData("Untitled1.ps1", null, 1)]
    [InlineData("legacy draft.ps1", null, 2)]
    [InlineData("Untitled1.ps1", "PowerShell 99", 1)]
    [InlineData("legacy draft.ps1", "PowerShell 99", 2)]
    public async Task RecoverLegacySnapshotWithoutWorkbenchStateUsesSelectedSessionAndMatchingPlaceholder(
        string name, string? savedSession, int expectedCount)
    {
        await InitializeRuntimeAsync();
        await RecoverLegacySnapshotContractCoreAsync(name, savedSession, expectedCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecoverLegacySnapshotContractCoreAsync(string name, string? savedSession, int expectedCount)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile, stateDirectory: Path.Combine(environment.DirectoryPath, "legacy-recovery"));
        await DisableRecoveryAutosaveAsync(tab);
        var id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var directory = Path.Combine(tab.StateDirectory, "settings.json.recovery");
        Directory.CreateDirectory(directory);
        const string text = "legacy recovered draft";
        await File.WriteAllTextAsync(RecoverySnapshotPath(tab, id), JsonSerializer.Serialize(
            new { Id = id, Name = name, Path = (string?)null, Text = text, SessionName = savedSession }));
        Assert.False(File.Exists(Path.Combine(tab.StateDirectory, "settings.json.workbench.json")));
        await using var host = new IseTestHost(tab);
        Iseberg.ScriptTab? placeholder = null;
        Iseberg.Core.PowerShellSession? initialEngine = null;
        await ChooseRecoveryAsync(host, tab, "Recover", () =>
        {
            var session = Assert.Single(tab.Workbench.Workbench.Sessions);
            placeholder = Assert.Single(session.Files);
            initialEngine = session.Engine;
        });
        var restored = Assert.Single(tab.Workbench.Workbench.Sessions);
        Assert.Equal("PowerShell 1", restored.Name);
        Assert.Equal(expectedCount, restored.Files.Count);
        var document = Assert.Single(restored.Files, file => file.File.IsDirty);
        Assert.Equal(name, document.File.Name);
        Assert.Equal(text, document.Document.Text);
        Assert.Null(document.File.Path);
        Assert.Equal(new Iseberg.Core.ScriptEncoding(65001, false), document.File.EncodingChoice);
        Assert.Same(document, restored.SelectedFile);
        Assert.Same(document.Document, tab.Workbench.ScriptEditorView.Document);
        if (expectedCount == 1) Assert.DoesNotContain(placeholder, restored.Files);
        else
        {
            Assert.Same(placeholder, restored.Files[0]);
            Assert.Equal("Untitled1.ps1", restored.Files[0].File.Name);
            Assert.Equal("", restored.Files[0].Document.Text);
            Assert.False(restored.Files[0].File.IsDirty);
        }
        Assert.False(File.Exists(RecoverySnapshotPath(tab, id)));
        Assert.Equal(new[] { RecoverySnapshotPath(tab, document.RecoveryId) }, Directory.GetFiles(directory, "*.json"));
        var live = JsonSerializer.Deserialize<Iseberg.Core.RecoveredScript>(
            await File.ReadAllTextAsync(RecoverySnapshotPath(tab, document.RecoveryId)))!;
        Assert.Equal("PowerShell 1", live.SessionName);
        Assert.Equal(name, live.Name);
        Assert.Equal(text, live.Text);
        Assert.Equal(new Iseberg.Core.ScriptEncoding(65001, false), live.Encoding);
        Assert.Equal(Environment.ProcessId, live.OwnerProcessId);
        Assert.Same(initialEngine, restored.Engine);
        Assert.Equal(Iseberg.Core.SessionState.Ready, restored.Engine.State);
        Assert.Empty(host.Errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DiscardRecoveryContractCoreAsync(int draftCount)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var root = Path.Combine(environment.DirectoryPath, "discard-recovery");
        var first = new PowerShellIseTab(profile, stateDirectory: root);
        await using (var firstHost = new IseTestHost(first))
        {
            await firstHost.InitializeAsync();
            for (var index = 0; index < draftCount; index++)
                first.Workbench.CreateDocument("discard draft " + index);
            await first.Workbench.SaveRecoveryAsync();
        }
        var recoveryDirectory = Path.Combine(first.StateDirectory, "settings.json.recovery");
        var releasedPaths = Directory.GetFiles(recoveryDirectory, "*.json");
        Assert.Equal(draftCount, releasedPaths.Length);
        var foreignDirectory = Path.Combine(root, "foreign-owned-fixture", "recovery");
        var foreignId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await new Iseberg.Core.ScriptRecovery(foreignDirectory).SaveAsync(foreignId,
            Iseberg.Core.ScriptFile.FromRecovery("foreign.ps1", "keep foreign draft"), "PowerShell 9", released: true);
        var foreignPath = Path.Combine(foreignDirectory, foreignId.ToString("N") + ".json");
        var foreignBytes = await File.ReadAllBytesAsync(foreignPath);

        var reopened = new PowerShellIseTab(profile, workspaceId: first.WorkspaceId, stateDirectory: root);
        await using var host = new IseTestHost(reopened);
        Iseberg.ScriptTab[] restoredFiles = [];
        Iseberg.ScriptTab? selectedFile = null;
        Iseberg.Core.PowerShellSession? restoredEngine = null;
        await ChooseRecoveryAsync(host, reopened, "Discard", () =>
        {
            var restored = Assert.Single(reopened.Workbench.Workbench.Sessions);
            restoredFiles = restored.Files.ToArray();
            selectedFile = restored.SelectedFile;
            restoredEngine = restored.Engine;
        });

        var session = Assert.Single(reopened.Workbench.Workbench.Sessions);
        Assert.Equal(draftCount + 1, session.Files.Count);
        Assert.Equal(Enumerable.Range(1, draftCount + 1).Select(index => $"Untitled{index}.ps1").ToArray(),
            session.Files.Select(file => file.File.Name).ToArray());
        for (var index = 0; index < restoredFiles.Length; index++)
        {
            Assert.Same(restoredFiles[index], session.Files[index]);
            Assert.Equal("", session.Files[index].Document.Text);
            Assert.False(session.Files[index].File.IsDirty);
        }
        Assert.Same(selectedFile, session.SelectedFile);
        Assert.Equal($"Untitled{draftCount + 1}.ps1", session.SelectedFile!.File.Name);
        Assert.All(releasedPaths, path => Assert.False(File.Exists(path)));
        Assert.Empty(Directory.GetFiles(recoveryDirectory, "*.json"));
        Assert.Empty(await new Iseberg.Core.ScriptRecovery(recoveryDirectory).ReadAsync());
        Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(foreignPath));
        Assert.Equal("keep foreign draft", Assert.Single(
            await new Iseberg.Core.ScriptRecovery(foreignDirectory).ReadAsync()).Text);
        Assert.Same(restoredEngine, session.Engine);
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        Assert.Equal("", reopened.Workbench.ScriptEditorView.Document.Text);
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData(65001)]
    [InlineData(1200)]
    public async Task RecoverSavedDocumentCreatesDetachedDraftWithoutOverwritingSavedFile(int codePage)
    {
        await InitializeRuntimeAsync();
        await RecoverSavedDocumentContractCoreAsync(codePage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecoverSavedDocumentContractCoreAsync(int codePage)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var root = Path.Combine(environment.DirectoryPath, "recover-saved");
        var path = Path.Combine(environment.DirectoryPath, "owned script.ps1");
        const string savedText = "$answer = 1\r\n";
        const string draftText = "Write-Output 'caf\u00e9'\r\n";
        var encoding = new Iseberg.Core.ScriptEncoding(codePage, true);
        var expectedBytes = encoding.CreateEncoding().GetPreamble()
            .Concat(encoding.CreateEncoding().GetBytes(savedText)).ToArray();
        var first = new PowerShellIseTab(profile, stateDirectory: root);
        Guid originalId;
        await using (var firstHost = new IseTestHost(first))
        {
            await firstHost.InitializeAsync();
            var draft = first.Workbench.CreateDocument(savedText);
            draft.File.SetEncoding(encoding);
            await draft.File.SaveAsync(path);
            Assert.False(draft.File.IsDirty);
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(path));
            draft.Document.Text = draftText;
            Assert.True(draft.File.IsDirty);
            await first.Workbench.SaveRecoveryAsync();
            originalId = draft.RecoveryId;
        }
        var originalPath = RecoverySnapshotPath(first, originalId);
        var released = JsonSerializer.Deserialize<Iseberg.Core.RecoveredScript>(await File.ReadAllTextAsync(originalPath))!;
        Assert.Equal(path, released.Path);
        Assert.Equal(draftText, released.Text);
        Assert.Equal(encoding, released.Encoding);

        var reopened = new PowerShellIseTab(profile, workspaceId: first.WorkspaceId, stateDirectory: root);
        await using var host = new IseTestHost(reopened);
        await ChooseRecoveryAsync(host, reopened, "Recover");
        var session = Assert.Single(reopened.Workbench.Workbench.Sessions);
        Assert.Equal(3, session.Files.Count);
        Assert.Equal(new[] { "Untitled1.ps1", "owned script.ps1", "owned script.ps1" },
            session.Files.Select(file => file.File.Name).ToArray());
        var saved = Assert.Single(session.Files, file => file.File.Path == path);
        Assert.Equal(savedText, saved.Document.Text);
        Assert.False(saved.File.IsDirty);
        Assert.Equal(encoding, saved.File.EncodingChoice);
        var recovered = Assert.Single(session.Files, file => file.File.IsDirty);
        Assert.Null(recovered.File.Path);
        Assert.Equal("owned script.ps1", recovered.File.Name);
        Assert.Equal(draftText, recovered.Document.Text);
        Assert.Equal(encoding, recovered.File.EncodingChoice);
        Assert.Same(recovered, session.SelectedFile);
        Assert.Equal(draftText, reopened.Workbench.ScriptEditorView.Document.Text);
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(originalPath));
        var live = JsonSerializer.Deserialize<Iseberg.Core.RecoveredScript>(
            await File.ReadAllTextAsync(RecoverySnapshotPath(reopened, recovered.RecoveryId)))!;
        Assert.Equal(recovered.RecoveryId, live.Id);
        Assert.Null(live.Path);
        Assert.Equal(draftText, live.Text);
        Assert.Equal(encoding, live.Encoding);
        Assert.Equal(Environment.ProcessId, live.OwnerProcessId);
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        Assert.Empty(host.Errors);
    }

    [AvaloniaFact]
    public async Task RecoverReplacesUntitledPlaceholdersWithoutReorderingDocuments()
    {
        await InitializeRuntimeAsync();
        await RecoverMetadataContractCoreAsync(0, "order");
    }

    [AvaloniaTheory]
    [InlineData("save")]
    [InlineData("remove")]
    public async Task RecoveryStorageFailureKeepsPublishedDraftAndOriginalSnapshotForRetry(string operation)
    {
        await InitializeRuntimeAsync();
        await RecoveryStorageFailureContractCoreAsync(operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecoveryStorageFailureContractCoreAsync(string operation)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var root = Path.Combine(environment.DirectoryPath, "recovery-storage-failure");
        var first = new PowerShellIseTab(profile, stateDirectory: root);
        const string text = "failure-preserved draft";
        Guid originalId;
        await using (var firstHost = new IseTestHost(first))
        {
            await firstHost.InitializeAsync();
            var document = first.Workbench.CreateDocument(text);
            document.File.SetEncoding(new(65001, true));
            await first.Workbench.SaveRecoveryAsync();
            originalId = document.RecoveryId;
        }
        var originalPath = RecoverySnapshotPath(first, originalId);
        var originalBytes = await File.ReadAllBytesAsync(originalPath);
        var metadataPath = Path.Combine(first.StateDirectory, "settings.json.workbench.json");
        var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
        var backupPath = Path.Combine(first.StateDirectory, "fault-original-snapshot.backup");
        var reopened = new PowerShellIseTab(profile, workspaceId: first.WorkspaceId, stateDirectory: root);
        await using var host = new IseTestHost(reopened);
        Iseberg.ScriptTab? published = null;
        string? blockedPath = null;
        try
        {
            var error = await Record.ExceptionAsync(() => ChooseRecoveryAsync(host, reopened, "Recover", () =>
            {
                var session = Assert.Single(reopened.Workbench.Workbench.Sessions);
                session.Files.CollectionChanged += (_, args) =>
                {
                    if (args.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
                    published = Assert.Single(args.NewItems!.OfType<Iseberg.ScriptTab>());
                    Assert.Equal(text, published.Document.Text);
                    Assert.True(published.File.IsDirty);
                    blockedPath = operation == "save" ? RecoverySnapshotPath(reopened, published.RecoveryId) : originalPath;
                    if (operation == "remove") File.Move(originalPath, backupPath);
                    Directory.CreateDirectory(blockedPath);
                };
            }));
            Assert.NotNull(error);
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected explicit recovery storage error, got {error}.");
            var draft = Assert.IsType<Iseberg.ScriptTab>(published);
            var session = Assert.Single(reopened.Workbench.Workbench.Sessions);
            Assert.Contains(draft, session.Files);
            Assert.Equal(text, draft.Document.Text);
            Assert.Equal(new Iseberg.Core.ScriptEncoding(65001, true), draft.File.EncodingChoice);
            Assert.True(draft.File.IsDirty);
            Assert.Null(draft.File.Path);
            Assert.NotEqual(originalId, draft.RecoveryId);
            Assert.NotNull(blockedPath);
            Assert.True(Directory.Exists(blockedPath));
            Assert.Equal(metadataBytes, await File.ReadAllBytesAsync(metadataPath));
            Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(
                Path.Combine(reopened.StateDirectory, "settings.json")));
            if (operation == "save")
            {
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalPath));
                Assert.False(File.Exists(RecoverySnapshotPath(reopened, draft.RecoveryId)));
            }
            else
            {
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupPath));
                var live = JsonSerializer.Deserialize<Iseberg.Core.RecoveredScript>(
                    await File.ReadAllTextAsync(RecoverySnapshotPath(reopened, draft.RecoveryId)))!;
                Assert.Equal(text, live.Text);
                Assert.Equal(draft.RecoveryId, live.Id);
                Assert.Equal(Environment.ProcessId, live.OwnerProcessId);
            }
        }
        finally
        {
            if (blockedPath is not null && Directory.Exists(blockedPath)) Directory.Delete(blockedPath);
            if (File.Exists(backupPath)) File.Move(backupPath, originalPath);
        }
        await reopened.DisposeAsync();
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalPath));
        Assert.Equal(metadataBytes, await File.ReadAllBytesAsync(metadataPath));
        using var reacquired = new Iseberg.Core.WorkbenchPersistenceLease(
            Path.Combine(reopened.StateDirectory, "settings.json"));
        var retained = Assert.Single(await new Iseberg.Core.ScriptRecovery(
            Path.Combine(reopened.StateDirectory, "settings.json.recovery")).ReadAsync());
        Assert.Equal(originalId, retained.Id);
        Assert.Equal(text, retained.Text);
        Assert.Equal(0, retained.OwnerProcessId);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RecoverRestoresSavedDocumentSelection(int selectedIndex)
    {
        await InitializeRuntimeAsync();
        await RecoverMetadataContractCoreAsync(selectedIndex, "selection");
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RecoverRestoresSavedCaretOffsetsForEveryRecoveredDocument(int selectedIndex)
    {
        await InitializeRuntimeAsync();
        await RecoverMetadataContractCoreAsync(selectedIndex, "caret");
    }

    [AvaloniaTheory]
    [InlineData("", 0, 0)]
    [InlineData("", 8, 0)]
    [InlineData("ab", 0, 0)]
    [InlineData("ab", 2, 2)]
    [InlineData("ab", 3, 2)]
    [InlineData("ab", 99, 2)]
    public async Task RecoverClampsStoredCaretToEmptyEndAndShortenedDraftBounds(
        string text, int savedOffset, int expectedOffset)
    {
        await InitializeRuntimeAsync();
        await RecoverCaretBoundaryContractCoreAsync(text, savedOffset, expectedOffset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecoverCaretBoundaryContractCoreAsync(string text, int savedOffset, int expectedOffset)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile, stateDirectory: Path.Combine(environment.DirectoryPath, "recovery-caret-bounds"));
        var id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var state = new Iseberg.Core.WorkbenchState
        {
            Sessions =
            [
                new()
                {
                    Name = "PowerShell 1",
                    Documents = [new()
                    {
                        RecoveryId = id, Name = "bounded.ps1", Encoding = new(65001, false), CaretOffset = savedOffset
                    }]
                }
            ]
        };
        await SeedRecoveryStateAsync(tab, state, new Dictionary<Guid, string> { [id] = text });
        await using var host = new IseTestHost(tab);
        await ChooseRecoveryAsync(host, tab, "Recover");
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var document = Assert.Single(session.Files);
        Assert.Equal("bounded.ps1", document.File.Name);
        Assert.Equal(text, document.Document.Text);
        Assert.True(document.File.IsDirty);
        Assert.Equal(new Iseberg.Core.ScriptEncoding(65001, false), document.File.EncodingChoice);
        Assert.Same(document, session.SelectedFile);
        Assert.Same(document.Document, tab.Workbench.ScriptEditorView.Document);
        Assert.False(File.Exists(RecoverySnapshotPath(tab, id)));
        Assert.Empty(host.Errors);
        Assert.Equal(expectedOffset, document.File.CaretOffset);
        Assert.Equal(expectedOffset, tab.Workbench.ScriptEditorView.CaretOffset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecoverMetadataContractCoreAsync(int selectedIndex, string observation)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile, stateDirectory: Path.Combine(environment.DirectoryPath, "recovery-metadata"));
        var (state, texts) = ThreeDocumentRecoveryFixture(selectedIndex);
        await SeedRecoveryStateAsync(tab, state, texts);
        await using var host = new IseTestHost(tab);
        await ChooseRecoveryAsync(host, tab, "Recover");

        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        Assert.Equal(3, session.Files.Count);
        Assert.Equal(new[] { "first.ps1", "last.ps1", "middle.ps1" },
            session.Files.Select(file => file.File.Name).Order(StringComparer.Ordinal).ToArray());
        foreach (var (name, text) in new[]
        {
            ("first.ps1", "first recovered text"), ("middle.ps1", "middle recovered text"), ("last.ps1", "last recovered text")
        })
        {
            var document = Assert.Single(session.Files, file => file.File.Name == name);
            Assert.Equal(text, document.Document.Text);
            Assert.True(document.File.IsDirty);
            Assert.Null(document.File.Path);
            Assert.Equal(new Iseberg.Core.ScriptEncoding(65001, true), document.File.EncodingChoice);
            var live = JsonSerializer.Deserialize<Iseberg.Core.RecoveredScript>(
                await File.ReadAllTextAsync(RecoverySnapshotPath(tab, document.RecoveryId)))!;
            Assert.Equal(document.RecoveryId, live.Id);
            Assert.Equal(text, live.Text);
            Assert.Equal(Environment.ProcessId, live.OwnerProcessId);
            Assert.Equal("PowerShell 1", live.SessionName);
        }
        Assert.All(texts.Keys, id => Assert.False(File.Exists(RecoverySnapshotPath(tab, id))));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(tab.StateDirectory, "settings.json.recovery"), "*.json").Length);
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        Assert.Empty(host.Errors);
        var originalOrder = new[] { "first.ps1", "middle.ps1", "last.ps1" };
        switch (observation)
        {
            case "order":
                Assert.Equal(originalOrder, session.Files.Select(file => file.File.Name).ToArray());
                break;
            case "selection":
                Assert.Equal(originalOrder[selectedIndex], session.SelectedFile!.File.Name);
                Assert.Same(session.SelectedFile.Document, tab.Workbench.ScriptEditorView.Document);
                break;
            case "caret":
                var orderedFiles = originalOrder.Select(name => Assert.Single(session.Files, file => file.File.Name == name)).ToArray();
                Assert.Equal(new[] { 1, 4, 8 }, orderedFiles.Select(file => file.File.CaretOffset).ToArray());
                Assert.Equal(new[] { 1, 4, 8 }[selectedIndex], tab.Workbench.ScriptEditorView.CaretOffset);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(observation));
        }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HostedRecoveryRejectsMultipleSessionsWithoutRewritingReleasedStorage(int selectedSession)
    {
        await InitializeRuntimeAsync();
        await UnsupportedHostedRecoveryContractCoreAsync(selectedSession);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task UnsupportedHostedRecoveryContractCoreAsync(int selectedSession)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile, stateDirectory: Path.Combine(environment.DirectoryPath, "recovery-sessions"));
        var firstId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var secondId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var state = new Iseberg.Core.WorkbenchState
        {
            SelectedSession = selectedSession,
            Sessions =
            [
                new()
                {
                    Name = "PowerShell 1",
                    Documents = [new() { RecoveryId = firstId, Name = "same.ps1", Encoding = new(65001, true) }]
                },
                new()
                {
                    Name = "PowerShell 2",
                    Documents = [new() { RecoveryId = secondId, Name = "same.ps1", Encoding = new(1200, true) }]
                }
            ]
        };
        await SeedRecoveryStateAsync(tab, state, new Dictionary<Guid, string>
        {
            [firstId] = "first session draft", [secondId] = "second session draft"
        });
        var before = Directory.GetFiles(tab.StateDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        await using var host = new IseTestHost(tab);
        host.Owner.Show();
        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => tab.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal("Open another DT Iseberg tab to create an independent PowerShell session.", error.Message);
        Assert.Empty(host.Owner.OwnedWindows);
        await tab.DisposeAsync();
        Assert.True(tab.IsDisposed);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal).ToArray(),
            Directory.GetFiles(tab.StateDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray());
        using var reacquired = new Iseberg.Core.WorkbenchPersistenceLease(Path.Combine(tab.StateDirectory, "settings.json"));
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RemovingRecoveredDocumentDeletesOnlyItsOwnedSnapshot(int removedIndex)
    {
        await InitializeRuntimeAsync();
        await RemoveRecoveredDocumentContractCoreAsync(removedIndex);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RemoveRecoveredDocumentContractCoreAsync(int removedIndex)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var root = Path.Combine(environment.DirectoryPath, "remove-recovered");
        var tab = new PowerShellIseTab(profile, stateDirectory: root);
        var (state, texts) = ThreeDocumentRecoveryFixture(1);
        await SeedRecoveryStateAsync(tab, state, texts);
        await using var host = new IseTestHost(tab);
        await ChooseRecoveryAsync(host, tab, "Recover");

        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var documents = session.Files.ToArray();
        var wrappers = tab.Workbench.Scripting.CurrentPowerShellTab.Files.ToArray();
        tab.Workbench.Scripting.CurrentPowerShellTab.Files.SetSelectedFile(wrappers[removedIndex]);
        Assert.Same(documents[removedIndex], session.SelectedFile);
        var snapshots = documents.ToDictionary(document => document.RecoveryId,
            document => File.ReadAllBytes(RecoverySnapshotPath(tab, document.RecoveryId)));
        var foreignDirectory = Path.Combine(root, "foreign-owned-fixture", "recovery");
        var foreignId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        await new Iseberg.Core.ScriptRecovery(foreignDirectory).SaveAsync(foreignId,
            Iseberg.Core.ScriptFile.FromRecovery("foreign.ps1", "untouched foreign draft"), "PowerShell 9", released: true);
        var foreignPath = Path.Combine(foreignDirectory, foreignId.ToString("N") + ".json");
        var foreignBytes = await File.ReadAllBytesAsync(foreignPath);

        tab.Workbench.Scripting.CurrentPowerShellTab.Files.Remove(wrappers[removedIndex], true);

        Assert.False(File.Exists(RecoverySnapshotPath(tab, documents[removedIndex].RecoveryId)));
        var survivors = documents.Where((_, index) => index != removedIndex).ToArray();
        Assert.Equal(survivors, session.Files.ToArray());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(tab.StateDirectory, "settings.json.recovery"), "*.json").Length);
        foreach (var document in survivors)
        {
            Assert.Equal(snapshots[document.RecoveryId],
                await File.ReadAllBytesAsync(RecoverySnapshotPath(tab, document.RecoveryId)));
            Assert.True(document.File.IsDirty);
        }
        Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(foreignPath));
        Assert.Contains(session.SelectedFile, session.Files);
        Assert.DoesNotContain(documents[removedIndex], session.Files);
        Assert.Throws<ObjectDisposedException>(() => wrappers[removedIndex].IsSaved);
        Assert.Same(engine, session.Engine);
        Assert.Equal(Iseberg.Core.SessionState.Ready, engine.State);
        Assert.Same(session.SelectedFile!.Document, tab.Workbench.ScriptEditorView.Document);
        Assert.Empty(host.Errors);
    }

    private static (Iseberg.Core.WorkbenchState State, Dictionary<Guid, string> Texts)
        ThreeDocumentRecoveryFixture(int selectedIndex)
    {
        var firstId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var middleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var lastId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        return (new Iseberg.Core.WorkbenchState
        {
            Sessions =
            [
                new()
                {
                    Name = "PowerShell 1", SelectedDocument = selectedIndex,
                    Documents =
                    [
                        new() { RecoveryId = firstId, Name = "first.ps1", CaretOffset = 1, Encoding = new(65001, true) },
                        new() { RecoveryId = middleId, Name = "middle.ps1", CaretOffset = 4, Encoding = new(65001, true) },
                        new() { RecoveryId = lastId, Name = "last.ps1", CaretOffset = 8, Encoding = new(65001, true) }
                    ]
                }
            ]
        }, new Dictionary<Guid, string>
        {
            [firstId] = "first recovered text", [middleId] = "middle recovered text", [lastId] = "last recovered text"
        });
    }

    private static async Task SeedRecoveryStateAsync(PowerShellIseTab tab, Iseberg.Core.WorkbenchState state,
        IReadOnlyDictionary<Guid, string> texts)
    {
        await DisableRecoveryAutosaveAsync(tab);
        var settingsPath = Path.Combine(tab.StateDirectory, "settings.json");
        await new Iseberg.Core.WorkbenchStateStore(settingsPath + ".workbench.json").SaveAsync(state);
        var recovery = new Iseberg.Core.ScriptRecovery(settingsPath + ".recovery");
        foreach (var session in state.Sessions)
            foreach (var document in session.Documents)
                await recovery.SaveAsync(document.RecoveryId,
                    Iseberg.Core.ScriptFile.FromRecovery(document.Name, texts[document.RecoveryId], document.Encoding),
                    session.Name, released: true);
    }

    private static string RecoverySnapshotPath(PowerShellIseTab tab, Guid id) =>
        Path.Combine(tab.StateDirectory, "settings.json.recovery", id.ToString("N") + ".json");

    private static async Task ChooseRecoveryAsync(IseTestHost host, PowerShellIseTab tab,
        string choice, Action? beforeChoice = null)
    {
        host.Owner.Show();
        var before = host.Owner.OwnedWindows.ToArray();
        var initialization = tab.InitializeAsync();
        await ChooseDialogAsync(host.Owner, before, initialization, choice, beforeChoice);
    }

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", "#012456", "#F5F5F5", "#FFFFFF", "#000000", 12d)]
    [InlineData("Light Console, Dark Editor", "#FFFFFF", "#626262", "#012456", "#F5F5F5", 12d)]
    [InlineData("Dark Console, Dark Editor", "#012456", "#F5F5F5", "#012456", "#F5F5F5", 12d)]
    [InlineData("Light Console, Light Editor", "#FFFFFF", "#626262", "#FFFFFF", "#000000", 12d)]
    [InlineData("Monochrome Green", "#000000", "#00FF00", "#000000", "#00FF00", 44d / 3)]
    [InlineData("Presentation", "#000000", "#F5F5F5", "#FFFFFF", "#000000", 80d / 3)]
    public async Task OriginalThemeResolverAndHostedEditorKeepFixedPalettesAndExactDipFontSize(
        string name, string consoleBackground, string consoleForeground,
        string scriptBackground, string scriptForeground, double dips)
    {
        await InitializeRuntimeAsync();
        await OriginalThemeResolutionCoreAsync(name, consoleBackground, consoleForeground, scriptBackground, scriptForeground, dips);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OriginalThemeResolutionCoreAsync(string name, string consoleBackground, string consoleForeground,
        string scriptBackground, string scriptForeground, double dips)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = name;
        profile.FontFace = "Consolas";
        profile.FontSize = 17;
        Assert.Equal(dips, Iseberg.Core.EditorThemePresets.FontSizePoints(name) * 4.0 / 3, precision: 10);
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        tab.Workbench.CreateDocument("$x = 42");
        tab.Workbench.ScriptEditorView.CaretOffset = 3;
        var engine = Assert.Single(tab.Workbench.Workbench.Sessions).Engine;
        foreach (var dark in new[] { false, true, false })
        {
            var resolved = PowerShellIseTab.ResolveTheme(name, dark);
            Assert.Equal(name, resolved.Name);
            Assert.Equal(consoleBackground, resolved.Colors["Console.Background"]);
            Assert.Equal(consoleForeground, resolved.Colors["Console.Foreground"]);
            Assert.Equal(scriptBackground, resolved.Colors["Script.Background"]);
            Assert.Equal(scriptForeground, resolved.Colors["Script.Foreground"]);
            host.Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            var editor = tab.Workbench.ScriptEditorView.TextEditor;
            Assert.Equal(Avalonia.Media.Color.Parse(scriptBackground), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(editor.Background).Color);
            Assert.Equal(Avalonia.Media.Color.Parse(scriptForeground), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(editor.Foreground).Color);
            Assert.Equal("Consolas", tab.Workbench.GetScriptingSettings().FontFamily);
            Assert.Equal(17, tab.Workbench.GetScriptingSettings().FontSize, precision: 10);
            Assert.Equal("Consolas", editor.FontFamily.Name);
            Assert.Equal(17, editor.FontSize, precision: 10);
            Assert.Equal(17 * .75, tab.Terminal.FontSize, precision: 10);
            Assert.Equal(name, tab.Workbench.GetScriptingSettings().ThemePreset);
            Assert.Equal("$x = 42", editor.Document.Text);
            Assert.Equal(3, tab.Workbench.ScriptEditorView.CaretOffset);
            Assert.Same(engine, Assert.Single(tab.Workbench.Workbench.Sessions).Engine);
        }
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData("classic ise", "#012456", "#FFFFFF", "#FFFFFF", "#000000")]
    [InlineData("dArK", "#1E1E1E", "#D4D4D4", "#1E1E1E", "#D4D4D4")]
    [InlineData("LIGHT", "#FFFFFF", "#202020", "#FFFFFF", "#000000")]
    public async Task LegacyThemeAliasCompatibilityMatrixUsesOriginalAliasAppearance(
        string name, string consoleBackground, string consoleForeground, string scriptBackground, string scriptForeground)
    {
        await InitializeRuntimeAsync();
        LegacyThemeResolutionCore(name, consoleBackground, consoleForeground, scriptBackground, scriptForeground);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LegacyThemeResolutionCore(
        string name, string consoleBackground, string consoleForeground, string scriptBackground, string scriptForeground)
    {
        foreach (var dark in new[] { false, true })
        {
            var theme = PowerShellIseTab.ResolveTheme(name, dark);
            Assert.Equal(consoleBackground, theme.Colors["Console.Background"]);
            Assert.Equal(consoleForeground, theme.Colors["Console.Foreground"]);
            Assert.Equal(scriptBackground, theme.Colors["Script.Background"]);
            Assert.Equal(scriptForeground, theme.Colors["Script.Foreground"]);
        }
    }

    [AvaloniaFact]
    public async Task FollowDtThemeAliasResolvesBothHostPalettesWithoutBecomingFixed()
    {
        await InitializeRuntimeAsync();
        FollowDtResolutionCore();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FollowDtResolutionCore()
    {
        var light = PowerShellIseTab.ResolveTheme("follow dt", false);
        var dark = PowerShellIseTab.ResolveTheme("Follow DT", true);
        Assert.Equal("Light", light.Name);
        Assert.Equal("#FFFFFF", light.Colors["Script.Background"]);
        Assert.Equal("#202020", light.Colors["Console.Foreground"]);
        Assert.Equal("Dark", dark.Name);
        Assert.Equal("#1E1E1E", dark.Colors["Script.Background"]);
        Assert.Equal("#D4D4D4", dark.Colors["Console.Foreground"]);
        Assert.Equal("#9CDCFE", dark.Colors["Script.Variable"]);
        Assert.Equal("#9A3412", light.Colors["Script.Variable"]);
        Assert.Throws<NotSupportedException>(() => PowerShellIseTab.ResolveTheme("Future Theme", true));
    }

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", 0xFFF5F5F5u, 0xFF012456u, "#000000", "#FFFFFF")]
    [InlineData("Light Console, Dark Editor", 0xFF626262u, 0xFFFFFFFFu, "#F5F5F5", "#012456")]
    [InlineData("Dark Console, Dark Editor", 0xFFF5F5F5u, 0xFF012456u, "#F5F5F5", "#012456")]
    [InlineData("Light Console, Light Editor", 0xFF626262u, 0xFFFFFFFFu, "#000000", "#FFFFFF")]
    [InlineData("Monochrome Green", 0xFF00FF00u, 0xFF000000u, "#00FF00", "#000000")]
    [InlineData("Presentation", 0xFFF5F5F5u, 0xFF000000u, "#000000", "#FFFFFF")]
    public async Task HostedOptionsThemeApplyIsDurableScopedAndCancelRetainsAcceptedPalette(
        string name, uint consoleForeground, uint consoleBackground, string scriptForeground, string scriptBackground)
    {
        await InitializeRuntimeAsync();
        await HostedThemeOptionsCoreAsync(name, consoleForeground, consoleBackground, scriptForeground, scriptBackground);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task HostedThemeOptionsCoreAsync(
        string name, uint consoleForeground, uint consoleBackground, string scriptForeground, string scriptBackground)
    {
        using var environment = new IseTestEnvironment();
        var root = Path.Combine(environment.DirectoryPath, "theme-options");
        var firstProfile = environment.Profile();
        firstProfile.IseColorTheme = "Dark Console, Light Editor (default)";
        var secondProfile = environment.Profile();
        secondProfile.IseColorTheme = "Light Console, Dark Editor";
        var first = new PowerShellIseTab(firstProfile, stateDirectory: root);
        var second = new PowerShellIseTab(secondProfile, stateDirectory: root);
        await using var host = new IseTestHost(first, second);
        host.Owner.RequestedThemeVariant = ThemeVariant.Light;
        await host.InitializeAsync();
        var firstEngine = first.Workbench.Workbench.SelectedSession!.Engine;
        var secondEngine = second.Workbench.Workbench.SelectedSession!.Engine;
        var optionsItem = Assert.Single(MenuItems(first.Workbench.FindControl<Menu>("WorkbenchMenu")!),
            menu => Equals(menu.Tag, "Options"));
        optionsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitForAsync(() => host.Owner.OwnedWindows.OfType<Iseberg.OptionsWindow>().Any(),
            () => "Hosted Options did not open.");
        var options = Assert.Single(host.Owner.OwnedWindows.OfType<Iseberg.OptionsWindow>());
        try
        {
            var baselineBytes = File.Exists(Path.Combine(first.StateDirectory, "settings.json"))
                ? await File.ReadAllBytesAsync(Path.Combine(first.StateDirectory, "settings.json")) : null;
            options.FindControl<Button>("ManageThemes")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => options.OwnedWindows.Any(), () => "Hosted theme manager did not open.");
            var manager = Assert.Single(options.OwnedWindows);
            manager.UpdateLayout();
            var list = Assert.Single(manager.GetVisualDescendants().OfType<ListBox>());
            Assert.Equal(new[]
            {
                "Dark Console, Light Editor (default)", "Light Console, Dark Editor", "Dark Console, Dark Editor",
                "Light Console, Light Editor", "Monochrome Green", "Presentation", "Classic ISE", "Dark", "Light", "Follow DT"
            }, list.Items.Cast<string>().ToArray());
            list.SelectedItem = name;
            Assert.Single(manager.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "OK"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => !manager.IsVisible, () => "Hosted theme selection did not finish.");
            Assert.Equal("Dark Console, Light Editor (default)", first.Workbench.GetScriptingSettings().ThemePreset);
            Assert.Equal(0xFF012456u, first.Terminal.Engine.Scheme.Background);
            Assert.Equal("#FFFFFF", first.Workbench.GetScriptingSettings().Theme.Colors["Script.Background"]);
            var settingsPath = Path.Combine(first.StateDirectory, "settings.json");
            if (baselineBytes is null) Assert.False(File.Exists(settingsPath));
            else Assert.Equal(baselineBytes, await File.ReadAllBytesAsync(settingsPath));
            // Changing another option makes Apply meaningful even when this row selects the initial theme.
            options.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = false;
            options.FindControl<Button>("ApplyOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => first.Workbench.GetScriptingSettings().ThemePreset == name &&
                                     !first.Workbench.GetScriptingSettings().ShowLineNumbers &&
                                     options.FindControl<Button>("CancelOptions")!.IsEnabled,
                () => "Hosted Options did not durably publish the accepted theme/preferences.");
            var acceptedBytes = await File.ReadAllBytesAsync(settingsPath);
            var stored = await Iseberg.Core.UserSettings.LoadAsync(settingsPath);
            Assert.Equal(name, stored.ThemePreset);
            Assert.False(stored.ShowLineNumbers);
            AssertPalette(first);
            options.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = true;
            options.FindControl<Button>("CancelOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(settingsPath));
            Assert.False(first.Workbench.ScriptEditorView.TextEditor.ShowLineNumbers);
            Assert.Equal("Light Console, Dark Editor", second.Workbench.GetScriptingSettings().ThemePreset);
            Assert.Equal(0xFF626262u, second.Terminal.Engine.Scheme.Foreground);
            Assert.Equal(0xFFFFFFFFu, second.Terminal.Engine.Scheme.Background);
            Assert.Equal(Avalonia.Media.Color.Parse("#012456"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(second.Workbench.ScriptEditorView.TextEditor.Background).Color);
            Assert.Equal(ThemeVariant.Light, host.Owner.ActualThemeVariant);
            Assert.Same(firstEngine, first.Workbench.Workbench.SelectedSession!.Engine);
            Assert.Same(secondEngine, second.Workbench.Workbench.SelectedSession!.Engine);
            Assert.True(await first.RequestCloseAsync());
            var reopened = new PowerShellIseTab(firstProfile, workspaceId: first.WorkspaceId, stateDirectory: root);
            await using var reopenedHost = new IseTestHost(reopened);
            await reopenedHost.InitializeAsync();
            AssertPalette(reopened, profileOwned: true);
            Assert.True(reopened.Workbench.ScriptEditorView.TextEditor.ShowLineNumbers);
            Assert.NotSame(firstEngine, reopened.Workbench.Workbench.SelectedSession!.Engine);
            Assert.Empty(reopenedHost.Errors);
        }
        finally
        {
            foreach (var owned in options.OwnedWindows.ToArray()) owned.Close();
            options.Close();
        }
        Assert.Empty(host.Errors);

        void AssertPalette(PowerShellIseTab tab, bool profileOwned = false)
        {
            Assert.Equal(profileOwned ? "Dark Console, Light Editor (default)" : name, tab.Workbench.GetScriptingSettings().ThemePreset);
            Assert.Equal(profileOwned ? 0xFFF5F5F5u : consoleForeground, tab.Terminal.Engine.Scheme.Foreground);
            Assert.Equal(profileOwned ? 0xFF012456u : consoleBackground, tab.Terminal.Engine.Scheme.Background);
            var editor = tab.Workbench.ScriptEditorView.TextEditor;
            Assert.Equal(Avalonia.Media.Color.Parse(profileOwned ? "#000000" : scriptForeground),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(editor.Foreground).Color);
            Assert.Equal(Avalonia.Media.Color.Parse(profileOwned ? "#FFFFFF" : scriptBackground),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(editor.Background).Color);
        }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppliedOptionsAndRecoverySurviveDirtyCloseCancelTogetherOnOriginalNativeEngine(bool prepareOnly)
    {
        await InitializeRuntimeAsync();
        await AppliedOptionsCancellationCoreAsync(prepareOnly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AppliedOptionsCancellationCoreAsync(bool prepareOnly)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var connection = NativeConnection(tab);
        var document = tab.Workbench.CreateDocument("$owned = 'cancel draft'");
        tab.Workbench.ScriptEditorView.CaretOffset = 7;
        Assert.Single(MenuItems(tab.Workbench.FindControl<Menu>("WorkbenchMenu")!), item => Equals(item.Tag, "Options"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitForAsync(() => host.Owner.OwnedWindows.OfType<Iseberg.OptionsWindow>().Any(), () => "Options missing.");
        var options = Assert.Single(host.Owner.OwnedWindows.OfType<Iseberg.OptionsWindow>());
        try
        {
            foreach (var name in new[] { "ShowOutlining", "ShowLineNumbers", "WarnDuplicates", "PromptToSave",
                "ConsoleIntelliSense", "ConsoleEnterSelects", "ScriptIntelliSense", "ScriptEnterSelects",
                "LocalHelp", "ShowToolbar", "DefaultSnippets", "CheckForUpdates" })
                options.FindControl<CheckBox>(name)!.IsChecked = false;
            options.FindControl<ComboBox>("EditorFontSize")!.SelectedItem = 18d;
            options.FindControl<ComboBox>("PanePosition")!.SelectedIndex = 1;
            options.FindControl<ComboBox>("CompletionTimeout")!.SelectedItem = 21;
            options.FindControl<TextBox>("AutoSaveInterval")!.Text = "0";
            options.FindControl<TextBox>("RecentFileCount")!.Text = "23";
            options.FindControl<Button>("ApplyOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => options.FindControl<Button>("CancelOptions")!.IsEnabled &&
                tab.Workbench.GetScriptingSettings().RecentFileCount == 23, () => "Accepted Options were not published.");
            options.FindControl<Button>("CancelOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { options.Close(); }
        var settingsPath = Path.Combine(tab.StateDirectory, "settings.json");
        var preferences = await File.ReadAllBytesAsync(settingsPath);
        var accepted = JsonSerializer.Serialize(tab.Workbench.GetScriptingSettings());
        await tab.Workbench.SaveRecoveryAsync();
        var recoveryPath = RecoverySnapshotPath(tab, document.RecoveryId);
        var recovery = await File.ReadAllBytesAsync(recoveryPath);
        var metadata = await File.ReadAllBytesAsync(settingsPath + ".workbench.json");
        var before = host.Owner.OwnedWindows.ToArray();
        var closing = prepareOnly ? tab.PrepareCloseAsync() : tab.RequestCloseAsync();
        await ChooseDialogAsync(host.Owner, before, closing, "Cancel");
        Assert.False(await closing);
        Assert.Equal(preferences, await File.ReadAllBytesAsync(settingsPath));
        Assert.Equal(recovery, await File.ReadAllBytesAsync(recoveryPath));
        Assert.Equal(metadata, await File.ReadAllBytesAsync(settingsPath + ".workbench.json"));
        Assert.Equal(accepted, JsonSerializer.Serialize(tab.Workbench.GetScriptingSettings()));
        Assert.False(tab.Workbench.ScriptEditorView.TextEditor.ShowLineNumbers);
        Assert.False(tab.Workbench.ScriptEditorView.TextEditor.WordWrap);
        Assert.Equal(18, tab.Workbench.ScriptEditorView.TextEditor.FontSize);
        Assert.Same(document, session.SelectedFile);
        Assert.True(document.File.IsDirty);
        Assert.Equal("$owned = 'cancel draft'", document.Document.Text);
        Assert.Equal(7, tab.Workbench.ScriptEditorView.CaretOffset);
        Assert.True(tab.Workbench.IsEnabled);
        Assert.False(tab.IsDisposed);
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(settingsPath));
        Assert.Same(engine, session.Engine);
        Assert.Same(connection, NativeConnection(tab));
        document.Document.Insert(document.Document.TextLength, "\n# still editable");
        await SubmitTokenAsync(tab, "'DT-OPTIONS-RECOVERY-CANCEL-USABLE'", "DT-OPTIONS-RECOVERY-CANCEL-USABLE");
        var afterExecution = await Iseberg.Core.UserSettings.LoadAsync(settingsPath);
        var debugger = Assert.Single(afterExecution.DebuggerSessions);
        Assert.Equal("PowerShell 1", debugger.Name);
        Assert.Empty(debugger.Watches);
        Assert.Empty(debugger.Breakpoints);
        // Executing refreshes and saves the debugger inventory; all accepted preference fields stay exact.
        afterExecution.DebuggerSessions.Clear();
        Assert.Equal(accepted, JsonSerializer.Serialize(afterExecution));
        Assert.Equal(recovery, await File.ReadAllBytesAsync(recoveryPath));
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData("settings")]
    [InlineData("workbench")]
    [InlineData("recovery")]
    public async Task FailedHostedInitializationReleasesLeaseImmediatelyWithoutWaitingForDispose(string corrupt)
    {
        await InitializeRuntimeAsync();
        await ImmediateFailureLeaseCoreAsync(corrupt);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ImmediateFailureLeaseCoreAsync(string corrupt)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile);
        var settingsPath = Path.Combine(tab.StateDirectory, "settings.json");
        await new Iseberg.Core.UserSettings { AutoSaveMinutes = 0 }.SaveAsync(settingsPath);
        var path = corrupt switch
        {
            "settings" => settingsPath,
            "workbench" => settingsPath + ".workbench.json",
            _ => Path.Combine(settingsPath + ".recovery", "ad4976254876453582211e5b22742982.json")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{owned malformed");
        var bytes = await File.ReadAllBytesAsync(path);
        await using var host = new IseTestHost(tab);
        host.Owner.Show();
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => tab.InitializeAsync());
        Assert.False(tab.Workbench.IsStarted);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(tab.Workbench.Workbench.Sessions); // Late recovery-read failure must also unwind its native session.
        // Failure must unwind ownership before callers explicitly dispose the failed control.
        using var reacquired = new Iseberg.Core.WorkbenchPersistenceLease(settingsPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [AvaloniaFact]
    public async Task HostedCommandsDescribeInsertRunAndLocalHelpStayOnSelectedNativeEngineAndLeaveOtherTabUntouched()
    {
        await InitializeRuntimeAsync();
        await HostedCommandsCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task HostedCommandsCoreAsync()
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile);
        var other = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab, other);
        await host.InitializeAsync();
        var workbench = tab.Workbench;
        var session = Assert.Single(workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var connection = NativeConnection(tab);
        var otherSession = Assert.Single(other.Workbench.Workbench.Sessions);
        var otherDocument = otherSession.SelectedFile!;
        await ExecuteTokenAsync(other, "$global:phase3Calls = 81; 'DT-OTHER-COMMANDS-BASE:81'", "DT-OTHER-COMMANDS-BASE:81");
        await workbench.ExecuteAsync("""
            $global:phase3Calls = 0
            function global:Invoke-Phase3Host {
                <#
                .SYNOPSIS
                Owned DT native Commands help, not a second engine.
                #>
                param([Parameter(Mandatory)][string]$Text)
                $global:phase3Calls++
                'DT-HOST-COMMAND:' + $global:phase3Calls + ':' + $Text
            }
            """);
        workbench.Scripting.CurrentPowerShellTab.ShowCommands = true;
        Assert.True(workbench.FindControl<Control>("CommandsPane")!.IsVisible);
        workbench.FindControl<Button>("CommandRefreshButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var list = workbench.FindControl<ListBox>("CommandList")!;
        await WaitForAsync(() => list.Items.Cast<Iseberg.Core.CommandDescription>().Any(c => c.Name == "Invoke-Phase3Host"),
            () => "Native host command not enumerated.");
        workbench.FindControl<TextBox>("CommandSearch")!.Text = "missing-phase3-command";
        await WaitForAsync(() => list.Items.Count == 0, () => "No-match command search did not refresh.");
        Assert.Empty(list.Items);
        workbench.FindControl<TextBox>("CommandSearch")!.Text = "invoke-PHASE3host";
        await WaitForAsync(() => list.Items.Count == 1, () => "Case-insensitive command search did not refresh.");
        var description = Assert.Single(list.Items.Cast<Iseberg.Core.CommandDescription>());
        list.SelectedItem = description;
        var view = workbench.FindControl<Iseberg.CommandFormView>("CommandForm")!;
        await WaitForAsync(() => view.Form?.Description.Name == "Invoke-Phase3Host", () => "Native command description missing.");
        view.UpdateLayout();
        await WaitForAsync(() => view.GetVisualDescendants().OfType<TextBox>().Any(box =>
            Avalonia.Automation.AutomationProperties.GetName(box) == "Text"), () => "Native parameter input not painted.");
        Assert.Equal("Invoke-Phase3Host", view.Form!.Description.InvocationName);
        Assert.False(workbench.FindControl<Button>("CommandRunButton")!.IsEnabled);
        Assert.False(workbench.FindControl<Button>("CommandInsertButton")!.IsEnabled);
        Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
            box => Avalonia.Automation.AutomationProperties.GetName(box) == "Text").Text = "café 'native'";
        await WaitForAsync(() => view.Result is { IsValid: true }, () => "Valid command form not enabled.");
        const string command = "& 'Invoke-Phase3Host' -Text 'café ''native'''";
        Assert.Equal(command, view.GetCommand());
        workbench.Scripting.CurrentEditor!.Clear();
        workbench.FindControl<Button>("CommandInsertButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(command + " ", session.SelectedFile!.Document.Text);
        workbench.FindControl<Button>("CommandRunButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForNativeOutputAsync(tab, "DT-HOST-COMMAND:1:café 'native'");
        await WaitForAsync(() => engine.State == Iseberg.Core.SessionState.Ready, () => "Native Commands run unfinished.");
        var before = host.Owner.OwnedWindows.ToArray();
        workbench.FindControl<Button>("CommandHelpButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => host.Owner.OwnedWindows.OfType<Iseberg.CommandHelpWindow>().Any(), () => "Local help missing.");
        var help = Assert.Single(host.Owner.OwnedWindows.OfType<Iseberg.CommandHelpWindow>());
        try
        {
            Assert.Contains("Owned DT native Commands help, not a second engine.",
                help.FindControl<AvaloniaEdit.TextEditor>("HelpContent")!.Text, StringComparison.Ordinal);
            Assert.Contains("Invoke-Phase3Host", help.Title, StringComparison.Ordinal);
            Assert.Same(engine, session.Engine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Same(connection, NativeConnection(tab));
            Assert.Equal(command + " ", session.SelectedFile.Document.Text);
            Assert.Same(otherDocument, otherSession.SelectedFile);
            Assert.Equal("", otherDocument.Document.Text);
            Assert.DoesNotContain(otherSession.Commands, c => c.Name == "Invoke-Phase3Host");
            Assert.False(other.Workbench.FindControl<Control>("CommandsPane")!.IsVisible);
            await ExecuteTokenAsync(other, "'DT-OTHER-COMMANDS-STILL:' + $global:phase3Calls", "DT-OTHER-COMMANDS-STILL:81");
        }
        finally { help.Close(); }
        Assert.Equal(before, host.Owner.OwnedWindows.ToArray());
        Assert.Equal(Path.Combine(environment.DirectoryPath, "PowerShellIse", "Snippets"), engine.Snippets.UserDirectory);
        await workbench.SaveSnippetsAsync([new("Owned Phase3 host snippet", "durable selected-engine catalog", "independent test author",
            "Write-Output 'DT-HOST-SNIPPET'", -1, true)]);
        Assert.Single(Directory.GetFiles(engine.Snippets.UserDirectory, "*.snippets.ps1xml"));
        Assert.Contains((await otherSession.Engine.Snippets.LoadAsync()).Snippets, snippet => snippet.Title == "Owned Phase3 host snippet");
        workbench.Scripting.CurrentEditor!.Clear();
        Assert.Single(MenuItems(workbench.FindControl<Menu>("WorkbenchMenu")!), item => Equals(item.Tag, "Snippets"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitForAsync(() => host.Owner.OwnedWindows.OfType<Iseberg.SnippetWindow>().Any(), () => "Hosted snippet picker missing.");
        var picker = Assert.Single(host.Owner.OwnedWindows.OfType<Iseberg.SnippetWindow>());
        try
        {
            picker.FindControl<TextBox>("SnippetSearch")!.Text = "Owned Phase3 host snippet";
            var snippets = picker.FindControl<ListBox>("SnippetList")!;
            await WaitForAsync(() => snippets.Items.Count == 1, () => "Owned catalog snippet not in actual picker.");
            Assert.Equal("Owned Phase3 host snippet", Assert.IsType<Iseberg.Core.PowerShellSnippet>(snippets.SelectedItem).Title);
            await WaitForAsync(() => picker.FindControl<TextBox>("SnippetPreview")!.Text == "Write-Output 'DT-HOST-SNIPPET'",
                () => "Snippet selection preview unfinished.");
            Assert.Equal("Write-Output 'DT-HOST-SNIPPET'", picker.FindControl<TextBox>("SnippetPreview")!.Text);
            picker.FindControl<Button>("InsertSnippet")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => !picker.IsVisible, () => "Hosted snippet insertion unfinished.");
            await WaitForAsync(() => session.SelectedFile.Document.Text == "Write-Output 'DT-HOST-SNIPPET'",
                () => "Hosted snippet picker result not delivered to editor.");
            Assert.Equal("Write-Output 'DT-HOST-SNIPPET'", session.SelectedFile.Document.Text);
            Assert.Same(engine, session.Engine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Equal("", otherDocument.Document.Text);
        }
        finally { picker.Close(); }
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task CancelRecoveryKeepsMixedSavedAndUntitledPlaceholdersSelectionCaretsAndEverySnapshotByte(int selected)
    {
        await InitializeRuntimeAsync();
        await MixedRecoveryCancelCoreAsync(selected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task MixedRecoveryCancelCoreAsync(int selected)
    {
        using var environment = new IseTestEnvironment();
        var saved = Path.Combine(environment.DirectoryPath, "saved mixed.ps1");
        await File.WriteAllTextAsync(saved, "saved disk content");
        var savedBytes = await File.ReadAllBytesAsync(saved);
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile);
        var (state, texts) = ThreeDocumentRecoveryFixture(selected);
        state.Sessions[0].Documents[0].Path = saved;
        await SeedRecoveryStateAsync(tab, state, texts);
        var directory = Path.Combine(tab.StateDirectory, "settings.json.recovery");
        var snapshots = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);
        await using var host = new IseTestHost(tab);
        host.Owner.Show();
        var request = tab.InitializeAsync();
        Iseberg.ScriptTab[]? placeholders = null;
        Iseberg.ScriptTab? selectedDocument = null;
        int caret = -1;
        await ChooseDialogAsync(host.Owner, [], request, "Cancel", () =>
        {
            var session = Assert.Single(tab.Workbench.Workbench.Sessions);
            placeholders = session.Files.ToArray();
            selectedDocument = session.SelectedFile;
            caret = tab.Workbench.ScriptEditorView.CaretOffset;
        });
        var restored = Assert.Single(tab.Workbench.Workbench.Sessions);
        Assert.Equal(placeholders, restored.Files.ToArray());
        Assert.Same(selectedDocument, restored.SelectedFile);
        Assert.Equal(caret, tab.Workbench.ScriptEditorView.CaretOffset);
        Assert.Equal(selected, Array.IndexOf(placeholders!, selectedDocument));
        Assert.Equal(new[] { "saved disk content", "", "" }, restored.Files.Select(file => file.Document.Text));
        Assert.All(restored.Files, file => Assert.False(file.File.IsDirty));
        Assert.Equal(saved, restored.Files[0].File.Path);
        Assert.All(snapshots, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
        Assert.Equal(savedBytes, await File.ReadAllBytesAsync(saved));
        Assert.Equal(3, Directory.GetFiles(directory, "*.json").Length);
        Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(Path.Combine(tab.StateDirectory, "settings.json")));
        await SubmitTokenAsync(tab, "'DT-MIXED-RECOVERY-CANCEL-USABLE'", "DT-MIXED-RECOVERY-CANCEL-USABLE");
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,696969,8B0000,006161,A82D00,0000FF")]
    [InlineData("Light Console, Dark Editor", "B0C4DE,E0FFFF,EE82EE,FFE4B5,98FB98,E0FFFF,E0FFFF,F5F5F5,FFE4C4,D3D3D3,DB7093,8FBC8F,FF4500,E0FFFF")]
    [InlineData("Dark Console, Dark Editor", "B0C4DE,E0FFFF,EE82EE,FFE4B5,98FB98,E0FFFF,E0FFFF,F5F5F5,FFE4C4,D3D3D3,DB7093,8FBC8F,FF4500,E0FFFF")]
    [InlineData("Light Console, Light Editor", "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,A9A9A9,8B0000,008080,FF4500,0000FF")]
    [InlineData("Monochrome Green", "009F00,00BF00,00DF00,00FF00,007F00,00DF00,00FF00,00DF00,009F00,007F00,00DF00,00FF00,00BF00,00BF00")]
    [InlineData("Presentation", "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,A9A9A9,8B0000,008080,FF4500,0000FF")]
    public async Task EveryOriginalPresetPaintsAllFourteenImplementedScriptTokenCategoriesOnHostedEditor(string theme, string palette)
    {
        await InitializeRuntimeAsync();
        await EveryHostedTokenCoreAsync(theme, palette);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task EveryHostedTokenCoreAsync(string theme, string palette)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = theme;
        profile.FontSize = 17;
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        const string text = "[CmdletBinding()] param([string]$arg)\n# comment\nfunction Phase3 { }\n:owned foreach ($x in 42) { Write-Output -InputObject 'str'; $x.Length + 1 }\nWrite-Output barearg";
        var document = tab.Workbench.CreateDocument(text);
        var editor = tab.Workbench.ScriptEditorView.TextEditor;
        tab.Workbench.ScriptEditorView.CaretOffset = 23;
        var markers = new[] { "CmdletBinding", "Write-Output", "barearg", "-InputObject", "# comment", "foreach", ":owned",
            "Length", "42", "+", "'str'", "string", "$arg", "Phase3" };
        var colors = palette.Split(',');
        foreach (var dark in new[] { false, true })
        {
            host.Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            host.Owner.UpdateLayout();
            await tab.Workbench.ScriptEditorView.AnalyzeAsync();
            editor.TextArea.TextView.Redraw();
            editor.TextArea.TextView.EnsureVisualLines();
            for (var index = 0; index < markers.Length; index++)
            {
                var offset = text.IndexOf(markers[index], StringComparison.Ordinal);
                var line = editor.TextArea.TextView.VisualLines.Single(line =>
                    line.FirstDocumentLine.Offset <= offset && line.LastDocumentLine.EndOffset >= offset);
                var element = line.Elements.Single(element => line.FirstDocumentLine.Offset + element.RelativeTextOffset <= offset &&
                    line.FirstDocumentLine.Offset + element.RelativeTextOffset + element.DocumentLength > offset);
                Assert.Equal(Avalonia.Media.Color.Parse("#" + colors[index]),
                    Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(element.TextRunProperties.ForegroundBrush).Color);
            }
            Assert.Equal(text, document.Document.Text);
            Assert.Equal(23, tab.Workbench.ScriptEditorView.CaretOffset);
            Assert.Equal(17, tab.Workbench.GetScriptingSettings().FontSize);
            Assert.Equal(theme, tab.Workbench.SelectedThemePreset);
        }
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)")]
    [InlineData("Light Console, Dark Editor")]
    [InlineData("Dark Console, Dark Editor")]
    [InlineData("Light Console, Light Editor")]
    [InlineData("Monochrome Green")]
    [InlineData("Presentation")]
    public async Task OriginalPresetsPreserveEveryProfileAnsiColorAndActuallyRenderAllSixteenIndicesWithoutExecution(string theme)
    {
        await InitializeRuntimeAsync();
        await EveryAnsiIndexCoreAsync(theme);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task EveryAnsiIndexCoreAsync(string theme)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = theme;
        profile.ColorScheme = "One Half Dark";
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var history = session.History.ToArray();
        var runspace = session.Engine.LocalRunspaceId;
        var expected = new uint[]
        {
            0xFF282C34, 0xFFE06C75, 0xFF98C379, 0xFFE5C07B, 0xFF61AFEF, 0xFFC678DD, 0xFF56B6C2, 0xFFDCDFE4,
            0xFF5A6374, 0xFFE06C75, 0xFF98C379, 0xFFE5C07B, 0xFF61AFEF, 0xFFC678DD, 0xFF56B6C2, 0xFFDCDFE4
        };
        foreach (var dark in new[] { false, true })
        {
            host.Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Assert.Equal(expected, tab.Terminal.Engine.Scheme.Table);
            NativeConnection(tab).Clear();
            var ansi = string.Concat(Enumerable.Range(0, 16).Select(index => $"\u001b[{(index < 8 ? 30 + index : 90 + index - 8)}m{(char)('A' + index)}"));
            NativeConnection(tab).WriteOutput(new Iseberg.Core.OutputEntry(ansi + "\u001b[0m\n"));
            await WaitForNativeOutputAsync(tab, "ABCDEFGHIJKLMNOP");
            var line = Assert.Single(tab.Terminal.Engine.CreateSnapshot(includeHistory: true).Buffer.Lines,
                line => string.Concat(line.Cells.Select(cell => cell.Text)).TrimEnd() == "ABCDEFGHIJKLMNOP");
            for (var index = 0; index < 16; index++)
            {
                Assert.Equal(Devolutions.Terminal.Core.TermColor.FromIndex(index), line.Cells[index].Attributes.Foreground);
                Assert.Equal(expected[index], tab.Terminal.Engine.Scheme.Resolve(index));
            }
            Assert.Equal(history, session.History.ToArray());
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);
            Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
        }
        Assert.Empty(host.Errors);
    }

    [AvaloniaTheory]
    [InlineData("Classic ISE", false, 0xFFFFFFFFu, 0xFF012456u, "#FFFFFF", "#000000")]
    [InlineData("Dark", false, 0xFFD4D4D4u, 0xFF1E1E1Eu, "#1E1E1E", "#D4D4D4")]
    [InlineData("Light", true, 0xFF202020u, 0xFFFFFFFFu, "#FFFFFF", "#000000")]
    [InlineData("Follow DT", false, 0xFF202020u, 0xFFFFFFFFu, "#FFFFFF", "#000000")]
    [InlineData("Follow DT", true, 0xFFD4D4D4u, 0xFF1E1E1Eu, "#1E1E1E", "#D4D4D4")]
    public async Task LegacyAliasesAcceptRealHostedOptionsAndReloadProfilePrecedenceWithoutLosingScopedPalette(
        string alias, bool darkOwner, uint foreground, uint background, string scriptBackground, string scriptForeground)
    {
        await InitializeRuntimeAsync();
        await AliasHostedOptionsCoreAsync(alias, darkOwner, foreground, background, scriptBackground, scriptForeground);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AliasHostedOptionsCoreAsync(string alias, bool darkOwner, uint foreground, uint background,
        string scriptBackground, string scriptForeground)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = alias;
        profile.IseAutoSaveMinutes = 0;
        profile.FontSize = 22;
        var tab = new PowerShellIseTab(profile);
        await using (var host = new IseTestHost(tab))
        {
            host.Owner.RequestedThemeVariant = darkOwner ? ThemeVariant.Dark : ThemeVariant.Light;
            await host.InitializeAsync();
            Assert.Single(MenuItems(tab.Workbench.FindControl<Menu>("WorkbenchMenu")!), item => Equals(item.Tag, "Options"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitForAsync(() => host.Owner.OwnedWindows.OfType<Iseberg.OptionsWindow>().Any(), () => "Alias Options missing.");
            var options = Assert.Single(host.Owner.OwnedWindows.OfType<Iseberg.OptionsWindow>());
            try
            {
                options.FindControl<Button>("ManageThemes")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => options.OwnedWindows.Any(), () => "Alias theme manager missing.");
                var manager = Assert.Single(options.OwnedWindows);
                manager.UpdateLayout();
                var list = Assert.Single(manager.GetVisualDescendants().OfType<ListBox>());
                Assert.Equal(alias, list.SelectedItem);
                list.SelectedItem = alias;
                Assert.Single(manager.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "OK"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => !manager.IsVisible, () => "Alias selection unfinished.");
                options.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = false;
                options.FindControl<Button>("ApplyOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => options.FindControl<Button>("CancelOptions")!.IsEnabled &&
                    !tab.Workbench.GetScriptingSettings().ShowLineNumbers, () => "Alias Options did not apply.");
                options.FindControl<Button>("CancelOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(alias, (await Iseberg.Core.UserSettings.LoadAsync(Path.Combine(tab.StateDirectory, "settings.json"))).ThemePreset);
                AssertPalette(tab);
                Assert.False(tab.Workbench.ScriptEditorView.TextEditor.ShowLineNumbers);
                Assert.Equal(22, tab.Workbench.ScriptEditorView.TextEditor.FontSize);
            }
            finally
            {
                foreach (var child in options.OwnedWindows.ToArray()) child.Close();
                options.Close();
            }
            Assert.Empty(host.Errors);
        }
        var reopened = new PowerShellIseTab(profile, workspaceId: tab.WorkspaceId);
        await using var reopenedHost = new IseTestHost(reopened);
        reopenedHost.Owner.RequestedThemeVariant = darkOwner ? ThemeVariant.Dark : ThemeVariant.Light;
        await reopenedHost.InitializeAsync();
        AssertPalette(reopened);
        Assert.True(reopened.Workbench.ScriptEditorView.TextEditor.ShowLineNumbers); // Documented profile ownership wins on reopen.
        Assert.Equal(22, reopened.Workbench.ScriptEditorView.TextEditor.FontSize);
        Assert.Empty(reopenedHost.Errors);

        void AssertPalette(PowerShellIseTab content)
        {
            Assert.Equal(alias, content.Workbench.SelectedThemePreset);
            Assert.Equal(foreground, content.Terminal.Engine.Scheme.Foreground);
            Assert.Equal(background, content.Terminal.Engine.Scheme.Background);
            Assert.Equal(Avalonia.Media.Color.Parse(scriptBackground),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(content.Workbench.ScriptEditorView.TextEditor.Background).Color);
            Assert.Equal(Avalonia.Media.Color.Parse(scriptForeground),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(content.Workbench.ScriptEditorView.TextEditor.Foreground).Color);
        }
    }

    [AvaloniaTheory]
    [InlineData("save")]
    [InlineData("remove")]
    public async Task MissingOwnedRecoveryDirectoryIsRecreatedForSaveAndHarmlessForPublicRemovalWithoutChangingBackup(string operation)
    {
        await InitializeRuntimeAsync();
        await MissingRecoveryDirectoryCoreAsync(operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task MissingRecoveryDirectoryCoreAsync(string operation)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseAutoSaveMinutes = 0;
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        await host.InitializeAsync();
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var document = tab.Workbench.CreateDocument("owned missing directory draft café");
        await tab.Workbench.SaveRecoveryAsync();
        var path = RecoverySnapshotPath(tab, document.RecoveryId);
        var original = await File.ReadAllBytesAsync(path);
        var directory = Path.GetDirectoryName(path)!;
        var backup = directory + ".owned-backup";
        Directory.Move(directory, backup);
        try
        {
            if (operation == "save")
            {
                await tab.Workbench.SaveRecoveryAsync();
                Assert.True(Directory.Exists(directory));
                Assert.Equal(new[] { path }, Directory.GetFiles(directory, "*.json"));
                var saved = JsonSerializer.Deserialize<Iseberg.Core.RecoveredScript>(await File.ReadAllTextAsync(path))!;
                Assert.Equal(document.RecoveryId, saved.Id);
                Assert.Equal("owned missing directory draft café", saved.Text);
                Assert.Equal(Environment.ProcessId, saved.OwnerProcessId);
                Assert.Same(document, session.SelectedFile);
                Assert.True(document.File.IsDirty);
            }
            else
            {
                tab.Workbench.Scripting.CurrentPowerShellTab.Files.Remove(tab.Workbench.Scripting.CurrentFile!, true);
                Assert.DoesNotContain(document, session.Files);
                Assert.False(Directory.Exists(directory));
                Assert.False(File.Exists(path));
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(backup, Path.GetFileName(path))));
            Assert.Same(engine, session.Engine);
            Assert.Equal(Iseberg.Core.SessionState.Ready, engine.State);
            Assert.Throws<IOException>(() => new Iseberg.Core.WorkbenchPersistenceLease(Path.Combine(tab.StateDirectory, "settings.json")));
            await SubmitTokenAsync(tab, "'DT-MISSING-RECOVERY-DIRECTORY-USABLE'", "DT-MISSING-RECOVERY-DIRECTORY-USABLE");
            Assert.Empty(host.Errors);
        }
        finally
        {
            if (!Directory.Exists(directory)) Directory.Move(backup, directory);
        }
    }
}
