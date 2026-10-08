using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Devolutions.Terminal.App.Connections;
using Devolutions.Terminal.App.Views;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.UI.Tests;

internal sealed class IseTestEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> previous = new();
    public string DirectoryPath { get; }
    public string ProfileId { get; } = Guid.NewGuid().ToString("B");

    public IseTestEnvironment()
    {
        var root = Environment.GetEnvironmentVariable("DTERM_ISE_TEST_ARTIFACTS") ?? Path.GetTempPath();
        DirectoryPath = Path.Combine(root, $"IseAcceptance.{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        foreach (var name in new[] { "DTERM_SETTINGS_PATH", "DT_SETTINGS_PATH", "WT_DOTNET_SETTINGS_PATH", "WT_BASE_SETTINGS_PATH" })
            previous[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable("DTERM_SETTINGS_PATH", Path.Combine(DirectoryPath, "settings.json"));
        Environment.SetEnvironmentVariable("DT_SETTINGS_PATH", null);
        Environment.SetEnvironmentVariable("WT_DOTNET_SETTINGS_PATH", null);
        Environment.SetEnvironmentVariable("WT_BASE_SETTINGS_PATH", DirectoryPath);
        Assert.Equal(Path.Combine(DirectoryPath, "settings.json"), SettingsService.SettingsPath);
        Assert.Equal(DirectoryPath, SettingsService.SettingsDirectory);
    }

    public ProfileSettings Profile(bool loadProfiles = false)
    {
        var profile = ProfileSettings.CreatePowerShellIse();
        profile.Guid = ProfileId;
        profile.StartingDirectory = DirectoryPath;
        profile.IseLoadProfiles = loadProfiles;
        return profile;
    }

    public void SaveDefaultProfile(string colorTheme = IsebergThemes.Classic)
    {
        var settings = SettingsLoader.Load("""{"profiles":{"list":[]}}""",
            """{"firstWindowPreference":"defaultProfile","confirmOnClose":"never","profiles":{"list":[]}}""");
        var profile = Profile();
        profile.IseColorTheme = colorTheme;
        settings.Profiles.Add(profile);
        settings.DefaultProfile = ProfileId;
        SettingsService.Save(settings);
        Assert.True(File.Exists(SettingsService.SettingsPath));
        Assert.Equal(ProfileKind.PowerShellIse,
            SettingsLoader.Load(SettingsLoader.ReadEmbeddedDefaults(), File.ReadAllText(SettingsService.SettingsPath))
                .Profiles.Single(p => Guid.Parse(p.Guid!) == Guid.Parse(ProfileId)).Kind);
    }

    public void Dispose()
    {
        foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal static class PowerShellIseTestHarness
{
    public static async Task InitializeRuntimeAsync()
    {
        await PowerShellIseRuntime.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    public static async Task WaitForAsync(Func<bool> condition, Func<string> diagnostic)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException(diagnostic());
            // Yield to dispatcher work, not wall-clock timing or an arbitrary sleep.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    public static async Task<Window> NewDialogAsync(Window owner, IReadOnlyCollection<Window> before, Task request)
    {
        await WaitForAsync(
            () => owner.OwnedWindows.Any(w => !before.Contains(w)) || request.IsCompleted,
            () => $"No new owned dialog. Owner visible={owner.IsVisible}; request status={request.Status}; owned={string.Join(", ", owner.OwnedWindows.Select(w => w.Title))}");
        Assert.False(request.IsCompleted,
            $"Request completed before its owned dialog appeared. Owner visible={owner.IsVisible}; request status={request.Status}.");
        return Assert.Single(owner.OwnedWindows, w => !before.Contains(w));
    }

    public static void ClickCancel(Window dialog)
    {
        dialog.UpdateLayout();
        var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Cancel"));
        Assert.True(button.IsVisible);
        Assert.True(button.IsEnabled);
        var position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog);
        Assert.NotNull(position);
        // A real pointer press/release, rather than invoking the handler or accepting the default.
        dialog.MouseDown(position.Value, MouseButton.Left);
        dialog.MouseUp(position.Value, MouseButton.Left);
    }

    public static async Task ChooseDialogAsync(Window owner, IReadOnlyCollection<Window> before,
        Task request, string choice, Action? beforeChoice = null)
    {
        await WaitForAsync(() => owner.OwnedWindows.Any(window => !before.Contains(window)) || request.IsCompleted,
            () => $"No owned choice dialog; request={request.Status}.");
        if (request.IsCompleted) await request;
        var dialog = await NewDialogAsync(owner, before, request);
        try
        {
            beforeChoice?.Invoke();
            dialog.UpdateLayout();
            var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(),
                candidate => Equals(candidate.Content, choice));
            Assert.True(button.IsVisible);
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
        }
        await request.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task ExecuteTokenAsync(PowerShellIseTab tab, string script, string token)
    {
        await tab.Workbench.ExecuteAsync(script).WaitAsync(TimeSpan.FromSeconds(60));
        var session = tab.Workbench.Workbench.SelectedSession!;
        await WaitForNativeOutputAsync(tab, token);
        Assert.Equal(Iseberg.Core.SessionState.Ready, session.Engine.State);
    }

    public static string[] NativeLines(PowerShellIseTab tab) =>
        tab.Terminal.Engine.CreateSnapshot(includeHistory: true).Buffer.Lines
            .Select(line => string.Concat(line.Cells.Select(cell => cell.Text)).TrimEnd()).ToArray();

    public static string NativeText(PowerShellIseTab tab) => string.Join("\n", NativeLines(tab));

    public static async Task WaitForNativeOutputAsync(PowerShellIseTab tab, string token)
    {
        // A standalone VT line proves output, not an echo of the command source.
        await WaitForAsync(() => NativeLines(tab).Contains(token, StringComparer.Ordinal),
            () => $"Native output line '{token}' missing:\n{NativeText(tab)}");
        Assert.Contains(token, NativeLines(tab));
    }

    public static IsebergTerminalConnection NativeConnection(PowerShellIseTab tab)
    {
        var factory = tab.Terminal.ConnectionFactory;
        var profile = tab.Terminal.Profile;
        Assert.NotNull(factory);
        Assert.NotNull(profile);
        return Assert.IsType<IsebergTerminalConnection>(factory(profile));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task SubmitTokenAsync(PowerShellIseTab tab, string script, string token)
    {
        var session = tab.Workbench.Workbench.SelectedSession!;
        var connection = NativeConnection(tab);
        await WaitForAsync(() => connection.IsInputEnabled && session.Engine.State == Iseberg.Core.SessionState.Ready,
            () => $"Native console not ready: input={connection.IsInputEnabled}, state={session.Engine.State}.");
        tab.Terminal.WriteInput(script + "\r");
        await WaitForNativeOutputAsync(tab, token);
        await WaitForAsync(() => connection.IsInputEnabled && session.Engine.State == Iseberg.Core.SessionState.Ready,
            () => $"Native command did not finish: input={connection.IsInputEnabled}, state={session.Engine.State}.");
        Assert.Same(session, Assert.Single(tab.Workbench.Workbench.Sessions));
    }

    public static async Task AssertPhase3CompletionScopeAsync(PowerShellIseTab tab, bool dark)
    {
        var editor = tab.Workbench.ScriptEditorView;
        var session = Assert.Single(tab.Workbench.Workbench.Sessions);
        var engine = session.Engine;
        var referenceList = new ListBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 1 };
        var reference = new Window
        {
            Width = 300, Height = 200, Content = referenceList,
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light
        };
        var document = tab.Workbench.CreateDocument("Get-Phase3");
        editor.CaretOffset = document.Document.TextLength;
        editor.CompletionProvider = new Phase3CompletionProvider();
        try
        {
            reference.Show();
            reference.UpdateLayout();
            editor.FocusEditor();
            await editor.ShowCompletionAsync();
            var completion = editor.CompletionPopup!;
            Assert.NotNull(completion);
            var popup = Assert.Single(Avalonia.LogicalTree.LogicalExtensions.GetLogicalChildren(completion)
                .OfType<Avalonia.Controls.Primitives.Popup>());
            await WaitForAsync(() => completion.IsOpen && popup.IsOpen, () => "Scoped completion and description missing.");
            completion.CompletionList.SelectedItem = completion.CompletionList.CompletionData[1];
            await WaitForAsync(() => (popup.Child as ContentControl)?.Content as string == "Independent second description",
                () => "Second selected completion description not updated.");
            var list = completion.CompletionList.ListBox;
            TopLevel.GetTopLevel(list)!.UpdateLayout();
            var expectedVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            Assert.Equal(expectedVariant, list.ActualThemeVariant);
            Assert.Equal(expectedVariant, popup.Child!.ActualThemeVariant);
            Assert.Equal("Get-Phase3Second", completion.CompletionList.SelectedItem!.Text);
            static uint BrushColor(Avalonia.Media.IBrush? brush)
            {
                var color = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(brush).Color;
                return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            }
            Assert.Equal(BrushColor(referenceList.Background), BrushColor(list.Background));
            Assert.Equal(BrushColor(referenceList.Foreground), BrushColor(list.Foreground));
            var selected = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            var expectedSelected = Assert.IsType<ListBoxItem>(referenceList.ContainerFromIndex(1));
            Assert.True(selected.IsSelected);
            Assert.Equal(dark ? 0xFFF0F0F0u : 0xFF000000u, BrushColor(selected.Foreground));
            var selectedPresenters = selected.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Where(presenter => presenter.Background is not null).ToArray();
            var referencePresenters = expectedSelected.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Where(presenter => presenter.Background is not null).ToArray();
            Assert.NotEmpty(selectedPresenters);
            Assert.NotEmpty(referencePresenters);
            Assert.Equal(referencePresenters.Select(presenter => BrushColor(presenter.Background)),
                selectedPresenters.Select(presenter => BrushColor(presenter.Background)));
            var description = Assert.IsAssignableFrom<ContentControl>(popup.Child);
            Assert.Equal(dark ? 0xFF252526u : 0xFFF7F7F7u, BrushColor(description.Background));
            Assert.Equal(dark ? 0xFFF0F0F0u : 0xFF202020u, BrushColor(description.Foreground));
            Assert.Equal("Get-Phase3", document.Document.Text);
            Assert.Same(engine, session.Engine);
            editor.CloseCompletion();
            Assert.False(completion.IsOpen);
            Assert.False(popup.IsOpen);
            Assert.Null(editor.CompletionPopup);
        }
        finally
        {
            editor.CloseCompletion();
            editor.CompletionProvider = null;
            reference.Close();
            // Remove only this test-owned dirty sample without a file-picker prompt.
            tab.Workbench.Scripting.CurrentPowerShellTab.Files.Remove(tab.Workbench.Scripting.CurrentFile!, true);
        }
    }

    private sealed class Phase3CompletionProvider : Iseberg.Editor.IEditorCompletionProvider
    {
        public Task<Iseberg.Editor.EditorCompletionList> CompleteAsync(Iseberg.Editor.EditorCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Iseberg.Editor.EditorCompletionList(request.Version,
                new Iseberg.Editor.EditorTextSpan(0, request.Text.Length),
                [new("Get-Phase3First", "Get-Phase3First", "Independent first description"),
                 new("Get-Phase3Second", "Get-Phase3Second", "Independent second description")]));
        }
    }
}

internal sealed class IseTestHost : IAsyncDisposable
{
    public Window Owner { get; }
    public PowerShellIseTab[] Tabs { get; }
    public List<string> Errors { get; } = [];

    public IseTestHost(params PowerShellIseTab[] tabs)
    {
        Tabs = tabs;
        foreach (var tab in tabs)
            tab.Workbench.ErrorOccurred += (_, error) => Errors.Add($"{error.Title}: {error.Exception}");
        var panel = new Grid();
        for (var index = 0; index < tabs.Length; index++)
        {
            panel.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetRow(tabs[index], index);
            panel.Children.Add(tabs[index]);
        }
        Owner = new Window { Width = 1100, Height = 800, Content = panel };
    }

    public async Task InitializeAsync()
    {
        Owner.Show();
        foreach (var tab in Tabs)
        {
            try { await tab.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Workbench initialization failed. Reported errors: {string.Join("\n", Errors)}", error);
            }
        }
        Owner.UpdateLayout();
        foreach (var tab in Tabs)
        {
            var connection = PowerShellIseTestHarness.NativeConnection(tab);
            await PowerShellIseTestHarness.WaitForAsync(() =>
            {
                var buffer = tab.Terminal.Engine.CreateSnapshot().Buffer;
                return connection.Columns == buffer.Columns && connection.Rows == buffer.Rows;
            }, () => $"Native terminal resize not applied: {connection.Columns}x{connection.Rows}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var tab in Tabs) await tab.DisposeAsync();
        }
        finally
        {
            Owner.Content = null;
            Owner.Close();
        }
    }
}
