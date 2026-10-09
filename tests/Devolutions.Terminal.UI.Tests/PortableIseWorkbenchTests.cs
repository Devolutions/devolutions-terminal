using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Iseberg;
using Iseberg.Core;
using Xunit;
using static Devolutions.Terminal.UI.Tests.PowerShellIseTestHarness;

namespace Devolutions.Terminal.UI.Tests;

public sealed class PortableIseWorkbenchTests
{
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RecoverRoutesSameNamedDocumentsToSavedSessionsAndRestoresActiveSession(int selectedSession)
    {
        await InitializeRuntimeAsync();
        await RecoverSessionRoutingCoreAsync(selectedSession);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecoverSessionRoutingCoreAsync(int selectedSession)
    {
        using var environment = new IseTestEnvironment();
        var root = Path.Combine(environment.DirectoryPath, "module-recovery-sessions");
        var settingsPath = Path.Combine(root, "settings.json");
        await new UserSettings
        {
            AutoSaveMinutes = 0, LoadProfiles = false, CheckForUpdates = false, ShowCommands = false
        }.SaveAsync(settingsPath);
        var firstId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var secondId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var state = new WorkbenchState
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
        await new WorkbenchStateStore(settingsPath + ".workbench.json").SaveAsync(state);
        var recoveryDirectory = settingsPath + ".recovery";
        var recovery = new ScriptRecovery(recoveryDirectory);
        await recovery.SaveAsync(firstId,
            ScriptFile.FromRecovery("same.ps1", "first session draft", new(65001, true)), "PowerShell 1", released: true);
        await recovery.SaveAsync(secondId,
            ScriptFile.FromRecovery("same.ps1", "second session draft", new(1200, true)), "PowerShell 2", released: true);
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            EnablePersistence = true, SettingsPath = settingsPath,
            SnippetDirectory = Path.Combine(root, "snippets"), StartingDirectory = environment.DirectoryPath
        });
        var errors = new List<Exception>();
        workbench.ErrorOccurred += (_, error) => errors.Add(error.Exception);
        var owner = new Window { Width = 1100, Height = 800, Content = workbench };
        try
        {
            owner.Show();
            var before = owner.OwnedWindows.ToArray();
            var initialization = workbench.InitializeAsync();
            await ChooseDialogAsync(owner, before, initialization, "Recover");

            var sessions = workbench.Workbench.Sessions.ToArray();
            Assert.Equal(new[] { "PowerShell 1", "PowerShell 2" }, sessions.Select(session => session.Name).ToArray());
            Assert.NotSame(sessions[0].Engine, sessions[1].Engine);
            for (var index = 0; index < 2; index++)
            {
                var document = Assert.Single(sessions[index].Files);
                var expectedText = new[] { "first session draft", "second session draft" }[index];
                var expectedEncoding = new ScriptEncoding(index == 0 ? 65001 : 1200, true);
                Assert.Equal("same.ps1", document.File.Name);
                Assert.Equal(expectedText, document.Document.Text);
                Assert.Equal(expectedEncoding, document.File.EncodingChoice);
                Assert.True(document.File.IsDirty);
                Assert.Same(document, sessions[index].SelectedFile);
                Assert.Equal(SessionState.Ready, sessions[index].Engine.State);
                var live = JsonSerializer.Deserialize<RecoveredScript>(await File.ReadAllTextAsync(
                    Path.Combine(recoveryDirectory, document.RecoveryId.ToString("N") + ".json")))!;
                Assert.Equal(sessions[index].Name, live.SessionName);
                Assert.Equal(expectedText, live.Text);
                Assert.Equal(expectedEncoding, live.Encoding);
                Assert.Equal(Environment.ProcessId, live.OwnerProcessId);
            }
            Assert.False(File.Exists(Path.Combine(recoveryDirectory, firstId.ToString("N") + ".json")));
            Assert.False(File.Exists(Path.Combine(recoveryDirectory, secondId.ToString("N") + ".json")));
            Assert.Equal(2, Directory.GetFiles(recoveryDirectory, "*.json").Length);
            Assert.Empty(errors);
            Assert.Equal(new[] { "PowerShell 1", "PowerShell 2" }[selectedSession],
                workbench.Workbench.SelectedSession!.Name);
            Assert.Same(sessions[selectedSession], workbench.Workbench.SelectedSession);
            Assert.Same(sessions[selectedSession].SelectedFile!.Document, workbench.ScriptEditorView.Document);
        }
        finally
        {
            try { await workbench.DisposeAsync(); }
            finally { owner.Content = null; owner.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task DisposalReleasesCachedScriptingFilesAndEditorsWhileRetainingModels()
    {
        await InitializeRuntimeAsync();
        await CachedScriptingDisposalCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CachedScriptingDisposalCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var model = Assert.Single(workbench.Workbench.Sessions);
        var document = Assert.Single(model.Files);
        var wrappers = CaptureScriptingWrapperReferences(workbench);
        Assert.True(wrappers.File.IsAlive);
        Assert.True(wrappers.Editor.IsAlive);
        await workbench.DisposeAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(wrappers.File.IsAlive);
        Assert.False(wrappers.Editor.IsAlive);
        Assert.Same(model, Assert.Single(workbench.Workbench.Sessions));
        Assert.Same(document, Assert.Single(model.Files));
        Assert.Equal(SessionState.Disposed, model.Engine.State);
        GC.KeepAlive(workbench);
        GC.KeepAlive(model);
        GC.KeepAlive(document);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference File, WeakReference Editor) CaptureScriptingWrapperReferences(WorkbenchControl workbench)
    {
        var file = workbench.Scripting.CurrentFile!;
        return (new WeakReference(file), new WeakReference(file.Editor));
    }

    [AvaloniaFact]
    public async Task DisposalDetachesRetainedScriptingModelsWithoutExplicitSessionRemoval()
    {
        await InitializeRuntimeAsync();
        await RetainedScriptingDisposalCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task RetainedScriptingDisposalCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var root = workbench.Scripting;
        var tab = root.CurrentPowerShellTab;
        var file = root.CurrentFile!;
        var editor = file.Editor;
        var model = Assert.Single(workbench.Workbench.Sessions);
        var document = Assert.Single(model.Files);
        var notifications = new List<string>();
        root.PropertyChanged += (_, args) => notifications.Add("root:" + args.PropertyName);
        root.Options.PropertyChanged += (_, args) => notifications.Add("options:" + args.PropertyName);
        root.PowerShellTabs.CollectionChanged += (_, _) => notifications.Add("tabs");
        tab.PropertyChanged += (_, args) => notifications.Add("tab:" + args.PropertyName);
        tab.Files.PropertyChanged += (_, args) => notifications.Add("files:" + args.PropertyName);
        tab.Files.CollectionChanged += (_, _) => notifications.Add("files");
        file.PropertyChanged += (_, args) => notifications.Add("file:" + args.PropertyName);
        editor.PropertyChanged += (_, args) => notifications.Add("editor:" + args.PropertyName);

        editor.Text = "before disposal";
        root.Options.ShowLineNumbers = false;
        await workbench.DrainScriptingSettingsPersistenceAsync();
        Assert.Contains("editor:Text", notifications);
        Assert.Contains("file:IsSaved", notifications);
        Assert.Contains("options:", notifications);

        await workbench.DisposeAsync();
        Assert.Same(model, Assert.Single(workbench.Workbench.Sessions));
        Assert.Equal(SessionState.Disposed, model.Engine.State);
        notifications.Clear();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            document.Document.Text = "late document " + cycle;
            document.File.SetEncoding(new ScriptEncoding(1200, true));
            var late = new ScriptTab(ScriptFile.FromRecovery("late.ps1", "late text"));
            model.Files.Add(late);
            model.SelectedFile = late;
            model.Files.Remove(late);
            model.SelectedFile = document;
            workbench.Workbench.SelectedSession = null;
            workbench.Workbench.Sessions.Remove(model);
            workbench.Workbench.Sessions.Add(model);
            workbench.Workbench.SelectedSession = model;
            workbench.FindControl<TextBlock>("StatusText")!.Text = "late status " + cycle;
            workbench.FindControl<Slider>("ZoomSlider")!.Value = 175 + cycle;
            workbench.FindControl<Control>("CommandsPane")!.IsVisible = cycle == 0;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.Empty(notifications);
        }

        Assert.Throws<ObjectDisposedException>(() => root.CurrentPowerShellTab);
        Assert.Throws<ObjectDisposedException>(() => root.CurrentFile);
        Assert.Throws<ObjectDisposedException>(() => root.PowerShellTabs.Count);
        Assert.Throws<ObjectDisposedException>(() => root.Options.FontSize);
        Assert.Throws<ObjectDisposedException>(() => root.Options.ShowLineNumbers = true);
        Assert.Throws<ObjectDisposedException>(() => tab.DisplayName);
        Assert.Throws<ObjectDisposedException>(() => tab.DisplayName = "must not revive");
        Assert.Throws<ObjectDisposedException>(() => tab.Files.Count);
        Assert.Throws<ObjectDisposedException>(() => file.DisplayName);
        Assert.Throws<ObjectDisposedException>(() => file.IsSaved);
        Assert.Throws<ObjectDisposedException>(() => editor.Text);
        Assert.Throws<ObjectDisposedException>(() => editor.Text = "must not revive");
        Assert.Empty(notifications);
    });

    [AvaloniaFact]
    public async Task DisposalInvalidatesAlreadyQueuedScriptingCallbacksWithoutResubscribing()
    {
        await InitializeRuntimeAsync();
        await QueuedScriptingDisposalCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task QueuedScriptingDisposalCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var root = workbench.Scripting;
        var tab = root.CurrentPowerShellTab;
        var file = root.CurrentFile!;
        var model = Assert.Single(workbench.Workbench.Sessions);
        var document = Assert.Single(model.Files);
        var notifications = new List<string?>();
        file.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        document.File.SetEncoding(new ScriptEncoding(1200, true));
        Assert.Contains("Encoding", notifications);
        notifications.Clear();
        // Keep the dispatcher occupied until the worker has queued its callback.
        Task.Run(() => document.File.SetEncoding(new ScriptEncoding(65001, false)))
            .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        Assert.Empty(notifications);
        await workbench.DisposeAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Empty(notifications);

        document.File.SetEncoding(new ScriptEncoding(1200, true));
        model.SelectedFile = document;
        workbench.Workbench.SelectedSession = model;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Empty(notifications);
        Assert.Throws<ObjectDisposedException>(() => file.Encoding);
        Assert.Throws<ObjectDisposedException>(() => tab.SelectedFile);
        Assert.Throws<ObjectDisposedException>(() => root.CurrentEditor);
        Assert.Same(model, Assert.Single(workbench.Workbench.Sessions));
    });

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", false)]
    [InlineData("Dark Console, Light Editor (default)", true)]
    [InlineData("Light Console, Dark Editor", false)]
    [InlineData("Light Console, Dark Editor", true)]
    [InlineData("Dark Console, Dark Editor", false)]
    [InlineData("Dark Console, Dark Editor", true)]
    [InlineData("Light Console, Light Editor", false)]
    [InlineData("Light Console, Light Editor", true)]
    [InlineData("Monochrome Green", false)]
    [InlineData("Monochrome Green", true)]
    [InlineData("Presentation", false)]
    [InlineData("Presentation", true)]
    public async Task RestoreDefaultsPreservesEveryOwnedThemeColorPresetAndLoadProfiles(
        string preset, bool loadProfiles)
    {
        await InitializeRuntimeAsync();
        await OwnedDefaultsPreservationCoreAsync(preset, loadProfiles);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OwnedDefaultsPreservationCoreAsync(string preset, bool loadProfiles)
    {
        var theme = EditorThemePresets.Original(preset);
        theme.Name = "Owned palette";
        theme.Colors["Script.Variable"] = "#A1B2C3";
        theme.Colors["Console.Background"] = "#010203";
        var palette = theme.Colors.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            CreateInitialSession = false,
            Preferences = new UserSettings
            {
                ThemePreset = preset, Theme = theme, LoadProfiles = loadProfiles,
                FontFamily = "Consolas", FontSize = 18, Zoom = 175, ShowLineNumbers = false,
                ShowCommands = true, CheckForUpdates = false, AutoSaveMinutes = 0
            }
        });
        var persisted = new List<UserSettings>();
        try
        {
            workbench.ScriptingSettingsPersistence = (snapshot, _) =>
            {
                persisted.Add(snapshot.Copy());
                return Task.CompletedTask;
            };
            workbench.Scripting.Options.RestoreDefaults();
            await workbench.DrainScriptingSettingsPersistenceAsync();
            var actual = workbench.GetScriptingSettings();
            var saved = Assert.Single(persisted);
            foreach (var snapshot in new[] { actual, saved })
            {
                Assert.Equal(preset, snapshot.ThemePreset);
                Assert.Equal(loadProfiles, snapshot.LoadProfiles);
                Assert.Equal("Owned palette", snapshot.Theme.Name);
                Assert.Equal(palette, snapshot.Theme.Colors.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
                Assert.Equal("#A1B2C3", snapshot.Theme.Colors["Script.Variable"]);
                Assert.Equal("#010203", snapshot.Theme.Colors["Console.Background"]);
                Assert.Equal("Lucida Console", snapshot.FontFamily);
                Assert.Equal(9, snapshot.FontSize);
                Assert.Equal(100, snapshot.Zoom);
                Assert.True(snapshot.ShowLineNumbers);
                Assert.True(snapshot.ShowCommands);
                Assert.False(snapshot.CheckForUpdates);
            }
            Assert.Empty(workbench.Workbench.Sessions);
        }
        finally { await workbench.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData("settings.json")]
    [InlineData("settings.json.workbench.json")]
    [InlineData("settings.json.recovery")]
    public async Task FailedInitializationAndDisposalPreserveCorruptSettingsStateAndRecovery(string target)
    {
        await InitializeRuntimeAsync();
        await FailedInitializationPreservationCoreAsync(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FailedInitializationPreservationCoreAsync(string target)
    {
        using var environment = new IseTestEnvironment();
        var root = Path.Combine(environment.DirectoryPath, "failed-startup");
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        await new UserSettings { AutoSaveMinutes = 0, LoadProfiles = false, CheckForUpdates = false }.SaveAsync(settingsPath);
        await new WorkbenchStateStore(settingsPath + ".workbench.json").SaveAsync(new());
        var snapshot = Path.Combine(settingsPath + ".recovery", "44444444444444444444444444444444.json");
        Directory.CreateDirectory(settingsPath + ".recovery");
        await File.WriteAllTextAsync(snapshot, JsonSerializer.Serialize(new RecoveredScript(
            Guid.Parse("44444444-4444-4444-4444-444444444444"), "draft.ps1", null, "preserve me")));
        var corrupt = target == "settings.json.recovery" ? snapshot : Path.Combine(root, target);
        await File.WriteAllTextAsync(corrupt, "{broken");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            EnablePersistence = true, SettingsPath = settingsPath,
            StartingDirectory = environment.DirectoryPath,
            Preferences = new UserSettings { AutoSaveMinutes = 0, LoadProfiles = false, CheckForUpdates = false }
        });
        var owner = new Window { Content = workbench };
        try
        {
            owner.Show();
            var initialization = workbench.InitializeAsync();
            await Assert.ThrowsAsync<JsonException>(() => initialization);
            Assert.False(workbench.IsStarted);
            Assert.Same(initialization, workbench.InitializeAsync());
            await workbench.DisposeAsync();
            Assert.True(workbench.IsDisposed);
            foreach (var (path, bytes) in before) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal("{broken", await File.ReadAllTextAsync(corrupt));
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal),
                Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Where(path => path != settingsPath + ".lock").Order(StringComparer.Ordinal));
            using var reacquired = new WorkbenchPersistenceLease(settingsPath);
        }
        finally
        {
            try { await workbench.DisposeAsync(); }
            finally { owner.Content = null; owner.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task SaveSnippetsUsesConfiguredDirectoryAndSameEngineCatalog()
    {
        await InitializeRuntimeAsync();
        await SaveSnippetsContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SaveSnippetsContractCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        Assert.Equal(Path.Combine(root, "catalog"), engine.Snippets.UserDirectory);
        await workbench.SaveSnippetsAsync(
        [
            new("Owned & <snippet>", "Description & detail", "Test author", "Write-Output '<&>'\r\n$x", 13, false),
            new("Second", "Another description", "Other author", "$y = 2\n$y", -1, true)
        ]);
        var path = Assert.Single(engine.Snippets.GetUserFiles()).FullName;
        Assert.Equal(Path.Combine(root, "catalog"), Path.GetDirectoryName(path));
        Assert.EndsWith(".snippets.ps1xml", path, StringComparison.Ordinal);
        const string expectedXml = """
            <Snippets xmlns="http://schemas.microsoft.com/PowerShell/Snippets">
              <Snippet Version="1.0.0">
                <Header>
                  <Title>Owned &amp; &lt;snippet&gt;</Title>
                  <Description>Description &amp; detail</Description>
                  <Author>Test author</Author>
                </Header>
                <Code><Script Language="PowerShell" Indent="false" CaretOffset="13">Write-Output '&lt;&amp;&gt;'&#xD;
            $x</Script></Code>
              </Snippet>
              <Snippet Version="1.0.0">
                <Header>
                  <Title>Second</Title>
                  <Description>Another description</Description>
                  <Author>Other author</Author>
                </Header>
                <Code><Script Language="PowerShell" Indent="true">$y = 2
            $y</Script></Code>
              </Snippet>
            </Snippets>
            """;
        Assert.True(XNode.DeepEquals(XDocument.Parse(expectedXml), XDocument.Parse(await File.ReadAllTextAsync(path))));
        var reloaded = await engine.Snippets.LoadAsync();
        Assert.Empty(reloaded.Errors);
        Assert.Equal(new PowerShellSnippet[]
        {
            new("Owned & <snippet>", "Description & detail", "Test author", "Write-Output '<&>'\r\n$x", 13, false),
            new("Second", "Another description", "Other author", "$y = 2\n$y", -1, true)
        }, reloaded.Snippets);
        Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
        Assert.Equal(runspace, engine.LocalRunspaceId);
        Assert.False(File.Exists(Path.Combine(root, "settings.json")));
    });

    [AvaloniaTheory]
    [InlineData(" ", 0)]
    [InlineData("Invalid caret", -2)]
    [InlineData("Invalid caret", 6)]
    public async Task SaveSnippetsRejectsInvalidBatchWithoutPartialFilesOrCatalog(string title, int caret)
    {
        await InitializeRuntimeAsync();
        await InvalidSnippetBatchContractCoreAsync(title, caret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task InvalidSnippetBatchContractCoreAsync(string title, int caret) =>
        WithModuleWorkbenchAsync(false, async (workbench, _) =>
        {
            var service = Assert.Single(workbench.Workbench.Sessions).Engine.Snippets;
            await workbench.SaveSnippetsAsync([new("Kept", "baseline", "author", "kept", 2, false)]);
            var original = Assert.Single(service.GetUserFiles()).FullName;
            var bytes = await File.ReadAllBytesAsync(original);
            await Assert.ThrowsAsync<InvalidDataException>(() => workbench.SaveSnippetsAsync(
                [new("Valid first", "not published", "author", "first"), new(title, "bad", "author", "12345", caret)]));
            Assert.Equal(new[] { original }, service.GetUserFiles().Select(file => file.FullName).ToArray());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(original));
            var catalog = await service.LoadAsync();
            Assert.Empty(catalog.Errors);
            Assert.Equal(new PowerShellSnippet[] { new("Kept", "baseline", "author", "kept", 2, false) }, catalog.Snippets);
        });

    [AvaloniaFact]
    public async Task SaveSnippetsRejectsNullAndEmptyWithoutWriting()
    {
        await InitializeRuntimeAsync();
        await EmptySnippetBatchContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task EmptySnippetBatchContractCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        Assert.Equal("snippets", (await Assert.ThrowsAsync<ArgumentNullException>(
            () => workbench.SaveSnippetsAsync(null!))).ParamName);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => workbench.SaveSnippetsAsync([]));
        Assert.Equal("snippets", error.ParamName);
        Assert.False(Directory.Exists(Path.Combine(root, "catalog")));
        Assert.Empty((await Assert.Single(workbench.Workbench.Sessions).Engine.Snippets.LoadAsync()).Snippets);
    });

    [AvaloniaFact]
    public async Task ExplicitRecoverySavesExactDocumentsSelectionCaretAndReleasedOwnership()
    {
        await InitializeRuntimeAsync();
        await ExplicitRecoveryContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ExplicitRecoveryContractCoreAsync() => WithModuleWorkbenchAsync(true, async (workbench, root) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        var initial = Assert.Single(session.Files);
        var first = workbench.CreateDocument("first\r\ncafé");
        first.File.SetEncoding(new ScriptEncoding(1200, true));
        workbench.ScriptEditorView.CaretOffset = 3;
        var second = workbench.CreateDocument("second\nline");
        workbench.ScriptEditorView.CaretOffset = 4;
        await workbench.SaveRecoveryAsync();
        var settingsPath = Path.Combine(root, "settings.json");
        Assert.Throws<IOException>(() => new WorkbenchPersistenceLease(settingsPath));
        var recoveryDirectory = settingsPath + ".recovery";
        var firstPath = Path.Combine(recoveryDirectory, first.RecoveryId.ToString("N") + ".json");
        var secondPath = Path.Combine(recoveryDirectory, second.RecoveryId.ToString("N") + ".json");
        Assert.Equal(new[] { firstPath, secondPath }.Order(StringComparer.Ordinal),
            Directory.GetFiles(recoveryDirectory).Order(StringComparer.Ordinal));
        var liveFirst = JsonSerializer.Deserialize<RecoveredScript>(await File.ReadAllTextAsync(firstPath))!;
        Assert.Equal("Untitled2.ps1", liveFirst.Name);
        Assert.Equal(first.RecoveryId, liveFirst.Id);
        Assert.Equal("first\r\ncafé", liveFirst.Text);
        Assert.Equal(new ScriptEncoding(1200, true, "UTF-16 LE"), liveFirst.Encoding);
        Assert.Null(liveFirst.Path);
        Assert.Equal("PowerShell 1", liveFirst.SessionName);
        Assert.Equal(Environment.ProcessId, liveFirst.OwnerProcessId);
        var liveSecond = JsonSerializer.Deserialize<RecoveredScript>(await File.ReadAllTextAsync(secondPath))!;
        Assert.Equal(second.RecoveryId, liveSecond.Id);
        Assert.Equal("Untitled3.ps1", liveSecond.Name);
        Assert.Equal("second\nline", liveSecond.Text);
        Assert.Equal(new ScriptEncoding(65001, false, "UTF-8"), liveSecond.Encoding);
        Assert.Equal(Environment.ProcessId, liveSecond.OwnerProcessId);
        Assert.Empty(await new ScriptRecovery(recoveryDirectory).ReadAsync());
        var metadataPath = settingsPath + ".workbench.json";
        var metadata = JsonSerializer.Deserialize<WorkbenchState>(await File.ReadAllTextAsync(metadataPath))!;
        Assert.Equal(1, metadata.Version);
        Assert.Equal(Environment.ProcessId, metadata.OwnerProcessId);
        Assert.Equal(0, metadata.SelectedSession);
        var savedSession = Assert.Single(metadata.Sessions);
        Assert.Equal("PowerShell 1", savedSession.Name);
        Assert.Equal(2, savedSession.SelectedDocument);
        Assert.Equal(new[] { "Untitled1.ps1", "Untitled2.ps1", "Untitled3.ps1" },
            savedSession.Documents.Select(doc => doc.Name).ToArray());
        Assert.Equal(new[] { initial.RecoveryId, first.RecoveryId, second.RecoveryId },
            savedSession.Documents.Select(doc => doc.RecoveryId).ToArray());
        Assert.Equal(new[] { 0, 3, 4 }, savedSession.Documents.Select(doc => doc.CaretOffset).ToArray());
        Assert.Equal(new ScriptEncoding[] { new(65001, false), new(1200, true), new(65001, false) },
            savedSession.Documents.Select(doc => doc.Encoding).ToArray());
        Assert.All(savedSession.Documents, doc => Assert.Null(doc.Path));

        await workbench.DisposeAsync();
        await workbench.DisposeAsync();
        Assert.Equal(SessionState.Disposed, session.Engine.State);
        using (var reacquired = new WorkbenchPersistenceLease(settingsPath))
        {
            var releasedMetadata = await new WorkbenchStateStore(metadataPath).LoadAsync();
            Assert.NotNull(releasedMetadata);
            Assert.Equal(0, releasedMetadata.OwnerProcessId);
            Assert.Equal(new[] { 0, 3, 4 }, Assert.Single(releasedMetadata.Sessions).Documents.Select(doc => doc.CaretOffset).ToArray());
            var released = await new ScriptRecovery(recoveryDirectory).ReadAsync();
            Assert.Equal(new[] { "first\r\ncafé", "second\nline" }.Order(StringComparer.Ordinal),
                released.Select(doc => doc.Text).Order(StringComparer.Ordinal));
            Assert.All(released, doc => Assert.Equal(0, doc.OwnerProcessId));
        }
    });

    [AvaloniaFact]
    public async Task RecoveryDisabledRejectsExplicitSaveWithoutFilesOrDocumentChanges()
    {
        await InitializeRuntimeAsync();
        await DisabledRecoveryContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task DisabledRecoveryContractCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var document = workbench.CreateDocument("dirty draft");
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => workbench.SaveRecoveryAsync());
        Assert.Equal("Recovery is disabled by the host.", error.Message);
        Assert.Same(document, workbench.Workbench.SelectedSession!.SelectedFile);
        Assert.Equal("dirty draft", document.Document.Text);
        Assert.True(document.File.IsDirty);
        Assert.False(Directory.Exists(root));
    });

    [AvaloniaFact]
    public async Task ApprovedModuleCloseReleasesOnlyItsOwnLease()
    {
        await InitializeRuntimeAsync();
        await ApprovedCloseLeaseContractCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ApprovedCloseLeaseContractCoreAsync() => WithModuleWorkbenchAsync(true, async (workbench, root) =>
    {
        var own = Path.Combine(root, "settings.json");
        var other = Path.Combine(root, "other.json");
        using var unrelated = new WorkbenchPersistenceLease(other);
        var session = Assert.Single(workbench.Workbench.Sessions);
        await workbench.SaveRecoveryAsync();
        Assert.True(await workbench.RequestCloseAsync());
        Assert.True(workbench.IsDisposed);
        Assert.Equal(SessionState.Disposed, session.Engine.State);
        using var reacquired = new WorkbenchPersistenceLease(own);
        Assert.Throws<IOException>(() => new WorkbenchPersistenceLease(other));
        Assert.Empty(await new ScriptRecovery(own + ".recovery").ReadAsync());
        var state = await new WorkbenchStateStore(own + ".workbench.json").LoadAsync();
        Assert.NotNull(state);
        Assert.Equal(0, state.OwnerProcessId);
        Assert.Equal("Untitled1.ps1", Assert.Single(Assert.Single(state.Sessions).Documents).Name);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task WithModuleWorkbenchAsync(bool persistence, Func<WorkbenchControl, string, Task> test)
    {
        using var environment = new IseTestEnvironment();
        var root = Path.Combine(environment.DirectoryPath, "module");
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            EnablePersistence = persistence,
            SettingsPath = persistence ? Path.Combine(root, "settings.json") : null,
            SnippetDirectory = Path.Combine(root, "catalog"),
            StartingDirectory = environment.DirectoryPath,
            Preferences = new UserSettings { AutoSaveMinutes = 0, LoadProfiles = false, CheckForUpdates = false, ShowCommands = false }
        });
        var errors = new List<Exception>();
        workbench.ErrorOccurred += (_, error) => errors.Add(error.Exception);
        var owner = new Window { Width = 1100, Height = 800, Content = workbench };
        try
        {
            owner.Show();
            await workbench.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await test(workbench, root);
            Assert.Empty(errors);
        }
        finally
        {
            try { await workbench.DisposeAsync(); }
            finally { owner.Content = null; owner.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task PortableOptionsBooleanInventoryWritesRealSettingsAndEditorProperties()
    {
        await InitializeRuntimeAsync();
        await OptionsBooleanInventoryCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OptionsBooleanInventoryCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var options = workbench.Scripting.Options;
        var cases = new (Action<bool> Write, Func<bool> Read, Func<UserSettings, bool> Stored, bool Default)[]
        {
            (v => options.ShowLineNumbers = v, () => options.ShowLineNumbers, s => s.ShowLineNumbers, true),
            (v => options.ShowOutlining = v, () => options.ShowOutlining, s => s.ShowOutlining, true),
            (v => options.WordWrap = v, () => options.WordWrap, s => s.WordWrap, false),
            (v => options.ShowToolBar = v, () => options.ShowToolBar, s => s.ShowToolbar, true),
            (v => options.ShowWarningBeforeSavingOnRun = v, () => options.ShowWarningBeforeSavingOnRun, s => s.PromptToSaveBeforeRun, true),
            (v => options.ShowWarningForDuplicateFiles = v, () => options.ShowWarningForDuplicateFiles, s => s.WarnDuplicateFiles, true),
            (v => options.ShowDefaultSnippets = v, () => options.ShowDefaultSnippets, s => s.UseDefaultSnippets, true),
            (v => options.ShowIntellisenseInScriptPane = v, () => options.ShowIntellisenseInScriptPane, s => s.ScriptIntelliSense, true),
            (v => options.ShowIntellisenseInConsolePane = v, () => options.ShowIntellisenseInConsolePane, s => s.ConsoleIntelliSense, true),
            (v => options.UseEnterToSelectInScriptPaneIntellisense = v, () => options.UseEnterToSelectInScriptPaneIntellisense, s => s.ScriptCompletionOnEnter, true),
            (v => options.UseEnterToSelectInConsolePaneIntellisense = v, () => options.UseEnterToSelectInConsolePaneIntellisense, s => s.ConsoleCompletionOnEnter, true),
            (v => options.UseLocalHelp = v, () => options.UseLocalHelp, s => s.UseLocalHelp, true),
            (v => options.FixedWidthFontsOnly = v, () => options.FixedWidthFontsOnly, s => s.FixedWidthFontsOnly, false)
        };
        foreach (var (write, read, stored, initial) in cases)
        {
            Assert.Equal(initial, read());
            Assert.Equal(initial, stored(workbench.GetScriptingSettings()));
            write(!initial);
            Assert.Equal(!initial, read());
            Assert.Equal(!initial, stored(workbench.GetScriptingSettings()));
            write(initial);
            Assert.Equal(initial, read());
            Assert.Equal(initial, stored(workbench.GetScriptingSettings()));
        }
        options.ShowLineNumbers = false;
        options.WordWrap = true;
        options.UseEnterToSelectInScriptPaneIntellisense = false;
        Assert.False(workbench.ScriptEditorView.TextEditor.ShowLineNumbers);
        Assert.True(workbench.ScriptEditorView.TextEditor.WordWrap);
        Assert.False(workbench.ScriptEditorView.CompletionAcceptsEnter);
        await workbench.DrainScriptingSettingsPersistenceAsync();
    });

    [AvaloniaTheory]
    [InlineData("FontSize", 6d, 18d, 72d, 5.999d, 72.001d)]
    [InlineData("Zoom", 20d, 175d, 400d, 19.999d, 400.001d)]
    [InlineData("IntellisenseTimeoutInSeconds", 1d, 7d, 30d, 0d, 31d)]
    [InlineData("AutoSaveMinuteInterval", 0d, 13d, 120d, -1d, 121d)]
    [InlineData("MruCount", 0d, 17d, 100d, -1d, 101d)]
    public async Task PortableOptionsRangeInventoryIncludesBothBoundariesAndAdjacentRejections(
        string property, double minimum, double interior, double maximum, double below, double above)
    {
        await InitializeRuntimeAsync();
        await OptionsRangeInventoryCoreAsync(property, minimum, interior, maximum, below, above);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OptionsRangeInventoryCoreAsync(
        string property, double minimum, double interior, double maximum, double below, double above) =>
        WithModuleWorkbenchAsync(false, async (workbench, _) =>
        {
            var options = workbench.Scripting.Options;
            Action<double> write;
            Func<double> read;
            Func<UserSettings, double> stored;
            switch (property)
            {
                case "FontSize":
                    write = value => options.FontSize = value;
                    read = () => options.FontSize;
                    stored = s => s.FontSize;
                    break;
                case "Zoom":
                    write = value => options.Zoom = value;
                    read = () => options.Zoom;
                    stored = s => s.Zoom;
                    break;
                case "IntellisenseTimeoutInSeconds":
                    write = value => options.IntellisenseTimeoutInSeconds = (int)value;
                    read = () => options.IntellisenseTimeoutInSeconds;
                    stored = s => s.IntelliSenseTimeoutSeconds;
                    break;
                case "AutoSaveMinuteInterval":
                    write = value => options.AutoSaveMinuteInterval = (int)value;
                    read = () => options.AutoSaveMinuteInterval;
                    stored = s => s.AutoSaveMinutes;
                    break;
                default:
                    write = value => options.MruCount = (int)value;
                    read = () => options.MruCount;
                    stored = s => s.RecentFileCount;
                    break;
            }
            foreach (var accepted in new[] { minimum, maximum, interior })
            {
                write(accepted);
                Assert.Equal(accepted, read());
                Assert.Equal(accepted, stored(workbench.GetScriptingSettings()));
            }
            foreach (var invalid in new[] { below, above })
            {
                Assert.Equal(property, Assert.Throws<ArgumentOutOfRangeException>(() => write(invalid)).ParamName);
                Assert.Equal(interior, read());
                Assert.Equal(interior, stored(workbench.GetScriptingSettings()));
            }
            if (property is "FontSize" or "Zoom")
                foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    Assert.Equal(property, Assert.Throws<ArgumentOutOfRangeException>(() => write(invalid)).ParamName);
                    Assert.Equal(interior, stored(workbench.GetScriptingSettings()));
                }
            if (property == "FontSize") Assert.Equal(24, workbench.ScriptEditorView.TextEditor.FontSize);
            if (property == "Zoom")
                Assert.Equal(175, workbench.FindControl<Avalonia.Controls.Primitives.RangeBase>("ZoomSlider")!.Value);
            await workbench.DrainScriptingSettingsPersistenceAsync();
        });

    [AvaloniaFact]
    public async Task PortableOptionsFontAndPaneLayoutValidateWithoutPublishingRejectedChanges()
    {
        await InitializeRuntimeAsync();
        await OptionsFontAndLayoutCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OptionsFontAndLayoutCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var options = workbench.Scripting.Options;
        Assert.Equal("Lucida Console", options.FontName);
        var notifications = new List<string?>();
        options.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        options.FontName = "Consolas";
        Assert.Equal("Consolas", workbench.GetScriptingSettings().FontFamily);
        foreach (var invalid in new[] { "", " ", "\t" })
        {
            var count = notifications.Count;
            Assert.Throws<ArgumentException>(() => options.FontName = invalid);
            Assert.Equal("Consolas", options.FontName);
            Assert.Equal(count, notifications.Count);
        }
        Assert.Throws<ArgumentNullException>(() => options.FontName = null!);
        foreach (var layout in new[] { "Right", "Maximized", "Top" })
        {
            options.SelectedScriptPaneState = layout;
            Assert.Equal(layout, options.SelectedScriptPaneState);
            Assert.Equal(layout, workbench.GetScriptingSettings().Layout);
            Assert.True(workbench.FindControl<Control>("ScriptPane")!.IsVisible);
        }
        foreach (var invalid in new[] { "", "top", "Left", " " })
        {
            var count = notifications.Count;
            Assert.Equal("SelectedScriptPaneState", Assert.Throws<ArgumentException>(
                () => options.SelectedScriptPaneState = invalid).ParamName);
            Assert.Equal("Top", workbench.GetScriptingSettings().Layout);
            Assert.Equal(count, notifications.Count);
        }
        await workbench.DrainScriptingSettingsPersistenceAsync();
    });

    [AvaloniaFact]
    public async Task PortableOptionsHostOwnedThemeProfileAndTokenColorMembersThrowExactErrors()
    {
        await InitializeRuntimeAsync();
        await OptionsHostOwnershipCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OptionsHostOwnershipCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var options = workbench.Scripting.Options;
        var before = System.Text.Json.JsonSerializer.Serialize(workbench.GetScriptingSettings());
        Assert.Equal("Profile loading is owned by DT's PowerShell ISE profile.",
            Assert.Throws<NotSupportedException>(() => options.LoadProfiles = true).Message);
        Assert.Equal("Choose the theme through the host's settings UI.",
            Assert.Throws<NotSupportedException>(() => options.Theme = new EditorTheme()).Message);
        foreach (var read in new Func<object>[] { () => options.TokenColors, () => options.ConsoleTokenColors, () => options.XmlTokenColors })
            Assert.Equal("ISE token-color dictionaries are not exposed; choose colors through the host's theme settings.",
                Assert.Throws<NotSupportedException>(() => read()).Message);
        foreach (var write in new Action[]
        {
            () => options.TokenColors = new object(), () => options.ConsoleTokenColors = new object(),
            () => options.XmlTokenColors = new object(), options.RestoreDefaultTokenColors,
            options.RestoreDefaultConsoleTokenColors, options.RestoreDefaultXmlTokenColors
        })
            Assert.Equal("Token colors are owned by the host's theme settings.",
                Assert.Throws<NotSupportedException>(write).Message);
        var detachedTheme = options.Theme;
        detachedTheme.Colors["Script.Foreground"] = "#123456";
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(workbench.GetScriptingSettings()));
        await workbench.DrainScriptingSettingsPersistenceAsync();
    });

    [AvaloniaFact]
    public async Task ScriptingSnapshotsAreIsolatedAndPersistenceSerializesAcceptedWritesInOrder()
    {
        await InitializeRuntimeAsync();
        await ScriptingPersistenceQueueCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ScriptingPersistenceQueueCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var snapshots = new List<UserSettings>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workbench.ScriptingSettingsPersistence = async (snapshot, cancellation) =>
        {
            Assert.False(cancellation.IsCancellationRequested);
            snapshots.Add(snapshot);
            if (snapshots.Count == 1) await release.Task;
        };
        workbench.Scripting.Options.Zoom = 160;
        workbench.Scripting.Options.WordWrap = true;
        try
        {
            Assert.Equal(160, workbench.GetScriptingSettings().Zoom);
            Assert.True(workbench.GetScriptingSettings().WordWrap);
            var first = Assert.Single(snapshots);
            Assert.Equal(160, first.Zoom);
            Assert.False(first.WordWrap);
            var drain = workbench.DrainScriptingSettingsPersistenceAsync();
            Assert.False(drain.IsCompleted);
            first.Theme.Colors["Script.Foreground"] = "#123456";
            first.FontFamily = "Callback mutation";
            var copy = workbench.GetScriptingSettings();
            copy.Theme.Colors["Script.Foreground"] = "#654321";
            copy.RecentFiles.Add("not live.ps1");
            Assert.Equal("#000000", workbench.GetScriptingSettings().Theme.Colors["Script.Foreground"]);
            Assert.Equal("Lucida Console", workbench.GetScriptingSettings().FontFamily);
            Assert.Empty(workbench.GetScriptingSettings().RecentFiles);
        }
        finally { release.TrySetResult(); }
        await workbench.DrainScriptingSettingsPersistenceAsync();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(160, snapshots[1].Zoom);
        Assert.True(snapshots[1].WordWrap);
        Assert.Equal("Lucida Console", snapshots[1].FontFamily);
        Assert.Equal("#000000", snapshots[1].Theme.Colors["Script.Foreground"]);
        Assert.True(workbench.DrainScriptingSettingsPersistenceAsync().IsCompletedSuccessfully);
    });

    [AvaloniaFact]
    public async Task FilesSelectedFileTracksEmptyOneManyRemovalAndDetachesPublishers()
    {
        await InitializeRuntimeAsync();
        await FilesSelectionNotificationsCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FilesSelectionNotificationsCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var tab = workbench.Scripting.CurrentPowerShellTab;
        var model = Assert.Single(workbench.Workbench.Sessions);
        var initial = Assert.Single(tab.Files);
        Assert.Same(initial, tab.Files.SelectedFile);
        Assert.Same(initial, workbench.Scripting.CurrentFile);
        tab.Files.Remove(initial);
        Assert.Empty(tab.Files);
        Assert.Null(tab.Files.SelectedFile);
        Assert.Null(workbench.Scripting.CurrentEditor);
        Assert.Throws<ObjectDisposedException>(() => initial.Editor.Text);
        Assert.Throws<ObjectDisposedException>(() => initial.Editor.Text = "must not resurrect");
        Assert.Throws<ObjectDisposedException>(() => initial.Save());
        var first = tab.Files.Add();
        var firstModel = Assert.Single(model.Files);
        var second = tab.Files.Add();
        Assert.Equal(new[] { first, second }, tab.Files.ToArray());
        Assert.Same(second, tab.Files.SelectedFile);
        var selections = new List<string?>();
        tab.Files.PropertyChanged += (_, args) => selections.Add(args.PropertyName);
        tab.Files.SetSelectedFile(first);
        Assert.Same(first, tab.SelectedFile);
        Assert.Same(first, workbench.Scripting.CurrentFile);
        Assert.Equal(new[] { "SelectedFile" }, selections);
        selections.Clear();
        tab.Files.SetSelectedFile(first);
        Assert.Equal(new[] { "SelectedFile" }, selections);
        var editorEvents = new List<string?>();
        first.Editor.PropertyChanged += (_, args) => editorEvents.Add(args.PropertyName);
        first.Editor.Text = "$x = 1";
        Assert.Equal("$x = 1", firstModel.Document.Text);
        Assert.Single(editorEvents, name => name == "Text");
        Assert.Single(editorEvents, name => name == "LineCount");
        foreach (var property in new[] { "CaretLine", "CaretColumn", "CaretLineText", "SelectedText", "CanGoToMatch" })
            Assert.Contains(property, editorEvents);
        Assert.False(first.IsSaved);
        Assert.Equal("Save the file or use Remove(file, true) to discard edits.", Assert.Throws<InvalidOperationException>(
            () => tab.Files.Remove(first)).Message);
        Assert.Same(first, tab.Files.SelectedFile);
        tab.Files.Remove(first, true);
        Assert.Same(second, tab.Files.SelectedFile);
        Assert.Equal(new[] { second }, tab.Files.ToArray());
        editorEvents.Clear();
        firstModel.Document.Text = "mutated after removal";
        Assert.Empty(editorEvents);
        Assert.Throws<ObjectDisposedException>(() => first.Editor.Text);
        var rootEvents = new List<string?>();
        workbench.Scripting.PropertyChanged += (_, args) => rootEvents.Add(args.PropertyName);
        await workbench.DisposeAsync();
        rootEvents.Clear();
        model.SelectedFile = firstModel;
        model.Files.Add(firstModel);
        Assert.Empty(rootEvents);
        Assert.Throws<ObjectDisposedException>(() => tab.Files.Count);
    });

    [AvaloniaFact]
    public async Task TabDisplayNameNotifiesOnlyForDistinctValidAssignmentsWithoutRenamingEngine()
    {
        await InitializeRuntimeAsync();
        await TabNameAndStatusCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task TabNameAndStatusCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var tab = workbench.Scripting.CurrentPowerShellTab;
        var session = Assert.Single(workbench.Workbench.Sessions);
        var runspace = session.Engine.LocalRunspaceId;
        var names = new List<string?>();
        tab.PropertyChanged += (_, args) => names.Add(args.PropertyName);
        Assert.Equal("PowerShell 1", tab.DisplayName);
        tab.DisplayName = "Owned tab café";
        tab.DisplayName = "Owned tab café";
        Assert.Equal(new[] { "DisplayName" }, names);
        Assert.Equal("Owned tab café", tab.DisplayName);
        Assert.Equal("PowerShell 1", session.Name);
        Assert.Equal("PowerShell 1", session.DisplayName);
        foreach (var invalid in new[] { "", " ", "\t" })
            Assert.Throws<ArgumentException>(() => tab.DisplayName = invalid);
        Assert.Throws<ArgumentNullException>(() => tab.DisplayName = null!);
        Assert.Equal(new[] { "DisplayName" }, names);
        Assert.Equal("Owned tab café", tab.DisplayName);
        Assert.Equal(workbench.FindControl<TextBlock>("StatusText")!.Text, tab.StatusText);
        Assert.True(tab.ExpandedScript);
        Assert.Equal("Script pane visibility is owned by the host. Use Options.SelectedScriptPaneState to change its layout.",
            Assert.Throws<NotSupportedException>(() => tab.ExpandedScript = false).Message);
        foreach (var accepted in new[] { 20d, 400d, 175d })
        {
            tab.Zoom = accepted;
            Assert.Equal(accepted, workbench.GetScriptingSettings().Zoom);
        }
        foreach (var invalid in new[] { 19.999d, 400.001d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Equal("Zoom", Assert.Throws<ArgumentOutOfRangeException>(() => tab.Zoom = invalid).ParamName);
            Assert.Equal(175, tab.Zoom);
        }
        tab.Zoom = 175;
        Assert.Equal(175, workbench.Scripting.Options.Zoom);
        Assert.Equal(175, workbench.FindControl<Avalonia.Controls.Primitives.RangeBase>("ZoomSlider")!.Value);
        tab.ShowCommands = true;
        Assert.True(workbench.GetScriptingSettings().ShowCommands);
        Assert.True(workbench.FindControl<Control>("CommandsPane")!.IsVisible);
        tab.ShowCommands = false;
        Assert.False(workbench.FindControl<Control>("CommandsPane")!.IsVisible);
        Assert.Equal(runspace, session.Engine.LocalRunspaceId);
        await workbench.DrainScriptingSettingsPersistenceAsync();
    });

    [AvaloniaTheory]
    [InlineData(1, true, 7)]
    [InlineData(2, false, 2)]
    [InlineData(5, false, 5)]
    [InlineData(6, false, 6)]
    [InlineData(7, true, 1)]
    public async Task ScriptingGoToMatchRequiresOpenBraceOrImmediatelyAfterClosingBrace(
        int column, bool canMatch, int destination)
    {
        await InitializeRuntimeAsync();
        await GoToMatchCoreAsync(column, canMatch, destination);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task GoToMatchCoreAsync(int column, bool canMatch, int destination) =>
        WithModuleWorkbenchAsync(false, async (workbench, _) =>
        {
            var editor = workbench.Scripting.CurrentEditor!;
            editor.Text = "{ $x }";
            editor.SetCaretPosition(1, column);
            var analysis = await workbench.ScriptEditorView.AnalyzeAsync();
            Assert.Equal(Iseberg.Editor.EditorAnalysisState.Available, analysis.State);
            Assert.Equal(canMatch, editor.CanGoToMatch);
            editor.GoToMatch();
            Assert.Equal(destination, editor.CaretColumn);
            Assert.Equal(1, editor.CaretLine);
            Assert.Equal("{ $x }", editor.Text);
            workbench.Scripting.Options.ShowOutlining = false;
            editor.ToggleOutliningExpansion();
            Assert.Equal(destination, editor.CaretColumn);
        });

    [AvaloniaFact]
    public async Task ScriptingEditorCoordinatesAndSelectionRejectInvalidRangesWithoutTextMutation()
    {
        await InitializeRuntimeAsync();
        await ScriptingCoordinatesCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ScriptingCoordinatesCoreAsync() => WithModuleWorkbenchAsync(false, (workbench, _) =>
    {
        var editor = workbench.Scripting.CurrentEditor!;
        editor.Text = "abc\r\ncafé";
        Assert.Equal(2, editor.LineCount);
        Assert.Equal(3, editor.GetLineLength(1));
        Assert.Equal(4, editor.GetLineLength(2));
        editor.SetCaretPosition(2, 5);
        Assert.Equal(2, editor.CaretLine);
        Assert.Equal(5, editor.CaretColumn);
        Assert.Equal("café", editor.CaretLineText);
        foreach (var position in new[] { (0, 1), (3, 1), (1, 0), (1, 5), (2, 6) })
            Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetCaretPosition(position.Item1, position.Item2));
        Assert.Equal(2, editor.CaretLine);
        Assert.Equal(5, editor.CaretColumn);
        editor.Select(1, 2, 2, 3);
        Assert.Equal("bc\r\nca", editor.SelectedText);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Select(1, 0, 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Select(1, 2, 2, 6));
        Assert.Equal("bc\r\nca", editor.SelectedText);
        Assert.Equal("The selection end must not precede its start.", Assert.Throws<ArgumentException>(
            () => editor.Select(2, 3, 1, 2)).Message);
        Assert.Equal("bc\r\nca", editor.SelectedText);
        editor.InsertText("X");
        Assert.Equal("aXfé", editor.Text);
        Assert.Equal(1, editor.CaretLine);
        Assert.Equal(3, editor.CaretColumn);
        Assert.Equal("", editor.SelectedText);
        Assert.Throws<ArgumentNullException>(() => editor.Text = null!);
        Assert.Equal("aXfé", editor.Text);
        editor.Select(1, 2, 1, 2);
        Assert.Equal("", editor.SelectedText);
        Assert.Equal(2, editor.CaretColumn);
        editor.Clear();
        Assert.Equal(1, editor.LineCount);
        Assert.Equal(0, editor.GetLineLength(1));
        editor.SetCaretPosition(1, 1);
        Assert.Equal(1, editor.CaretColumn);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetCaretPosition(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetCaretPosition(2, 1));
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public async Task ScriptingSnippetMetadataDistinguishesBuiltInCatalogAndExplicitLoadProvenance()
    {
        await InitializeRuntimeAsync();
        await SnippetMetadataCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SnippetMetadataCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var snippets = workbench.Scripting.CurrentPowerShellTab.Snippets;
        var builtIn = snippets.Single(snippet => snippet.Title == "if");
        Assert.Equal("Conditional execution.", builtIn.Description);
        Assert.Equal("Iseberg", builtIn.Author);
        Assert.Equal("if ($condition) {\n    \n}", builtIn.CodeFragment);
        Assert.Equal(22, builtIn.CaretOffset);
        Assert.True(builtIn.Indent);
        Assert.Equal("", builtIn.Compatibility);
        Assert.True(builtIn.IsBuiltIn);
        Assert.True(builtIn.IsDefault);
        Assert.Null(builtIn.FullPath);
        Assert.Null(builtIn.SchemaVersion);
        Assert.False(builtIn.IsTabSpecific);
        await workbench.SaveSnippetsAsync([new("Owned", "Description", "Author", "one\n two", 3, false)]);
        var service = Assert.Single(workbench.Workbench.Sessions).Engine.Snippets;
        var path = Assert.Single(service.GetUserFiles()).FullName;
        var catalog = snippets.Single(snippet => snippet.Title == "Owned");
        Assert.Null(catalog.FullPath);
        Assert.Null(catalog.SchemaVersion);
        Assert.Null(catalog.IsTabSpecific);
        snippets.Load(path);
        var loaded = snippets.Single(snippet => snippet.Title == "Owned");
        Assert.Equal(path, loaded.FullPath);
        Assert.Equal("1.0.0", loaded.SchemaVersion);
        Assert.True(loaded.IsTabSpecific);
        Assert.False(loaded.IsDefault);
        Assert.False(loaded.IsBuiltIn);
        Assert.Equal("Description", loaded.Description);
        Assert.Equal("Author", loaded.Author);
        Assert.Equal("one\n two", loaded.CodeFragment);
        Assert.Equal("one\n two", loaded.Text);
        Assert.Equal("Owned", loaded.DisplayTitle);
        Assert.Equal(3, loaded.CaretOffset);
        Assert.False(loaded.Indent);
        Assert.Equal(("one\r\n two", 3), loaded.Expand("    ", "\r\n"));
        Assert.Equal(Path.Combine(root, "catalog"), Path.GetDirectoryName(path));
        Assert.Equal("fullPath", Assert.Throws<ArgumentException>(() => snippets.Load("relative.snippets.ps1xml")).ParamName);
        var bytes = await File.ReadAllBytesAsync(path);
        var invalid = Path.Combine(root, "bad.snippets.ps1xml");
        await File.WriteAllTextAsync(invalid, "<invalid/>");
        Assert.Throws<InvalidDataException>(() => snippets.Load(invalid));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(path, snippets.Single(snippet => snippet.Title == "Owned").FullPath);
    });

    [AvaloniaFact]
    public async Task PsIseCompositionUsesTheSelectedOwnedRunspaceForNewPortableOperations()
    {
        await InitializeRuntimeAsync();
        await PsIseCompositionCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task PsIseCompositionCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var output = new List<OutputEntry>();
        engine.Output += output.Add;
        try
        {
            await engine.ExecuteAsync("""
                $global:portableParitySentinel = 'same-engine'
                $psISE.Options.FontSize = 18
                $psISE.Options.WordWrap = $true
                $psISE.CurrentPowerShellTab.DisplayName = 'Script owned'
                $file = $psISE.CurrentPowerShellTab.Files.SelectedFile
                $file.Editor.Text = '{ $x }'
                $file.Editor.SetCaretPosition(1, 1)
                $file.Editor.GoToMatch()
                'PARITY:' + $global:portableParitySentinel + ':' + $file.Editor.CaretColumn
                """);
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("PARITY:same-engine:7", StringComparison.Ordinal));
            Assert.Equal(18, workbench.GetScriptingSettings().FontSize);
            Assert.True(workbench.ScriptEditorView.TextEditor.WordWrap);
            Assert.Equal("Script owned", workbench.Scripting.CurrentPowerShellTab.DisplayName);
            Assert.Equal("{ $x }", workbench.Scripting.CurrentEditor!.Text);
            Assert.Equal(7, workbench.Scripting.CurrentEditor.CaretColumn);
            Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            await workbench.DrainScriptingSettingsPersistenceAsync();
        }
        finally { engine.Output -= output.Add; }
    });

    [AvaloniaTheory]
    [InlineData(false, 9d, 12d)]
    [InlineData(false, 11d, 44d / 3)]
    [InlineData(false, 20d, 80d / 3)]
    [InlineData(true, 12d, 12d)]
    [InlineData(true, 44d / 3, 44d / 3)]
    [InlineData(true, 80d / 3, 80d / 3)]
    public async Task OptionsPreviewLeavesDipFontsUnscaledAndConvertsPointFontsOnce(
        bool fontSizesInDips, double storedSize, double previewSize)
    {
        await InitializeRuntimeAsync();
        await OptionsPreviewFontCoreAsync(fontSizesInDips, storedSize, previewSize);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OptionsPreviewFontCoreAsync(bool fontSizesInDips, double storedSize, double previewSize)
    {
        var baseline = new UserSettings
        {
            FontFamily = "Lucida Console", FontSize = storedSize, FixedWidthFontsOnly = false,
            AutoSaveMinutes = 0, CheckForUpdates = false
        };
        var attempts = 0;
        var options = new OptionsWindow(baseline, _ => { attempts++; return Task.CompletedTask; },
            fontSizesInDips: fontSizesInDips);
        try
        {
            options.Show();
            var sizes = options.FindControl<ComboBox>("EditorFontSize")!;
            var sample = options.FindControl<Iseberg.Editor.PowerShellEditorControl>("SampleEditorControl")!;
            Assert.Single(sizes.Items.Cast<double>(), size => size == storedSize);
            Assert.Equal(storedSize, Assert.IsType<double>(sizes.SelectedItem), precision: 10);
            Assert.Equal(storedSize, options.Draft.FontSize, precision: 10);
            Assert.Equal(previewSize, sample.TextEditor.FontSize, precision: 10);
            Assert.Equal("Lucida Console", options.Draft.FontFamily);
            Assert.False(options.FindControl<Button>("ApplyOptions")!.IsEnabled);

            sizes.SelectedItem = 18d;
            Assert.Equal(18d, options.Draft.FontSize);
            Assert.Equal(fontSizesInDips ? 18d : 24d, sample.TextEditor.FontSize, precision: 10);
            Assert.True(options.FindControl<Button>("ApplyOptions")!.IsEnabled);

            sizes.SelectedItem = storedSize;
            Assert.Equal(storedSize, Assert.IsType<double>(sizes.SelectedItem), precision: 10);
            Assert.Equal(storedSize, options.Draft.FontSize, precision: 10);
            Assert.Equal(previewSize, sample.TextEditor.FontSize, precision: 10);
            Assert.False(options.FindControl<Button>("ApplyOptions")!.IsEnabled);
            ClickOwnedButton(options.FindControl<Button>("CancelOptions")!);
            Assert.Equal(0, attempts);
            Assert.Equal(storedSize, baseline.FontSize, precision: 10);
            Assert.Equal("Lucida Console", baseline.FontFamily);
        }
        finally { options.Close(); }
        return Task.CompletedTask;
    }

    [AvaloniaTheory]
    [InlineData("Monochrome Green", true, true, 4)]
    [InlineData("Monochrome Green", false, true, 4)]
    [InlineData("Presentation", true, true, 5)]
    [InlineData("Presentation", false, true, 5)]
    [InlineData("Monochrome Green", true, false, 4)]
    [InlineData("Presentation", false, false, 5)]
    public async Task ManageThemesSelectsCurrentPresetOrPaletteAndInheritsScopedVariant(
        string name, bool darkOptions, bool hasPreset, int expectedIndex)
    {
        await InitializeRuntimeAsync();
        await OptionsManagerScopeCoreAsync(name, darkOptions, hasPreset, expectedIndex);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OptionsManagerScopeCoreAsync(
        string name, bool darkOptions, bool hasPreset, int expectedIndex)
    {
        var variant = darkOptions ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        var owner = new Window
        {
            RequestedThemeVariant = darkOptions ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark
        };
        var baseline = new UserSettings
        {
            ThemePreset = hasPreset ? name : null,
            Theme = EditorThemePresets.Original(hasPreset
                ? name == "Monochrome Green" ? "Presentation" : "Monochrome Green"
                : name),
            FixedWidthFontsOnly = false, AutoSaveMinutes = 0, CheckForUpdates = false
        };
        var attempts = 0;
        var options = new OptionsWindow(baseline, _ => { attempts++; return Task.CompletedTask; },
            hostOwnsTheme: true, hostThemes: EditorThemePresets.OriginalBuiltIns(), fontSizesInDips: true)
        {
            RequestedThemeVariant = variant
        };
        try
        {
            owner.Show();
            options.Show(owner);
            Assert.Equal(variant, options.ActualThemeVariant);
            Assert.NotEqual(variant, owner.ActualThemeVariant);
            var unchanged = JsonSerializer.Serialize(options.Draft);
            ClickOwnedButton(options.FindControl<Button>("ManageThemes")!);
            await WaitForAsync(() => options.OwnedWindows.Any(), () => "Options did not open its scoped theme manager.");
            var manager = Assert.Single(options.OwnedWindows);
            manager.UpdateLayout();
            var list = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(manager).OfType<ListBox>());
            Assert.Same(options, manager.Owner);
            Assert.Equal(variant, manager.RequestedThemeVariant);
            Assert.Equal(variant, manager.ActualThemeVariant);
            Assert.Equal(variant, list.ActualThemeVariant);
            Assert.Equal(expectedIndex, list.SelectedIndex);
            Assert.Equal(name, list.SelectedItem);

            list.SelectedIndex = 0;
            ClickOwnedButton(Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(manager).OfType<Button>(),
                button => Equals(button.Content, "Cancel")));
            await WaitForAsync(() => !manager.IsVisible, () => "Canceled theme manager stayed open.");
            Assert.Equal(unchanged, JsonSerializer.Serialize(options.Draft));
            Assert.Equal(variant, options.ActualThemeVariant);
            Assert.NotEqual(variant, owner.ActualThemeVariant);
            Assert.False(options.FindControl<Button>("ApplyOptions")!.IsEnabled);
            Assert.Equal(0, attempts);
            ClickOwnedButton(options.FindControl<Button>("CancelOptions")!);
            Assert.Equal(hasPreset ? name : null, baseline.ThemePreset);
            Assert.Equal(hasPreset ? name == "Monochrome Green" ? "Presentation" : "Monochrome Green" : name,
                baseline.Theme.Name);
        }
        finally
        {
            foreach (var owned in options.OwnedWindows.ToArray()) owned.Close();
            options.Close();
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("Dark Console, Light Editor (default)", "#FFFFFF", "#012456", 12d)]
    [InlineData("Light Console, Dark Editor", "#012456", "#FFFFFF", 12d)]
    [InlineData("Dark Console, Dark Editor", "#012456", "#012456", 12d)]
    [InlineData("Light Console, Light Editor", "#FFFFFF", "#FFFFFF", 12d)]
    [InlineData("Monochrome Green", "#000000", "#000000", 44d / 3)]
    [InlineData("Presentation", "#FFFFFF", "#000000", 80d / 3)]
    public async Task OriginalBuiltInThemesOptionsDraftCancelFailureApplyAndLaterCancel(
        string name, string scriptBackground, string consoleBackground, double dips)
    {
        await InitializeRuntimeAsync();
        await OptionsThemeTransactionsCoreAsync(name, scriptBackground, consoleBackground, dips);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OptionsThemeTransactionsCoreAsync(
        string name, string scriptBackground, string consoleBackground, double dips)
    {
        var baseline = new UserSettings
        {
            FontSize = 14, AutoSaveMinutes = 0, ThemePreset = "Before",
            Theme = new EditorTheme { Name = "Before" }, CheckForUpdates = false
        };
        var accepted = baseline.Copy();
        var attempts = 0;
        var fail = false;
        Task Apply(UserSettings snapshot)
        {
            attempts++;
            if (fail) throw new InvalidOperationException("controlled-save-failure");
            accepted = snapshot.Copy();
            // The callback must never receive the dialog's mutable draft.
            snapshot.Theme.Colors["Script.Background"] = "#123456";
            return Task.CompletedTask;
        }
        var canceled = new OptionsWindow(baseline, Apply, hostOwnsTheme: true,
            hostThemes: EditorThemePresets.OriginalBuiltIns(), fontSizesInDips: true);
        try
        {
            canceled.Show();
            await SelectOptionsThemeAsync(canceled, name);
            Assert.Equal(name, canceled.Draft.ThemePreset);
            Assert.Equal(scriptBackground, canceled.Draft.Theme.Colors["Script.Background"]);
            Assert.Equal(consoleBackground, canceled.Draft.Theme.Colors["Console.Background"]);
            Assert.Equal(dips, canceled.Draft.FontSize, precision: 10);
            var sizes = canceled.FindControl<ComboBox>("EditorFontSize")!;
            var presetSize = Assert.Single(sizes.Items.Cast<double>(), size => Math.Round(size, 10) == Math.Round(dips, 10));
            Assert.Equal(dips, presetSize, precision: 10);
            Assert.Equal(dips, Assert.IsType<double>(sizes.SelectedItem), precision: 10);
            Assert.Equal("Before", accepted.ThemePreset);
            ClickOwnedButton(canceled.FindControl<Button>("CancelOptions")!);
            Assert.Equal(0, attempts);
            Assert.Equal("Before", accepted.ThemePreset);
            Assert.Equal(14, accepted.FontSize);
        }
        finally
        {
            foreach (var owned in canceled.OwnedWindows.ToArray()) owned.Close();
            canceled.Close();
        }
        var dialog = new OptionsWindow(baseline, Apply, hostOwnsTheme: true,
            hostThemes: EditorThemePresets.OriginalBuiltIns(), fontSizesInDips: true);
        try
        {
            dialog.Show();
            await SelectOptionsThemeAsync(dialog, name);
            fail = true;
            ClickOwnedButton(dialog.FindControl<Button>("ApplyOptions")!);
            await WaitForAsync(() => dialog.OwnedWindows.Any(), () => "Options did not show the controlled save error.");
            var error = Assert.Single(dialog.OwnedWindows);
            Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(error).OfType<TextBlock>(),
                text => text.Text == "controlled-save-failure");
            ClickOwnedButton(Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(error).OfType<Button>(),
                button => Equals(button.Content, "OK")));
            await WaitForAsync(() => dialog.FindControl<Button>("ApplyOptions")!.IsEnabled,
                () => "Options did not permit retry after a failed Apply.");
            Assert.Equal(1, attempts);
            Assert.Equal("Before", accepted.ThemePreset);
            Assert.Equal(name, dialog.Draft.ThemePreset);
            fail = false;
            ClickOwnedButton(dialog.FindControl<Button>("ApplyOptions")!);
            Assert.Equal(2, attempts);
            Assert.Equal(name, accepted.ThemePreset);
            Assert.Equal(scriptBackground, accepted.Theme.Colors["Script.Background"]);
            Assert.Equal(consoleBackground, accepted.Theme.Colors["Console.Background"]);
            Assert.Equal("Lucida Console", accepted.FontFamily);
            Assert.Equal(dips, accepted.FontSize, precision: 10);
            Assert.Equal(scriptBackground, dialog.Draft.Theme.Colors["Script.Background"]);
            Assert.False(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
            var sample = dialog.FindControl<Iseberg.Editor.PowerShellEditorControl>("SampleEditorControl")!;
            Assert.Equal(dips, sample.TextEditor.FontSize, precision: 10);
            Assert.Equal(Avalonia.Media.Color.Parse(scriptBackground),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(sample.TextEditor.Background).Color);
            dialog.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = false;
            Assert.True(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
            ClickOwnedButton(dialog.FindControl<Button>("CancelOptions")!);
            Assert.Equal(2, attempts);
            Assert.True(accepted.ShowLineNumbers);
            Assert.Equal(name, accepted.ThemePreset);
        }
        finally
        {
            foreach (var owned in dialog.OwnedWindows.ToArray()) owned.Close();
            dialog.Close();
        }
        Assert.Equal("Before", baseline.ThemePreset);
        Assert.Equal(14, baseline.FontSize);
    }

    private static async Task SelectOptionsThemeAsync(OptionsWindow options, string name)
    {
        ClickOwnedButton(options.FindControl<Button>("ManageThemes")!);
        await WaitForAsync(() => options.OwnedWindows.Any(), () => "Options did not open its owned theme selector.");
        var manager = Assert.Single(options.OwnedWindows);
        manager.UpdateLayout();
        var list = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(manager).OfType<ListBox>());
        Assert.Equal(new[]
        {
            "Dark Console, Light Editor (default)", "Light Console, Dark Editor", "Dark Console, Dark Editor",
            "Light Console, Light Editor", "Monochrome Green", "Presentation"
        }, list.Items.Cast<string>().ToArray());
        list.SelectedItem = name;
        Assert.Equal(name, list.SelectedItem);
        ClickOwnedButton(Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(manager).OfType<Button>(),
            button => Equals(button.Content, "OK")));
        await WaitForAsync(() => !manager.IsVisible, () => "Accepted theme selector stayed open.");
    }

    private static void ClickOwnedButton(Button button)
    {
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact]
    public async Task ScriptingPersistenceFailureIsReportedAndDrainDoesNotLoseTheAcceptedInMemorySnapshot()
    {
        await InitializeRuntimeAsync();
        await PersistenceFailureCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task PersistenceFailureCoreAsync()
    {
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            CreateInitialSession = false,
            Preferences = new UserSettings { AutoSaveMinutes = 0, CheckForUpdates = false }
        });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("owned persistence failed");
        workbench.ErrorOccurred += (_, error) => reported.TrySetResult(error.Exception);
        try
        {
            workbench.ScriptingSettingsPersistence = (_, _) => release.Task;
            workbench.Scripting.Options.Zoom = 160;
            var drain = workbench.DrainScriptingSettingsPersistenceAsync();
            Assert.False(drain.IsCompleted);
            Assert.Equal(160, workbench.GetScriptingSettings().Zoom);
            release.SetException(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => drain));
            Assert.Same(failure, await reported.Task);
            Assert.Equal(160, workbench.GetScriptingSettings().Zoom);
            var saved = new List<UserSettings>();
            workbench.ScriptingSettingsPersistence = (settings, _) =>
            {
                saved.Add(settings);
                return Task.CompletedTask;
            };
            workbench.Scripting.Options.Zoom = 175;
            await workbench.DrainScriptingSettingsPersistenceAsync();
            Assert.Equal(175, Assert.Single(saved).Zoom);
            Assert.Equal(175, workbench.GetScriptingSettings().Zoom);
        }
        finally
        {
            release.TrySetResult();
            await workbench.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ScriptingHostConstraintsRejectToolbarAndPaneAndCommandsWithoutChangingSettings()
    {
        await InitializeRuntimeAsync();
        await HostConstraintsCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task HostConstraintsCoreAsync()
    {
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            ShowToolbar = false, ShowConsolePane = false, EnableCommandsPane = false, CreateInitialSession = false,
            Preferences = new UserSettings { AutoSaveMinutes = 0, CheckForUpdates = false, Layout = "Maximized" }
        });
        try
        {
            var options = workbench.Scripting.Options;
            var before = JsonSerializer.Serialize(workbench.GetScriptingSettings());
            Assert.False(options.ShowToolBar);
            Assert.Equal("The toolbar is disabled by the workbench host.",
                Assert.Throws<NotSupportedException>(() => options.ShowToolBar = true).Message);
            foreach (var layout in new[] { "Top", "Right" })
                Assert.Equal("This pane layout is disabled by the workbench host.",
                    Assert.Throws<NotSupportedException>(() => options.SelectedScriptPaneState = layout).Message);
            Assert.Equal(before, JsonSerializer.Serialize(workbench.GetScriptingSettings()));
            var model = new SessionModel("PowerShell 1");
            workbench.Workbench.Sessions.Add(model);
            workbench.Workbench.SelectedSession = model;
            var tab = workbench.Scripting.CurrentPowerShellTab;
            Assert.False(tab.ShowCommands);
            Assert.Equal("The Commands pane is disabled by the workbench host.",
                Assert.Throws<NotSupportedException>(() => tab.ShowCommands = true).Message);
            Assert.Equal(before, JsonSerializer.Serialize(workbench.GetScriptingSettings()));
        }
        finally { await workbench.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task ScriptingRejectsForeignFileOwnershipAndUnsupportedWpfAndCrossTabOperationsWithoutMutation()
    {
        await InitializeRuntimeAsync();
        await OwnershipAndUnsupportedCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OwnershipAndUnsupportedCoreAsync() => WithModuleWorkbenchAsync(false, (workbench, root) =>
    {
        var own = workbench.Scripting.CurrentPowerShellTab;
        var selected = own.Files.SelectedFile;
        // This second model is never initialized; it exists solely to provide distinct ownership.
        var foreignModel = new SessionModel("PowerShell 2", Path.Combine(root, "foreign"));
        var foreignDocument = new ScriptTab(ScriptFile.CreateUntitled("foreign.ps1"));
        foreignModel.Files.Add(foreignDocument);
        foreignModel.SelectedFile = foreignDocument;
        workbench.Workbench.Sessions.Add(foreignModel);
        var foreign = workbench.Scripting.PowerShellTabs[1].Files.SelectedFile!;
        foreach (var operation in new Action[] { () => own.Files.SetSelectedFile(foreign), () => own.Files.Remove(foreign, true) })
        {
            var error = Assert.Throws<ArgumentException>(operation);
            Assert.Equal("file", error.ParamName);
            Assert.StartsWith("The file belongs to another PowerShell tab.", error.Message, StringComparison.Ordinal);
        }
        Assert.Same(selected, own.Files.SelectedFile);
        Assert.Same(foreignDocument, Assert.Single(foreignModel.Files));
        Assert.Equal("Cross-tab script invocation is not supported. Run the command in its owning PowerShell tab.",
            Assert.Throws<NotSupportedException>(() => own.Invoke("'never run'")).Message);
        foreach (var operation in new Action[]
        {
            () => own.InvokeSynchronous("'never run'"), () => own.InvokeSynchronous("'never run'", true),
            () => own.InvokeSynchronous("'never run'", false, 0)
        })
            Assert.Equal("Cross-tab synchronous invocation is not supported.",
                Assert.Throws<NotSupportedException>(operation).Message);
        Assert.Equal("Creating PowerShell tabs from scripts is not supported. Use File > New PowerShell Tab.",
            Assert.Throws<NotSupportedException>(() => workbench.Scripting.PowerShellTabs.Add()).Message);
        Assert.Equal("Closing PowerShell tabs from scripts is not supported. Use File > Close PowerShell Tab.",
            Assert.Throws<NotSupportedException>(() => workbench.Scripting.PowerShellTabs.Remove(own)).Message);
        const string wpf = "WPF ISE add-on tools are not supported by Iseberg's Avalonia host, including on Windows. " +
            "Use script-based AddOnsMenu actions; WPF controls cannot run natively on Linux or macOS.";
        foreach (var operation in new Action[]
        {
            () => own.VerticalAddOnTools.Add("never created", typeof(object)),
            () => own.HorizontalAddOnTools.Add("never created", typeof(object), true),
            own.VerticalAddOnTools.Clear, own.HorizontalAddOnTools.Clear,
            () => { _ = workbench.Scripting.VisibleHorizontalAddOnTools; },
            () => { _ = workbench.Scripting.VisibleVerticalAddOnTools; }
        })
            Assert.Equal(wpf, Assert.Throws<NotSupportedException>(operation).Message);
        Assert.False(own.HorizontalAddOnToolsPaneOpened);
        Assert.False(own.VerticalAddOnToolsPaneOpened);
        Assert.Equal("The host owns console input and output; a mutable ISE console editor is not exposed.",
            Assert.Throws<NotSupportedException>(() => own.ConsolePane).Message);
        Assert.Same(selected, own.Files.SelectedFile);
        Assert.Equal("", selected!.Editor.Text);
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public async Task EnabledCommandsPaneDescribesInsertsRunsAndShowsLocalHelpOnItsExistingEngine()
    {
        await InitializeRuntimeAsync();
        await CommandsPaneCompositionCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CommandsPaneCompositionCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        await workbench.ExecuteAsync("""
            $global:ownedCommandsCalls = 0
            function global:Invoke-OwnedParity {
                <#
                .SYNOPSIS
                Independently authored portable command help.
                #>
                param([Parameter(Mandatory)][string]$Text)
                $global:ownedCommandsCalls++
                'OWNED-COMMAND:' + $global:ownedCommandsCalls + ':' + $Text
            }
            """);
        workbench.Scripting.CurrentPowerShellTab.ShowCommands = true;
        Assert.True(workbench.FindControl<Control>("CommandsPane")!.IsVisible);
        ClickOwnedButton(workbench.FindControl<Button>("CommandRefreshButton")!);
        var list = workbench.FindControl<ListBox>("CommandList")!;
        await WaitForAsync(() => list.Items.Cast<CommandDescription>().Any(command => command.Name == "Invoke-OwnedParity"),
            () => "The existing engine's new function did not reach the Commands pane.");
        list.SelectedItem = list.Items.Cast<CommandDescription>().Single(command => command.Name == "Invoke-OwnedParity");
        var view = workbench.FindControl<CommandFormView>("CommandForm")!;
        await WaitForAsync(() => view.Form?.Description.Name == "Invoke-OwnedParity",
            () => "The selected engine's command form did not load.");
        Assert.Equal("Invoke-OwnedParity", view.Form!.Description.InvocationName);
        await WaitForAsync(() => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<TextBox>()
                .Any(box => Avalonia.Automation.AutomationProperties.GetName(box) == "Text"),
            () => "The selected command's Text parameter control did not become available.");
        var text = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<TextBox>(),
            box => Avalonia.Automation.AutomationProperties.GetName(box) == "Text");
        text.Text = "café 'quoted'";
        const string command = "& 'Invoke-OwnedParity' -Text 'café ''quoted'''";
        await WaitForAsync(() => view.Result is { IsValid: true }, () => "Command form did not accept the required text.");
        Assert.Equal(command, view.GetCommand());
        workbench.Scripting.CurrentEditor!.Clear();
        ClickOwnedButton(workbench.FindControl<Button>("CommandInsertButton")!);
        Assert.Equal(command + " ", workbench.Scripting.CurrentEditor.Text);
        var result = new TaskCompletionSource<OutputEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<OutputEntry>();
        void OnOutput(OutputEntry entry)
        {
            if (entry.Kind == OutputKind.Error) errors.Enqueue(entry);
            if (entry.Kind == OutputKind.Output && entry.Text.Contains("OWNED-COMMAND:", StringComparison.Ordinal))
                result.TrySetResult(entry);
        }
        engine.Output += OnOutput;
        try
        {
            ClickOwnedButton(workbench.FindControl<Button>("CommandRunButton")!);
            Assert.Equal("OWNED-COMMAND:1:café 'quoted'" + Environment.NewLine,
                (await result.Task.WaitAsync(TimeSpan.FromSeconds(60))).Text);
            await WaitForAsync(() => engine.State == SessionState.Ready, () => "Commands pane execution did not complete.");
            Assert.Empty(errors);
            Assert.Equal(command + " ", workbench.Scripting.CurrentEditor.Text);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
            var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
            Assert.True(workbench.GetScriptingSettings().UseLocalHelp);
            var helpButton = workbench.FindControl<Button>("CommandHelpButton")!;
            await WaitForAsync(() => helpButton.IsEnabled,
                () => "Commands pane help did not become ready after execution.");
            ClickOwnedButton(helpButton);
            await WaitForAsync(() => owner.OwnedWindows.OfType<CommandHelpWindow>().Any(),
                () => "The Commands pane did not show its local help on the current engine.");
            var help = Assert.Single(owner.OwnedWindows.OfType<CommandHelpWindow>());
            try
            {
                Assert.Contains("Invoke-OwnedParity", help.Title, StringComparison.Ordinal);
                Assert.Contains("Independently authored portable command help.",
                    help.FindControl<AvaloniaEdit.TextEditor>("HelpContent")!.Document.Text, StringComparison.Ordinal);
                Assert.Equal(runspace, engine.LocalRunspaceId);
                Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
            }
            finally { help.Close(); }
        }
        finally { engine.Output -= OnOutput; }
    });

    [AvaloniaFact]
    public async Task OptionsPendingApplyDisablesCloseAndPublishesOnlyAfterControlledSuccess()
    {
        await InitializeRuntimeAsync();
        await OptionsBusyApplyCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OptionsBusyApplyCoreAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<UserSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        UserSettings? accepted = null;
        var dialog = new OptionsWindow(new UserSettings { AutoSaveMinutes = 0, CheckForUpdates = false }, async snapshot =>
        {
            entered.SetResult(snapshot);
            await release.Task;
            accepted = snapshot.Copy();
        });
        try
        {
            dialog.Show();
            dialog.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = false;
            ClickOwnedButton(dialog.FindControl<Button>("ApplyOptions")!);
            var snapshot = await entered.Task;
            Assert.False(snapshot.ShowLineNumbers);
            Assert.Null(accepted);
            Assert.False(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
            Assert.False(dialog.FindControl<Button>("CancelOptions")!.IsEnabled);
            dialog.Close();
            Assert.True(dialog.IsVisible);
            release.SetResult();
            await WaitForAsync(() => dialog.FindControl<Button>("CancelOptions")!.IsEnabled,
                () => "Options remained busy after the controlled successful Apply.");
            Assert.NotNull(accepted);
            Assert.False(accepted.ShowLineNumbers);
            Assert.False(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
            dialog.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = true;
            Assert.False(accepted.ShowLineNumbers);
            ClickOwnedButton(dialog.FindControl<Button>("CancelOptions")!);
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            release.TrySetResult();
            await WaitForAsync(() => dialog.FindControl<Button>("CancelOptions")!.IsEnabled,
                () => "Owned Options callback did not finish for teardown.");
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task ScriptingFilesAddReusesOwnedAbsolutePathWithoutReloadingDirtyEditor()
    {
        await InitializeRuntimeAsync();
        await FilesAddPathCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FilesAddPathCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        Directory.CreateDirectory(root);
        var firstPath = Path.Combine(root, "first.ps1");
        var secondPath = Path.Combine(root, "second.ps1");
        await File.WriteAllTextAsync(firstPath, "first café");
        await File.WriteAllTextAsync(secondPath, "second");
        var files = workbench.Scripting.CurrentPowerShellTab.Files;
        var first = files.Add(firstPath);
        Assert.Equal(firstPath, first.FullPath);
        Assert.Equal("first café", first.Editor.Text);
        Assert.Equal("first.ps1", first.DisplayName);
        Assert.False(first.IsUntitled);
        Assert.True(first.IsSaved);
        var second = files.Add(secondPath);
        Assert.Equal(3, files.Count);
        Assert.Same(second, files.SelectedFile);
        first.Editor.Text = "dirty edit";
        await File.WriteAllTextAsync(firstPath, "externally changed");
        var repeated = files.Add(firstPath);
        Assert.Same(first, repeated);
        Assert.Equal(3, files.Count);
        Assert.Same(first, files.SelectedFile);
        Assert.Equal("dirty edit", first.Editor.Text);
        Assert.Equal("first.ps1*", first.DisplayName);
        Assert.False(first.IsSaved);
        Assert.Equal("externally changed", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("second", second.Editor.Text);
        Assert.Equal("fullPath", Assert.Throws<ArgumentException>(() => files.Add("relative.ps1")).ParamName);
        Assert.Throws<ArgumentNullException>(() => files.Add(null!));
        Assert.Same(first, files.SelectedFile);
        Assert.Equal(3, files.Count);
    });

    [AvaloniaFact]
    public async Task ScriptingExplicitSettingsNotificationPublishesActualOptionsAndTabPropertyNames()
    {
        await InitializeRuntimeAsync();
        await ExplicitSettingsNotificationCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ExplicitSettingsNotificationCoreAsync() => WithModuleWorkbenchAsync(false, (workbench, _) =>
    {
        var tab = workbench.Scripting.CurrentPowerShellTab;
        var options = workbench.Scripting.Options;
        var optionsEvents = new List<(object? Sender, string? Name)>();
        var tabEvents = new List<(object? Sender, string? Name)>();
        options.PropertyChanged += (sender, args) => optionsEvents.Add((sender, args.PropertyName));
        tab.PropertyChanged += (sender, args) => tabEvents.Add((sender, args.PropertyName));
        var before = JsonSerializer.Serialize(workbench.GetScriptingSettings());
        workbench.NotifyScriptingSettingsChanged();
        var optionEvent = Assert.Single(optionsEvents);
        Assert.Same(options, optionEvent.Sender);
        Assert.Equal("", optionEvent.Name);
        Assert.Equal(new[] { "ExpandedScript", "ShowCommands", "Zoom" }, tabEvents.Select(entry => entry.Name).ToArray());
        Assert.All(tabEvents, entry => Assert.Same(tab, entry.Sender));
        Assert.Equal(before, JsonSerializer.Serialize(workbench.GetScriptingSettings()));
        return Task.CompletedTask;
    });

    [AvaloniaFact]
    public async Task PortableOptionsRestoreDefaultsResetsItsInventoryButPreservesHostOwnedFields()
    {
        await InitializeRuntimeAsync();
        await OptionsRestoreDefaultsCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OptionsRestoreDefaultsCoreAsync()
    {
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            CreateInitialSession = false,
            Preferences = new UserSettings
            {
                ThemePreset = "Presentation", Theme = EditorThemePresets.Original("Presentation"),
                LoadProfiles = true, ShowCommands = false, CheckForUpdates = false, AutoSaveMinutes = 0,
                FontFamily = "Consolas", FontSize = 18, Zoom = 175, ShowLineNumbers = false,
                ShowOutlining = false, WordWrap = true, ShowToolbar = false, WarnDuplicateFiles = false,
                PromptToSaveBeforeRun = false, UseDefaultSnippets = false, ConsoleIntelliSense = false,
                ConsoleCompletionOnEnter = false, ScriptIntelliSense = false, ScriptCompletionOnEnter = false,
                IntelliSenseTimeoutSeconds = 17, RecentFileCount = 23, UseLocalHelp = false, FixedWidthFontsOnly = true
            }
        });
        try
        {
            workbench.Scripting.Options.RestoreDefaults();
            await workbench.DrainScriptingSettingsPersistenceAsync();
            var actual = workbench.GetScriptingSettings();
            Assert.Equal("Lucida Console", actual.FontFamily);
            Assert.Equal(9, actual.FontSize);
            Assert.Equal(100, actual.Zoom);
            Assert.Equal(3, actual.IntelliSenseTimeoutSeconds);
            Assert.Equal(2, actual.AutoSaveMinutes);
            Assert.Equal(10, actual.RecentFileCount);
            Assert.True(actual.ShowLineNumbers);
            Assert.True(actual.ShowOutlining);
            Assert.False(actual.WordWrap);
            Assert.True(actual.ShowToolbar);
            Assert.True(actual.WarnDuplicateFiles);
            Assert.True(actual.PromptToSaveBeforeRun);
            Assert.True(actual.UseDefaultSnippets);
            Assert.True(actual.ConsoleIntelliSense);
            Assert.True(actual.ConsoleCompletionOnEnter);
            Assert.True(actual.ScriptIntelliSense);
            Assert.True(actual.ScriptCompletionOnEnter);
            Assert.True(actual.UseLocalHelp);
            Assert.False(actual.FixedWidthFontsOnly);
            Assert.True(actual.LoadProfiles);
            Assert.Equal("Presentation", actual.ThemePreset);
            Assert.Equal("#000000", actual.Theme.Colors["Console.Background"]);
            Assert.Equal("#FFFFFF", actual.Theme.Colors["Script.Background"]);
            Assert.False(actual.ShowCommands);
            Assert.False(actual.CheckForUpdates);
        }
        finally { await workbench.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData("AutoSaveInterval", 0, 120)]
    [InlineData("RecentFileCount", 0, 100)]
    public async Task OptionsNumericTextFieldsAcceptBoundsRejectAdjacentAndMalformedValuesWithoutSaving(
        string controlName, int minimum, int maximum)
    {
        await InitializeRuntimeAsync();
        await OptionsNumericValidationCoreAsync(controlName, minimum, maximum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OptionsNumericValidationCoreAsync(string controlName, int minimum, int maximum)
    {
        var accepted = new List<UserSettings>();
        var dialog = new OptionsWindow(new UserSettings { CheckForUpdates = false }, snapshot =>
        {
            accepted.Add(snapshot.Copy());
            return Task.CompletedTask;
        });
        try
        {
            dialog.Show();
            var input = dialog.FindControl<TextBox>(controlName)!;
            foreach (var value in new[] { maximum, minimum, 17 })
            {
                input.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await WaitForAsync(() => dialog.FindControl<Button>("ApplyOptions")!.IsEnabled &&
                    (controlName == "AutoSaveInterval" ? dialog.Draft.AutoSaveMinutes : dialog.Draft.RecentFileCount) == value,
                    () => $"Options rejected the valid {controlName} value {value}.");
                Assert.Equal(value, controlName == "AutoSaveInterval" ? dialog.Draft.AutoSaveMinutes : dialog.Draft.RecentFileCount);
                Assert.False(dialog.FindControl<Control>("ValidationPanel")!.IsVisible);
            }
            foreach (var value in new[] { (minimum - 1).ToString(), (maximum + 1).ToString(), "", "invalid", "1.5" })
            {
                input.Text = value;
                await WaitForAsync(() => dialog.FindControl<Control>("ValidationPanel")!.IsVisible,
                    () => $"Options accepted invalid {controlName} text '{value}'.");
                Assert.False(dialog.FindControl<Button>("AcceptOptions")!.IsEnabled);
                Assert.False(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
                Assert.Equal(17, controlName == "AutoSaveInterval" ? dialog.Draft.AutoSaveMinutes : dialog.Draft.RecentFileCount);
                Assert.Empty(accepted);
            }
            input.Text = "17";
            await WaitForAsync(() => dialog.FindControl<Button>("ApplyOptions")!.IsEnabled,
                () => "Corrected Options numeric value did not restore acceptance.");
            ClickOwnedButton(dialog.FindControl<Button>("ApplyOptions")!);
            Assert.Equal(17, controlName == "AutoSaveInterval" ? Assert.Single(accepted).AutoSaveMinutes : Assert.Single(accepted).RecentFileCount);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task OptionsInvalidColorCannotPublishAndCorrectionRestoresExactPalette()
    {
        await InitializeRuntimeAsync();
        await OptionsColorValidationCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OptionsColorValidationCoreAsync()
    {
        var accepted = new List<UserSettings>();
        var dialog = new OptionsWindow(new UserSettings { CheckForUpdates = false }, snapshot =>
        {
            accepted.Add(snapshot.Copy());
            return Task.CompletedTask;
        });
        try
        {
            dialog.Show();
            var tree = dialog.FindControl<TreeView>("ColorTree")!;
            var script = tree.Items.Cast<TreeViewItem>().First();
            tree.SelectedItem = script.Items.Cast<TreeViewItem>().Single(item => Equals(item.Tag, "Script.Background"));
            dialog.FindControl<CheckBox>("Hexadecimal")!.IsChecked = false;
            var red = dialog.FindControl<TextBox>("RedValue")!;
            red.Text = "999";
            await WaitForAsync(() => dialog.FindControl<Control>("ValidationPanel")!.IsVisible,
                () => "Out-of-byte-range color did not disable Options acceptance.");
            Assert.False(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
            Assert.False(dialog.FindControl<Button>("AcceptOptions")!.IsEnabled);
            Assert.Equal("#FFFFFF", dialog.Draft.Theme.Colors["Script.Background"]);
            Assert.Empty(accepted);
            red.Text = "0";
            await WaitForAsync(() => dialog.FindControl<Button>("ApplyOptions")!.IsEnabled,
                () => "Corrected color did not restore Options acceptance.");
            ClickOwnedButton(dialog.FindControl<Button>("ApplyOptions")!);
            Assert.Equal("#00FFFF", Assert.Single(accepted).Theme.Colors["Script.Background"]);
            Assert.Equal("#00FFFF", dialog.Draft.Theme.Colors["Script.Background"]);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ShowDefaultSnippetsFiltersBuiltInsPreservesOwnedMetadataAndNotifiesOnlyOnChange()
    {
        await InitializeRuntimeAsync();
        await SnippetDefaultsFilteringCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SnippetDefaultsFilteringCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        await workbench.SaveSnippetsAsync([new("GapOwned", "Owned description", "Owned author", "one\n two", 3, false)]);
        var service = Assert.Single(workbench.Workbench.Sessions).Engine.Snippets;
        var path = Assert.Single(service.GetUserFiles()).FullName;
        Assert.Equal(Path.Combine(root, "catalog"), Path.GetDirectoryName(path));
        var bytes = await File.ReadAllBytesAsync(path);
        var snippets = workbench.Scripting.CurrentPowerShellTab.Snippets;
        snippets.Load(path);
        Assert.True(snippets.Single(snippet => snippet.Title == "if").IsBuiltIn);
        var notifications = new List<(object? Sender, System.Collections.Specialized.NotifyCollectionChangedAction Action)>();
        snippets.CollectionChanged += (sender, args) => notifications.Add((sender, args.Action));

        workbench.Scripting.Options.ShowDefaultSnippets = false;
        Assert.False(workbench.GetScriptingSettings().UseDefaultSnippets);
        var owned = Assert.Single(snippets);
        Assert.Equal("GapOwned", owned.Title);
        Assert.Equal("Owned description", owned.Description);
        Assert.Equal("Owned author", owned.Author);
        Assert.Equal("one\n two", owned.CodeFragment);
        Assert.Equal(3, owned.CaretOffset);
        Assert.False(owned.Indent);
        Assert.False(owned.IsBuiltIn);
        Assert.False(owned.IsDefault);
        Assert.Equal(path, owned.FullPath);
        Assert.Equal("1.0.0", owned.SchemaVersion);
        Assert.True(owned.IsTabSpecific);
        Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, Assert.Single(notifications).Action);
        workbench.Scripting.Options.ShowDefaultSnippets = false;
        Assert.Single(notifications);

        workbench.Scripting.Options.ShowDefaultSnippets = true;
        var restored = snippets.Single(snippet => snippet.Title == "if");
        Assert.True(restored.IsBuiltIn);
        Assert.Equal("Conditional execution.", restored.Description);
        Assert.Equal("if ($condition) {\n    \n}", restored.CodeFragment);
        Assert.Equal(path, snippets.Single(snippet => snippet.Title == "GapOwned").FullPath);
        Assert.Equal(new[]
        {
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset,
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset
        }, notifications.Select(notification => notification.Action).ToArray());
        Assert.All(notifications, notification => Assert.Same(snippets, notification.Sender));
        workbench.Scripting.Options.ShowDefaultSnippets = true;
        Assert.Equal(2, notifications.Count);
        await workbench.DrainScriptingSettingsPersistenceAsync();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.Combine(root, "catalog")));
    });

    [AvaloniaTheory]
    [InlineData("\n", 0, true, "  X\n  YTAIL", 2)]
    [InlineData("\n", 2, true, "  X\n  YTAIL", 6)]
    [InlineData("\n", 3, true, "  X\n  YTAIL", 7)]
    [InlineData("\r", 2, true, "  X\r  YTAIL", 6)]
    [InlineData("\r\n", 2, true, "  X\r\n  YTAIL", 7)]
    [InlineData("\r\n", 2, false, "  X\r\nYTAIL", 5)]
    public async Task PublicSnippetInsertionReplacesSelectionAndUsesExpandedIndentationNewlineAndCaret(
        string newLine, int snippetCaret, bool indent, string expectedText, int expectedCaret)
    {
        await InitializeRuntimeAsync();
        await SnippetInsertionCoreAsync(newLine, snippetCaret, indent, expectedText, expectedCaret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SnippetInsertionCoreAsync(
        string newLine, int snippetCaret, bool indent, string expectedText, int expectedCaret) =>
        WithModuleWorkbenchAsync(false, (workbench, _) =>
        {
            var document = workbench.CreateDocument("  abTAIL");
            var editor = workbench.ScriptEditorView.TextEditor;
            editor.Select(2, 2);
            Assert.Equal("ab", editor.SelectedText);

            WorkbenchControl.InsertSnippet(editor, new("Owned insertion", "description", "author", "X\nY", snippetCaret, indent), newLine);

            Assert.Equal(expectedText, editor.Document.Text);
            Assert.Equal(expectedText, document.Document.Text);
            Assert.Equal(expectedCaret, editor.CaretOffset);
            Assert.Equal(0, editor.SelectionLength);
            Assert.Equal("", editor.SelectedText);
            return Task.CompletedTask;
        });

    [AvaloniaFact]
    public void PublicSnippetInsertionRejectsReadOnlyWithoutChangingSelectionTextOrCaret()
    {
        var editor = new AvaloniaEdit.TextEditor { Text = "  abTAIL" };
        editor.Select(2, 2);
        editor.CaretOffset = 4;
        editor.IsReadOnly = true;

        Assert.Throws<InvalidOperationException>(() =>
            WorkbenchControl.InsertSnippet(editor, new("Owned insertion", "description", "author", "X\nY", 2), "\r\n"));

        Assert.Equal("  abTAIL", editor.Document.Text);
        Assert.Equal(4, editor.CaretOffset);
        Assert.Equal(2, editor.SelectionStart);
        Assert.Equal(2, editor.SelectionLength);
        Assert.Equal("ab", editor.SelectedText);
    }

    [AvaloniaTheory]
    [InlineData(0, "X\nYTAIL", 3)]
    [InlineData(4, "TAILX\nY", 7)]
    public void PublicSnippetInsertionAtDocumentBoundariesKeepsUnselectedText(
        int offset, string expectedText, int expectedCaret)
    {
        var editor = new AvaloniaEdit.TextEditor { Text = "TAIL" };
        editor.Select(offset, 0);

        WorkbenchControl.InsertSnippet(editor, new("Owned insertion", "description", "author", "X\nY", 3), "\n");

        Assert.Equal(expectedText, editor.Document.Text);
        Assert.Equal(expectedCaret, editor.CaretOffset);
        Assert.Equal(0, editor.SelectionLength);
        Assert.Equal("", editor.SelectedText);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CreateSnippetRealPromptCancellationPreservesEditorAndConfiguredCatalog(int cancelAt)
    {
        await InitializeRuntimeAsync();
        await SnippetPromptCancellationCoreAsync(cancelAt);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SnippetPromptCancellationCoreAsync(int cancelAt) => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        await workbench.SaveSnippetsAsync([new("Kept", "baseline", "author", "baseline code")]);
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var runspace = engine.LocalRunspaceId;
        var path = Assert.Single(engine.Snippets.GetUserFiles()).FullName;
        var bytes = await File.ReadAllBytesAsync(path);
        var editor = workbench.ScriptEditorView;
        editor.Document.Text = "before café '<&>'\r\n$owned after";
        editor.Select(new(7, 17));
        var text = editor.Document.Text;
        var selection = editor.Selection;
        var caret = editor.CaretOffset;
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        ClickPhase2Action(workbench, "CreateSnippet");
        var labels = new[] { "SnippetTitle", "SnippetDescription", "SnippetAuthor" };
        for (var index = 0; index <= cancelAt; index++)
            await RespondToPhase2PromptAsync(owner, UiText.Get(labels[index]), "Owned field " + index, index == cancelAt);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Empty(owner.OwnedWindows);
        Assert.Equal(text, editor.Document.Text);
        Assert.Equal(selection, editor.Selection);
        Assert.Equal(caret, editor.CaretOffset);
        Assert.Equal(new[] { path }, engine.Snippets.GetUserFiles().Select(file => file.FullName).ToArray());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new PowerShellSnippet[] { new("Kept", "baseline", "author", "baseline code") },
            (await engine.Snippets.LoadAsync()).Snippets);
        Assert.False(File.Exists(Path.Combine(root, "settings.json")));
        Assert.Equal(runspace, engine.LocalRunspaceId);
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateSnippetUiGateRejectsNoSelectionAndReadOnlyWithoutPromptOrWrite(bool readOnly)
    {
        await InitializeRuntimeAsync();
        await SnippetUiGateCoreAsync(readOnly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SnippetUiGateCoreAsync(bool readOnly) => WithPhase2ErrorWorkbenchAsync(async (workbench, owner, root, errors) =>
    {
        var editor = workbench.ScriptEditorView;
        editor.Document.Text = "before selected after";
        editor.Select(readOnly ? new(7, 8) : new(4, 0));
        editor.IsReadOnly = readOnly;
        var selection = editor.Selection;
        var caret = editor.CaretOffset;
        ClickPhase2Action(workbench, "CreateSnippet");
        await WaitForAsync(() => errors.Count > 0, () => "CreateSnippet did not publish its selection/read-only error.");
        var error = Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Equal(UiText.Get("SelectSnippetCode"), error.Exception.Message);
        Assert.Empty(owner.OwnedWindows);
        Assert.Equal("before selected after", editor.Document.Text);
        Assert.Equal(selection, editor.Selection);
        Assert.Equal(caret, editor.CaretOffset);
        Assert.False(Directory.Exists(Path.Combine(root, "catalog")));
        Assert.Empty((await workbench.Workbench.SelectedSession!.Engine.Snippets.LoadAsync()).Snippets);
    });

    [AvaloniaTheory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task CreateSnippetBlankTitleStopsBeforeDescriptionAndPreservesSelection(string title)
    {
        await InitializeRuntimeAsync();
        await BlankSnippetTitleCoreAsync(title);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task BlankSnippetTitleCoreAsync(string title) => WithPhase2ErrorWorkbenchAsync(async (workbench, owner, root, errors) =>
    {
        var editor = workbench.ScriptEditorView;
        editor.Document.Text = "selected code";
        editor.Select(new(0, 8));
        ClickPhase2Action(workbench, "CreateSnippet");
        await RespondToPhase2PromptAsync(owner, UiText.Get("SnippetTitle"), title, false);
        await WaitForAsync(() => errors.Count > 0, () => "Blank snippet title was not rejected.");
        Assert.Equal(UiText.Get("SnippetTitleRequired"), Assert.IsType<InvalidOperationException>(Assert.Single(errors).Exception).Message);
        Assert.Empty(owner.OwnedWindows);
        Assert.Equal(new Iseberg.Editor.EditorTextSpan(0, 8), editor.Selection);
        Assert.Equal("selected code", editor.Document.Text);
        Assert.False(Directory.Exists(Path.Combine(root, "catalog")));
    });

    [AvaloniaTheory]
    [InlineData("Owned café & <title>", "Description & detail", "Author β")]
    [InlineData("../metadata only", "", "")]
    public async Task CreateSnippetRealUiWritesConfiguredPathAndReloadsExactMetadataAndSelectedCode(
        string title, string description, string author)
    {
        await InitializeRuntimeAsync();
        await CreateSnippetUiRoundTripCoreAsync(title, description, author);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CreateSnippetUiRoundTripCoreAsync(string title, string description, string author) =>
        WithModuleWorkbenchAsync(false, async (workbench, root) =>
        {
            var engine = workbench.Workbench.SelectedSession!.Engine;
            var runspace = engine.LocalRunspaceId;
            var editor = workbench.ScriptEditorView;
            const string code = "café '<&>'\r\n$owned";
            editor.Document.Text = "before " + code + " after";
            editor.Select(new(7, code.Length));
            var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
            var status = workbench.FindControl<TextBlock>("StatusText")!;
            var statusAnnouncements = new List<string?>();
            status.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBlock.TextProperty) statusAnnouncements.Add(status.Text);
            };
            ClickPhase2Action(workbench, "CreateSnippet");
            await RespondToPhase2PromptAsync(owner, UiText.Get("SnippetTitle"), title, false);
            await RespondToPhase2PromptAsync(owner, UiText.Get("SnippetDescription"), description, false);
            await RespondToPhase2PromptAsync(owner, UiText.Get("SnippetAuthor"), author, false);
            await WaitForAsync(() => engine.Snippets.GetUserFiles().Any() &&
                    statusAnnouncements.Contains(UiText.Get("SnippetSaved")),
                () => "CreateSnippet did not finish saving and announce its owned catalog.");
            var path = Assert.Single(engine.Snippets.GetUserFiles()).FullName;
            Assert.Equal(Path.Combine(root, "catalog"), Path.GetDirectoryName(path));
            Assert.True(Guid.TryParseExact(Path.GetFileName(path).Replace(".snippets.ps1xml", ""), "N", out _));
            var independentService = new IseSnippetService(Path.Combine(root, "catalog"));
            var loaded = await independentService.LoadAsync();
            Assert.Empty(loaded.Errors);
            Assert.Equal(new PowerShellSnippet(title, description, author, code, -1, true), Assert.Single(loaded.Snippets));
            var xml = XDocument.Parse(await File.ReadAllTextAsync(path));
            var ns = XNamespace.Get("http://schemas.microsoft.com/PowerShell/Snippets");
            Assert.Equal(code, xml.Root!.Element(ns + "Snippet")!.Element(ns + "Code")!.Element(ns + "Script")!.Value);
            Assert.Equal("before " + code + " after", editor.Document.Text);
            Assert.Equal(new Iseberg.Editor.EditorTextSpan(7, code.Length), editor.Selection);
            Assert.Single(statusAnnouncements, text => text == UiText.Get("SnippetSaved"));
            editor.Document.Text = "  target";
            editor.Select(new(2, 6));
            WorkbenchControl.InsertSnippet(editor.TextEditor, loaded.Snippets[0], "\r\n");
            Assert.Equal("  café '<&>'\r\n  $owned", editor.Document.Text);
            Assert.Equal(22, editor.CaretOffset);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Equal(new[] { path }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        });

    private static void ClickPhase2Action(WorkbenchControl workbench, string action)
    {
        static IEnumerable<MenuItem> Descendants(MenuItem item) =>
            new[] { item }.Concat(item.Items.OfType<MenuItem>().SelectMany(Descendants));
        var menu = Assert.Single(workbench.FindControl<Menu>("WorkbenchMenu")!.Items.OfType<MenuItem>()
            .SelectMany(Descendants), item => Equals(item.Tag, action));
        Assert.True(menu.IsEnabled);
        menu.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static async Task RespondToPhase2PromptAsync(Window owner, string label, string value, bool cancel)
    {
        await WaitForAsync(() => owner.OwnedWindows.Any(), () => "The owned prompt did not appear: " + label);
        var dialog = Assert.Single(owner.OwnedWindows);
        try
        {
            dialog.UpdateLayout();
            Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<TextBlock>(),
                block => block.Text == label);
            var input = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<TextBox>());
            Assert.Equal("", input.Text);
            input.Text = value;
            if (cancel) ClickCancel(dialog);
            else ClickOwnedButton(Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Button>(),
                button => Equals(button.Content, UiText.Get("OK"))));
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task WithPhase2ErrorWorkbenchAsync(
        Func<WorkbenchControl, Window, string, List<WorkbenchErrorEventArgs>, Task> test)
    {
        using var environment = new IseTestEnvironment();
        var root = Path.Combine(environment.DirectoryPath, "ui-errors");
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            SnippetDirectory = Path.Combine(root, "catalog"), StartingDirectory = environment.DirectoryPath,
            Preferences = new UserSettings { AutoSaveMinutes = 0, LoadProfiles = false, CheckForUpdates = false, ShowCommands = false }
        });
        var errors = new List<WorkbenchErrorEventArgs>();
        workbench.ErrorOccurred += (_, error) => errors.Add(error);
        var owner = new Window { Width = 1100, Height = 800, Content = workbench };
        try
        {
            owner.Show();
            await workbench.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Empty(errors);
            await test(workbench, owner, root, errors);
        }
        finally
        {
            foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close();
            try { await workbench.DisposeAsync(); }
            finally { owner.Content = null; owner.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData("Invoke-Phase2Help argument", 3, "Invoke-Phase2Help", "Owned outer local help.")]
    [InlineData("Invoke-Phase2Help argument", 22, "Invoke-Phase2Help", "Owned outer local help.")]
    [InlineData("Invoke-Phase2Help (Invoke-Phase2Inner argument)", 28, "Invoke-Phase2Inner", "Owned inner local help.")]
    [InlineData("Invoke-Phase2Help (Invoke-Phase2Inner argument)", 39, "Invoke-Phase2Inner", "Owned inner local help.")]
    [InlineData("Invoke-Phase2Help left | Invoke-Phase2Inner right", 44, "Invoke-Phase2Inner", "Owned inner local help.")]
    public async Task F1LocalHelpUsesEnclosingCommandAcrossCommandArgumentNestedAndPipelineContexts(
        string text, int caret, string command, string synopsis)
    {
        await InitializeRuntimeAsync();
        await ContextHelpCoreAsync(text, caret, command, synopsis);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ContextHelpCoreAsync(string text, int caret, string command, string synopsis) =>
        WithPhase2ErrorWorkbenchAsync(async (workbench, owner, _, errors) =>
        {
            var engine = workbench.Workbench.SelectedSession!.Engine;
            var runspace = engine.LocalRunspaceId;
            await workbench.ExecuteAsync("""
                function global:Invoke-Phase2Help {
                    <#
                    .SYNOPSIS
                    Owned outer local help.
                    #>
                    param([string]$Text)
                    $Text
                }
                function global:Invoke-Phase2Inner {
                    <#
                    .SYNOPSIS
                    Owned inner local help.
                    #>
                    param([string]$Text)
                    $Text
                }
                """);
            var editor = workbench.ScriptEditorView;
            editor.Document.Text = text;
            editor.CaretOffset = caret;
            await editor.AnalyzeAsync();
            Assert.True(workbench.GetScriptingSettings().UseLocalHelp);
            Assert.True(editor.FocusEditor());
            PressPhase2Key(owner, Avalonia.Input.Key.F1, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.F1);
            await WaitForAsync(() => owner.OwnedWindows.OfType<CommandHelpWindow>().Any() || errors.Count > 0,
                () => "F1 did not complete local help routing.");
            // Check invariants before the semantic assertion so routing defects do not hide a replacement engine/edit.
            Assert.Equal(text, editor.Document.Text);
            Assert.Equal(caret, editor.CaretOffset);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
            Assert.Empty(errors);
            var help = Assert.Single(owner.OwnedWindows.OfType<CommandHelpWindow>());
            try
            {
                Assert.Contains(command, help.Title, StringComparison.Ordinal);
                Assert.Contains(synopsis, help.FindControl<AvaloniaEdit.TextEditor>("HelpContent")!.Text, StringComparison.Ordinal);
            }
            finally { help.Close(); }
        });

    [AvaloniaTheory]
    [InlineData("", 0)]
    [InlineData("   \n", 2)]
    [InlineData("# owned comment", 5)]
    [InlineData("'owned literal'", 5)]
    [InlineData("42", 1)]
    public async Task F1NoCommandContextUsesLocalGetHelpFallbackWithoutEditingOrLaunching(string text, int caret)
    {
        await InitializeRuntimeAsync();
        await FallbackHelpCoreAsync(text, caret);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FallbackHelpCoreAsync(string text, int caret) => WithPhase2ErrorWorkbenchAsync(async (workbench, owner, _, errors) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var runspace = engine.LocalRunspaceId;
        var editor = workbench.ScriptEditorView;
        editor.Document.Text = text;
        editor.CaretOffset = caret;
        await editor.AnalyzeAsync();
        Assert.True(editor.FocusEditor());
        PressPhase2Key(owner, Avalonia.Input.Key.F1, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.F1);
        await WaitForAsync(() => owner.OwnedWindows.OfType<CommandHelpWindow>().Any() || errors.Count > 0,
            () => "F1 no-command fallback did not complete.");
        Assert.Equal(text, editor.Document.Text);
        Assert.Equal(caret, editor.CaretOffset);
        Assert.Equal(runspace, engine.LocalRunspaceId);
        Assert.Empty(errors);
        var help = Assert.Single(owner.OwnedWindows.OfType<CommandHelpWindow>());
        try { Assert.Contains("Get-Help", help.Title, StringComparison.Ordinal); }
        finally { help.Close(); }
    });

    [AvaloniaTheory]
    [InlineData("run")]
    [InlineData("pass-thru")]
    [InlineData("insert")]
    [InlineData("cancel")]
    [InlineData("invalid-form")]
    public async Task InRunspaceShowCommandUsesSameEngineAndRealAcceptInsertCancelOrInvalidForm(string action)
    {
        await InitializeRuntimeAsync();
        await InRunspaceShowCommandCoreAsync(action);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task InRunspaceShowCommandCoreAsync(string action) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var runspace = engine.LocalRunspaceId;
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        var output = new System.Collections.Concurrent.ConcurrentQueue<OutputEntry>();
        engine.Output += output.Enqueue;
        Task? request = null;
        try
        {
            await workbench.ExecuteAsync("""
                $global:phase2ShowCalls = 0
                $global:phase2Prefix = 'OWNED-SHOW'
                function global:Invoke-Phase2Show {
                    <#
                    .SYNOPSIS
                    Owned in-runspace Show-Command local help.
                    #>
                    param([Parameter(Mandatory)][string]$Text)
                    $global:phase2ShowCalls++
                    $global:phase2Prefix + ':' + $global:phase2ShowCalls + ':' + $Text
                }
                """);
            workbench.ScriptEditorView.Document.Text = "before after";
            workbench.ScriptEditorView.Select(new(7, 0));
            output.Clear();
            request = workbench.ExecuteAsync("Show-Command Invoke-Phase2Show -NoCommonParameter -Width 420 -Height 430" +
                (action == "pass-thru" ? " -PassThru" : ""));
            await WaitForAsync(() => owner.OwnedWindows.OfType<ShowCommandWindow>().Any() || request.IsCompleted,
                () => "The in-runspace Show-Command request did not show its form.");
            Assert.False(request.IsCompleted);
            var dialog = Assert.Single(owner.OwnedWindows.OfType<ShowCommandWindow>());
            try
            {
                Assert.Equal(420, dialog.Width);
                Assert.Equal(430, dialog.Height);
                Assert.Equal("Invoke-Phase2Show", dialog.Title);
                var view = dialog.FindControl<CommandFormView>("ShowCommandForm")!;
                Assert.Equal("Invoke-Phase2Show", view.Form!.Description.Name);
                Assert.DoesNotContain(view.Form.Description.ParameterSets.SelectMany(set => set.Parameters), parameter => parameter.IsCommon);
                Assert.False(dialog.FindControl<Button>("ShowCommandRun")!.IsEnabled);
                Assert.False(dialog.FindControl<MenuItem>("ShowCommandInsert")!.IsEnabled);
                await WaitForAsync(() => view.Result is not null,
                    () => "Show-Command did not finish validating its initial form.");
                Assert.False(view.Result!.IsValid);
                Assert.False(request.IsCompleted);
                Assert.Equal("before after", workbench.ScriptEditorView.Document.Text);
                if (action is not ("cancel" or "invalid-form"))
                {
                    dialog.UpdateLayout();
                    var input = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<TextBox>(),
                        box => Avalonia.Automation.AutomationProperties.GetName(box) == "Text");
                    input.Text = "café 'quoted'";
                    await WaitForAsync(() => view.Result is { IsValid: true }, () => "Show-Command did not accept mandatory text.");
                    Assert.Equal("& 'Invoke-Phase2Show' -Text 'café ''quoted'''", view.GetCommand());
                }
                if (action == "insert")
                    dialog.FindControl<MenuItem>("ShowCommandInsert")!.RaiseEvent(
                        new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                else if (action is "cancel" or "invalid-form") ClickCancel(dialog);
                else ClickOwnedButton(dialog.FindControl<Button>("ShowCommandRun")!);
            }
            finally { if (dialog.IsVisible) dialog.Close(); }
            await request.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            var values = output.Where(entry => entry.Kind == OutputKind.Output).Select(entry => entry.Text).ToArray();
            Assert.Equal(action == "run" ? new[] { "OWNED-SHOW:1:café 'quoted'" + Environment.NewLine } :
                action == "pass-thru" ? new[] { "& 'Invoke-Phase2Show' -Text 'café ''quoted'''" + Environment.NewLine } :
                Array.Empty<string>(), values);
            Assert.Equal(action == "insert" ? "before & 'Invoke-Phase2Show' -Text 'café ''quoted''' after" : "before after",
                workbench.ScriptEditorView.Document.Text);
            output.Clear();
            await workbench.ExecuteAsync("'SHOW-CALLS:' + $global:phase2ShowCalls");
            Assert.Contains(output, entry => entry.Kind == OutputKind.Output &&
                entry.Text == "SHOW-CALLS:" + (action == "run" ? "1" : "0") + Environment.NewLine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
            Assert.Equal(SessionState.Ready, engine.State);
        }
        finally
        {
            foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close();
            if (request is { IsCompleted: false }) { await engine.StopAsync(); await request; }
            engine.Output -= output.Enqueue;
        }
    });

    [AvaloniaFact]
    public async Task InRunspaceShowCommandUnknownCommandReportsExactErrorWithoutDialogExecutionOrEdit()
    {
        await InitializeRuntimeAsync();
        await UnknownShowCommandCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task UnknownShowCommandCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var runspace = engine.LocalRunspaceId;
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        workbench.ScriptEditorView.Document.Text = "kept editor";
        workbench.ScriptEditorView.CaretOffset = 4;
        var output = new System.Collections.Concurrent.ConcurrentQueue<OutputEntry>();
        engine.Output += output.Enqueue;
        try
        {
            await workbench.ExecuteAsync("$global:unknownShowRan = 0; Show-Command No-SuchPhase2OwnedCommand");
            Assert.Equal("Command 'No-SuchPhase2OwnedCommand' was not found in this PowerShell tab." + Environment.NewLine,
                Assert.Single(output, entry => entry.Kind == OutputKind.Error).Text);
            Assert.Empty(owner.OwnedWindows);
            Assert.Equal("kept editor", workbench.ScriptEditorView.Document.Text);
            Assert.Equal(4, workbench.ScriptEditorView.CaretOffset);
            output.Clear();
            await workbench.ExecuteAsync("'UNKNOWN-CALLS:' + $global:unknownShowRan");
            Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text == "UNKNOWN-CALLS:0" + Environment.NewLine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Equal(SessionState.Ready, engine.State);
        }
        finally { engine.Output -= output.Enqueue; }
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualScriptCompletionRemainsAvailableWhenAutomaticPreferenceIsDisabled(bool automaticEnabled)
    {
        await InitializeRuntimeAsync();
        await ManualCompletionPolicyCoreAsync(automaticEnabled);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ManualCompletionPolicyCoreAsync(bool automaticEnabled) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var runspace = engine.LocalRunspaceId;
        var editor = workbench.ScriptEditorView;
        workbench.Scripting.Options.ShowIntellisenseInScriptPane = automaticEnabled;
        editor.Document.Text = "owned";
        editor.CaretOffset = 5;
        var provider = new Phase2CompletionProvider();
        editor.CompletionProvider = provider;
        Assert.True(editor.FocusEditor());
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        PressPhase2Key(owner, Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Space);
        await WaitForAsync(() => editor.IsCompletionOpen, () => "Manual Ctrl+Space did not invoke the configured provider.");
        Assert.Equal(1, provider.Calls);
        Assert.Equal("owned", provider.LastText);
        Assert.Equal(5, provider.LastCaret);
        Assert.Equal("owned-result", editor.CompletionPopup!.CompletionList.SelectedItem!.Text);
        Assert.Equal("owned", editor.Document.Text);
        editor.CloseCompletion();
        Assert.False(editor.IsCompletionOpen);
        Assert.Equal(runspace, engine.LocalRunspaceId);
        await workbench.DrainScriptingSettingsPersistenceAsync();
    });

    [AvaloniaFact]
    public async Task EnabledAutomaticCompletionUsesTypedContextAndDisabledPreferenceClosesItsPopup()
    {
        await InitializeRuntimeAsync();
        await AutomaticCompletionPolicyCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task AutomaticCompletionPolicyCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var editor = workbench.ScriptEditorView;
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        var provider = new Phase2CompletionProvider();
        editor.CompletionProvider = provider;
        editor.Document.Text = "";
        Assert.True(editor.FocusEditor());
        Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(owner, "$");
        await WaitForAsync(() => editor.IsCompletionOpen, () => "Enabled automatic '$' completion never invoked the provider.");
        Assert.Equal(1, provider.Calls);
        Assert.Equal("$", provider.LastText);
        Assert.Equal(1, provider.LastCaret);
        Assert.Equal("$", editor.Document.Text);
        var popup = editor.CompletionPopup!;
        workbench.Scripting.Options.ShowIntellisenseInScriptPane = false;
        Assert.False(editor.IsCompletionOpen);
        Assert.False(popup.IsOpen);
        Assert.Equal("$", editor.Document.Text);
        Assert.Equal(1, editor.CaretOffset);
        await workbench.DrainScriptingSettingsPersistenceAsync();
    });

    private sealed class Phase2CompletionProvider : Iseberg.Editor.IEditorCompletionProvider
    {
        public int Calls { get; private set; }
        public string? LastText { get; private set; }
        public int LastCaret { get; private set; }
        public Task<Iseberg.Editor.EditorCompletionList> CompleteAsync(Iseberg.Editor.EditorCompletionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastText = request.Text;
            LastCaret = request.CaretOffset;
            return Task.FromResult(new Iseberg.Editor.EditorCompletionList(request.Version, new(0, request.CaretOffset),
                [new("owned-result", "owned-result", "owned provider description")]));
        }
    }

    private static void PressPhase2Key(Window owner, Avalonia.Input.Key key, Avalonia.Input.RawInputModifiers modifiers,
        Avalonia.Input.PhysicalKey physical)
    {
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(owner, key, modifiers, physical, null);
        Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(owner, key, modifiers, physical, null);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("{\n$x\n}")]
    [InlineData("{\n{\n$x\n}\n}")]
    [InlineData("{\n$x\n}\n{\n$y\n}")]
    public async Task WrapperOutliningTogglesActualSectionsAndReenableRebuildsEditedFolds(string text)
    {
        await InitializeRuntimeAsync();
        await WrapperOutliningCoreAsync(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task WrapperOutliningCoreAsync(string text) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var wrapper = workbench.Scripting.CurrentEditor!;
        var editor = workbench.ScriptEditorView;
        wrapper.Text = text;
        await editor.AnalyzeAsync();
        var manager = Assert.Single(editor.TextEditor.TextArea.TextView.ElementGenerators
            .OfType<AvaloniaEdit.Folding.FoldingElementGenerator>()).FoldingManager!;
        var expected = text == "" ? Array.Empty<(int, int)>() :
            text == "{\n$x\n}" ? new[] { (0, 6) } :
            text == "{\n{\n$x\n}\n}" ? new[] { (0, 10), (2, 8) } :
            new[] { (0, 6), (7, 13) };
        Assert.Equal(expected, manager.AllFoldings.Select(fold => (fold.StartOffset, fold.EndOffset)).ToArray());
        wrapper.ToggleOutliningExpansion();
        Assert.All(manager.AllFoldings, fold => Assert.True(fold.IsFolded));
        wrapper.ToggleOutliningExpansion();
        Assert.All(manager.AllFoldings, fold => Assert.False(fold.IsFolded));
        workbench.Scripting.Options.ShowOutlining = false;
        Assert.Empty(manager.AllFoldings);
        wrapper.Text = "{\n{\n$x\n}\n}";
        await editor.AnalyzeAsync();
        wrapper.ToggleOutliningExpansion();
        Assert.Empty(manager.AllFoldings);
        Assert.Equal("{\n{\n$x\n}\n}", wrapper.Text);
        workbench.Scripting.Options.ShowOutlining = true;
        await editor.AnalyzeAsync();
        Assert.Equal(new[] { (0, 10), (2, 8) }, manager.AllFoldings.Select(fold => (fold.StartOffset, fold.EndOffset)).ToArray());
        Assert.All(manager.AllFoldings, fold => Assert.False(fold.IsFolded));
        wrapper.ToggleOutliningExpansion();
        Assert.All(manager.AllFoldings, fold => Assert.True(fold.IsFolded));
        await workbench.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(wrapper.ToggleOutliningExpansion);
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrapperBraceNavigationUnfoldsOnlyItsContainingSectionEvenWhenEditorIsReadOnly(bool readOnly)
    {
        await InitializeRuntimeAsync();
        await FoldBraceVisibilityCoreAsync(readOnly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FoldBraceVisibilityCoreAsync(bool readOnly) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var wrapper = workbench.Scripting.CurrentEditor!;
        wrapper.Text = "{\n$x\n}\n{\n$y\n}";
        await workbench.ScriptEditorView.AnalyzeAsync();
        var manager = Assert.Single(workbench.ScriptEditorView.TextEditor.TextArea.TextView.ElementGenerators
            .OfType<AvaloniaEdit.Folding.FoldingElementGenerator>()).FoldingManager!;
        var folds = manager.AllFoldings.ToArray();
        Assert.Equal(2, folds.Length);
        wrapper.SetCaretPosition(1, 1);
        workbench.ScriptEditorView.IsReadOnly = readOnly;
        wrapper.ToggleOutliningExpansion();
        Assert.All(folds, fold => Assert.True(fold.IsFolded));
        wrapper.GoToMatch();
        Assert.False(folds[0].IsFolded);
        Assert.True(folds[1].IsFolded);
        Assert.Equal(3, wrapper.CaretLine);
        Assert.Equal(2, wrapper.CaretColumn);
        Assert.Equal("{\n$x\n}\n{\n$y\n}", wrapper.Text);
        Assert.Equal(readOnly, workbench.ScriptEditorView.IsReadOnly);
    });

    [AvaloniaTheory]
    [InlineData(7, 2, "$x")]
    [InlineData(7, 16, "$x\n'café <&> 😀'")]
    [InlineData(10, 13, "'café <&> 😀'")]
    public async Task ActualHeadlessClipboardCopyCarriesOnlySelectedPlainTextAndRichHtml(
        int start, int length, string selected)
    {
        await InitializeRuntimeAsync();
        await RichClipboardCoreAsync(start, length, selected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task RichClipboardCoreAsync(int start, int length, string selected) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var clipboardText = selected.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        var editor = workbench.ScriptEditorView;
        editor.Document.Text = "before $x\n'café <&> 😀' after";
        editor.Select(new(start, length));
        Assert.Equal(Iseberg.Editor.EditorAnalysisState.Available, (await editor.AnalyzeAsync()).State);
        var caret = editor.CaretOffset;
        var sentinelFormat = Avalonia.Input.DataFormat.CreateBytesPlatformFormat("DT-Iseberg-Clipboard-Sentinel");
        var sentinelBytes = System.Text.Encoding.UTF8.GetBytes("owned byte transport sentinel");
        var sentinel = new Avalonia.Input.DataTransfer();
        var sentinelItem = new Avalonia.Input.DataTransferItem();
        sentinelItem.SetText("owned clipboard sentinel");
        sentinelItem.Set(sentinelFormat, sentinelBytes);
        sentinel.Add(sentinelItem);
        await owner.Clipboard!.SetDataAsync(sentinel);
        Assert.Equal(sentinelBytes, await Avalonia.Input.Platform.ClipboardExtensions.TryGetValueAsync(owner.Clipboard!, sentinelFormat));
        editor.TextEditor.Copy();
        await WaitForAsync(() => Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(owner.Clipboard!).GetAwaiter().GetResult() == clipboardText,
            () => "Copy did not publish exactly the selected text to the owned clipboard.");
        Assert.Equal(clipboardText, await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(owner.Clipboard!));
        var fragment = await ReadRichClipboardFragmentAsync(owner.Clipboard!);
        var html = XElement.Parse(fragment, LoadOptions.PreserveWhitespace);
        Assert.Equal(selected, html.Value);
        Assert.Equal("pre", html.Name.LocalName);
        Assert.DoesNotContain("before", fragment);
        Assert.DoesNotContain(" after", fragment);
        Assert.DoesNotContain("<&>", fragment);
        Assert.Contains("white-space:pre;tab-size:4;font-family:monospace;color:#000000;background-color:#FFFFFF",
            html.Attribute("style")!.Value, StringComparison.Ordinal);
        if (selected.Contains("$x", StringComparison.Ordinal))
            Assert.Contains(html.Elements("span"), span => span.Value == "$x" &&
                span.Attribute("style")!.Value.Equals("color:#FF4500;", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("before $x\n'café <&> 😀' after", editor.Document.Text);
        Assert.Equal(caret, editor.CaretOffset);
        Assert.Equal(new Iseberg.Editor.EditorTextSpan(start, length), editor.Selection);
    });

    private static async Task<string> ReadRichClipboardFragmentAsync(Avalonia.Input.Platform.IClipboard clipboard)
    {
        if (OperatingSystem.IsMacOS())
            return System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(
                await Avalonia.Input.Platform.ClipboardExtensions.TryGetValueAsync(clipboard,
                    Avalonia.Input.DataFormat.CreateBytesPlatformFormat("public.html"))));
        if (!OperatingSystem.IsWindows())
            return Assert.IsType<string>(await Avalonia.Input.Platform.ClipboardExtensions.TryGetValueAsync(clipboard,
                Avalonia.Input.DataFormat.CreateStringPlatformFormat("text/html")));
        var bytes = Assert.IsType<byte[]>(await Avalonia.Input.Platform.ClipboardExtensions.TryGetValueAsync(clipboard,
            Avalonia.Input.DataFormat.CreateBytesPlatformFormat("HTML Format")));
        var header = System.Text.Encoding.UTF8.GetString(bytes).Split("\r\n");
        var start = int.Parse(header.Single(line => line.StartsWith("StartFragment:", StringComparison.Ordinal))[14..],
            System.Globalization.CultureInfo.InvariantCulture);
        var end = int.Parse(header.Single(line => line.StartsWith("EndFragment:", StringComparison.Ordinal))[12..],
            System.Globalization.CultureInfo.InvariantCulture);
        return System.Text.Encoding.UTF8.GetString(bytes, start, end - start);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ClipboardNoSelectionHonorsWholeLinePolicyAndReadOnlyStillAllowsCopy(bool wholeLine, bool readOnly)
    {
        await InitializeRuntimeAsync();
        await ClipboardEmptySelectionCoreAsync(wholeLine, readOnly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ClipboardEmptySelectionCoreAsync(bool wholeLine, bool readOnly) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        var editor = workbench.ScriptEditorView;
        editor.Document.Text = "before\r\ncafé <&>\r\nafter";
        editor.CaretOffset = 10;
        editor.IsReadOnly = readOnly;
        editor.TextEditor.Options.CutCopyWholeLine = wholeLine;
        Assert.Equal(Iseberg.Editor.EditorAnalysisState.Available, (await editor.AnalyzeAsync()).State);
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(owner.Clipboard!, "owned clipboard sentinel");
        editor.TextEditor.Copy();
        Assert.Equal(wholeLine ? "café <&>" + Environment.NewLine : "owned clipboard sentinel", await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(owner.Clipboard!));
        if (wholeLine)
        {
            var fragment = await ReadRichClipboardFragmentAsync(owner.Clipboard!);
            Assert.Equal("café <&>\n", XElement.Parse(fragment, LoadOptions.PreserveWhitespace).Value);
            Assert.DoesNotContain("<&>", fragment, StringComparison.Ordinal);
        }
        if (readOnly)
        {
            editor.Select(new(8, 8));
            editor.TextEditor.Copy();
            Assert.Equal("café <&>", await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(owner.Clipboard!));
            Assert.False(editor.TextEditor.CanCut);
            Assert.False(editor.TextEditor.CanPaste);
        }
        Assert.Equal("before\r\ncafé <&>\r\nafter", editor.Document.Text);
    });

    [AvaloniaTheory]
    [InlineData("read-only")]
    [InlineData("protected")]
    public async Task ActualPasteAndCutRejectReadOnlyOrFullyProtectedSelectionWithoutMutation(string protection)
    {
        await InitializeRuntimeAsync();
        await ClipboardProtectedCoreAsync(protection);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ClipboardProtectedCoreAsync(string protection) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        var editor = workbench.ScriptEditorView;
        editor.Document.Text = "before selected after";
        editor.Select(new(7, 8));
        var originalProtection = editor.TextEditor.TextArea.ReadOnlySectionProvider;
        if (protection == "read-only") editor.IsReadOnly = true;
        else editor.TextEditor.TextArea.ReadOnlySectionProvider = new Phase2ProtectedSection();
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(owner.Clipboard!, "replacement");
        Assert.False(editor.TextEditor.CanPaste);
        editor.TextEditor.Paste();
        Assert.Equal("before selected after", editor.Document.Text);
        Assert.Equal(new Iseberg.Editor.EditorTextSpan(7, 8), editor.Selection);
        Assert.Equal(15, editor.CaretOffset);
        Assert.Equal("replacement", await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(owner.Clipboard!));
        // AvaloniaEdit advertises Cut for a selected protected region even though deletion is filtered.
        Assert.Equal(protection == "protected", editor.TextEditor.CanCut);
        editor.TextEditor.Cut();
        Assert.Equal("before selected after", editor.Document.Text);
        Assert.Equal(15, editor.CaretOffset);
        Assert.Equal(new Iseberg.Editor.EditorTextSpan(7, 8), editor.Selection);
        Assert.Equal(protection == "protected" ? "selected" : "replacement",
            await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(owner.Clipboard!));
        editor.IsReadOnly = false;
        editor.TextEditor.TextArea.ReadOnlySectionProvider = originalProtection;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(owner.Clipboard!, "replacement");
        Assert.True(editor.TextEditor.CanPaste);
        editor.TextEditor.Paste();
        await WaitForAsync(() => editor.Document.Text == "before replacement after", () => "Unprotected paste did not work.");
        Assert.Equal(18, editor.CaretOffset);
    });

    private sealed class Phase2ProtectedSection : AvaloniaEdit.Editing.IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => false;
        public IEnumerable<AvaloniaEdit.Document.ISegment> GetDeletableSegments(AvaloniaEdit.Document.ISegment segment) => [];
    }

    [AvaloniaFact]
    public async Task TabDisplayAliasUpdatesRealLabelButDoesNotRewritePersistedSessionIdentity()
    {
        await InitializeRuntimeAsync();
        await TabAliasUiPersistenceCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task TabAliasUiPersistenceCoreAsync() => WithModuleWorkbenchAsync(true, async (workbench, root) =>
    {
        var first = workbench.Workbench.SelectedSession!;
        var second = await workbench.CreateSessionAsync();
        var rootWrapper = workbench.Scripting;
        var firstWrapper = rootWrapper.PowerShellTabs[0];
        var secondWrapper = rootWrapper.PowerShellTabs[1];
        firstWrapper.DisplayName = "Shared alias café";
        secondWrapper.DisplayName = "Shared alias café";
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        owner.UpdateLayout();
        var labels = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(workbench.FindControl<Avalonia.Controls.Primitives.TabStrip>("SessionTabs")!)
            .OfType<TextBlock>().Where(block => block.Text == "Shared alias café").ToArray();
        Assert.Equal(2, labels.Length);
        Assert.Equal("PowerShell 1", first.Name);
        Assert.Equal("PowerShell 2", second.Name);
        Assert.Same(second, workbench.Workbench.SelectedSession);
        await workbench.SaveRecoveryAsync();
        var state = JsonSerializer.Deserialize<WorkbenchState>(await File.ReadAllTextAsync(Path.Combine(root, "settings.json.workbench.json")));
        Assert.Equal(new[] { "PowerShell 1", "PowerShell 2" }, state!.Sessions.Select(session => session.Name).ToArray());
        Assert.Equal(1, state.SelectedSession);
        Assert.Equal("Shared alias café", firstWrapper.DisplayName);
        Assert.Equal("Shared alias café", secondWrapper.DisplayName);
    });

    [AvaloniaFact]
    public async Task ForeignWorkbenchTabAndDocumentSelectionRejectsOwnershipWithoutPublishingNotifications()
    {
        await InitializeRuntimeAsync();
        await ForeignWorkbenchOwnershipCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ForeignWorkbenchOwnershipCoreAsync() => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var foreign = new WorkbenchControl(new WorkbenchOptions
        {
            SnippetDirectory = Path.Combine(root, "foreign-catalog"), StartingDirectory = Path.GetDirectoryName(root),
            Preferences = new UserSettings { AutoSaveMinutes = 0, LoadProfiles = false, CheckForUpdates = false, ShowCommands = false }
        });
        var owner = new Window { Content = foreign, Width = 900, Height = 600 };
        try
        {
            owner.Show();
            await foreign.InitializeAsync();
            var ownModel = workbench.Workbench.SelectedSession!;
            var ownFile = workbench.Scripting.CurrentFile;
            var ownEditor = workbench.Scripting.CurrentEditor;
            var foreignTab = foreign.Scripting.CurrentPowerShellTab;
            var foreignFile = foreign.Scripting.CurrentFile!;
            var notifications = new List<string?>();
            workbench.Scripting.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            Assert.Equal("tab", Assert.Throws<ArgumentException>(() =>
                workbench.Scripting.PowerShellTabs.SetSelectedPowerShellTab(foreignTab)).ParamName);
            Assert.Equal("session", Assert.Throws<ArgumentException>(() =>
                workbench.SelectSession(foreign.Workbench.SelectedSession!)).ParamName);
            Assert.Equal("document", Assert.Throws<ArgumentException>(() =>
                workbench.SelectDocument(foreign.Workbench.SelectedSession!.SelectedFile!)).ParamName);
            Assert.Equal("file", Assert.Throws<ArgumentException>(() =>
                workbench.Scripting.CurrentPowerShellTab.Files.SetSelectedFile(foreignFile)).ParamName);
            Assert.Equal("file", Assert.Throws<ArgumentException>(() =>
                workbench.Scripting.CurrentPowerShellTab.Files.Remove(foreignFile, true)).ParamName);
            Assert.Empty(notifications);
            Assert.Same(ownModel, workbench.Workbench.SelectedSession);
            Assert.Same(ownFile, workbench.Scripting.CurrentFile);
            Assert.Same(ownEditor, workbench.Scripting.CurrentEditor);
            Assert.Same(foreignFile, foreign.Scripting.CurrentFile);
            Assert.Equal("", ownEditor!.Text);
            Assert.Equal("", foreignFile.Editor.Text);
        }
        finally { await foreign.DisposeAsync(); owner.Content = null; owner.Close(); }
    });

    [AvaloniaTheory]
    [InlineData(19, null)]
    [InlineData(20, "$owned = 41  [Int32]")]
    [InlineData(22, "$owned = 41  [Int32]")]
    [InlineData(25, "$owned = 41  [Int32]")]
    [InlineData(26, null)]
    [InlineData(36, null)]
    public async Task ActualPausedVariableHoverHonorsExactTokenBoundaryAndClearsOnResume(int offset, string? expected)
    {
        await InitializeRuntimeAsync();
        await PausedVariableHoverCoreAsync(offset, expected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task PausedVariableHoverCoreAsync(int offset, string? expected) =>
        WithPhase2PausedScriptAsync(async (workbench, owner, session, execution) =>
        {
            var editor = workbench.ScriptEditorView.TextEditor;
            RaisePhase2Hover(owner, workbench, 20);
            await WaitForAsync(() => ToolTip.GetIsOpen(editor), () => "The initial actual paused variable hover did not open.");
            Assert.Equal("$owned = 41  [Int32]", Assert.IsType<TextBlock>(ToolTip.GetTip(editor)).Text);
            RaisePhase2Hover(owner, workbench, offset);
            if (expected is null)
            {
                Assert.False(ToolTip.GetIsOpen(editor));
                Assert.Null(ToolTip.GetTip(editor));
            }
            else
            {
                await WaitForAsync(() => ToolTip.GetIsOpen(editor), () => "Variable boundary hover did not open.");
                Assert.Equal(expected, Assert.IsType<TextBlock>(ToolTip.GetTip(editor)).Text);
            }
            session.Engine.Resume(Iseberg.Core.DebuggerResumeAction.Continue);
            await execution.WaitAsync(TimeSpan.FromSeconds(60));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.False(ToolTip.GetIsOpen(editor));
            Assert.Null(ToolTip.GetTip(editor));
            Assert.Null(session.DebugLocation);
            Assert.Null(session.DebugSnapshot);
            Assert.Equal(SessionState.Ready, session.Engine.State);
        });

    [AvaloniaTheory]
    [InlineData("document-edit")]
    [InlineData("document-switch")]
    [InlineData("evaluation")]
    [InlineData("detach")]
    public async Task ActualPausedHoverInvalidatesOnDocumentRevisionSelectionEvaluationOrDetach(string change)
    {
        await InitializeRuntimeAsync();
        await PausedHoverInvalidationCoreAsync(change);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task PausedHoverInvalidationCoreAsync(string change) =>
        WithPhase2PausedScriptAsync(async (workbench, owner, session, _) =>
        {
            var editor = workbench.ScriptEditorView.TextEditor;
            var original = session.SelectedFile!;
            var runspace = session.Engine.LocalRunspaceId;
            RaisePhase2Hover(owner, workbench, 22);
            await WaitForAsync(() => ToolTip.GetIsOpen(editor), () => "Actual paused hover did not open before invalidation.");
            Assert.Equal("$owned = 41  [Int32]", Assert.IsType<TextBlock>(ToolTip.GetTip(editor)).Text);
            if (change == "document-edit") original.Document.Insert(0, "# revision\n");
            else if (change == "document-switch") workbench.CreateDocument("different document");
            else if (change == "evaluation") await workbench.SubmitConsoleInputAsync("$owned = 99");
            else owner.Content = null;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.False(ToolTip.GetIsOpen(editor));
            Assert.Null(ToolTip.GetTip(editor));
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);
            Assert.True(session.Engine.IsDebuggerPaused);
            if (change == "evaluation")
            {
                RaisePhase2Hover(owner, workbench, 22);
                await WaitForAsync(() => ToolTip.GetIsOpen(editor), () => "Hover did not resolve the new debugger revision.");
                Assert.Equal("$owned = 99  [Int32]", Assert.IsType<TextBlock>(ToolTip.GetTip(editor)).Text);
                Assert.Contains(session.DebugSnapshot!.Variables, value => value.Name == "$owned" && value.Value == "99");
            }
            if (change == "document-edit") Assert.Equal("# revision\n$owned = 41\n$next = $owned + 1; Write-Output $next", original.Document.Text);
            if (change == "document-switch") Assert.Equal("different document", session.SelectedFile!.Document.Text);
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task WithPhase2PausedScriptAsync(Func<WorkbenchControl, Window, SessionModel, Task, Task> test) =>
        WithModuleWorkbenchAsync(false, async (workbench, root) =>
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "Owned hover.ps1");
            const string script = "$owned = 41\n$next = $owned + 1; Write-Output $next";
            await File.WriteAllTextAsync(path, script);
            await workbench.OpenFileAsync(path);
            var session = workbench.Workbench.SelectedSession!;
            await session.Engine.SetLineBreakpointsAsync(path, [new(BreakpointKind.Line, path, Line: 2)]);
            var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
            Task? execution = null;
            try
            {
                execution = workbench.ExecuteAsync(". '" + path.Replace("'", "''") + "'");
                await WaitForAsync(() => session.Engine.IsDebuggerPaused && session.DebugSnapshot?.Variables.Any(
                        value => value.Name == "$owned" && value.Value == "41") == true,
                    () => "Owned script did not pause with its actual independently seeded variable snapshot.");
                Assert.Equal(2, session.DebugLocation!.Line);
                Assert.Equal(path, session.DebugLocation.ScriptPath);
                Assert.Equal(script, workbench.ScriptEditorView.Document.Text);
                await workbench.ScriptEditorView.AnalyzeAsync();
                var view = workbench.ScriptEditorView.TextEditor.TextArea.TextView;
                owner.UpdateLayout();
                view.EnsureVisualLines();
                var renderer = Assert.Single(view.BackgroundRenderers.OfType<ScriptAdornments>());
                var group = new Avalonia.Media.DrawingGroup();
                using (var context = group.Open()) renderer.Draw(view, context);
                var rectangles = AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(
                    view, new AvaloniaEdit.Document.SimpleSegment(12, 18)).ToArray();
                Assert.NotEmpty(rectangles);
                Assert.Equal(rectangles, group.Children.OfType<Avalonia.Media.GeometryDrawing>()
                    .Where(drawing => drawing.Brush is Avalonia.Media.ISolidColorBrush { Color.A: 65 })
                    .Select(drawing => drawing.Geometry!.Bounds).ToArray());
                await test(workbench, owner, session, execution);
            }
            finally
            {
                if (session.Engine.IsDebuggerPaused) session.Engine.Resume(Iseberg.Core.DebuggerResumeAction.Continue);
                if (execution is not null) await execution.WaitAsync(TimeSpan.FromSeconds(60));
                await session.Engine.SetLineBreakpointsAsync(path, []);
                owner.Content = workbench;
            }
        });

    private static void RaisePhase2Hover(Window owner, WorkbenchControl workbench, int offset, bool stopped = false,
        bool allowUnmappedLineEnd = false)
    {
        var editor = workbench.ScriptEditorView.TextEditor;
        owner.UpdateLayout();
        var view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        var location = editor.Document.GetLocation(offset);
        var point = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(location),
            AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - view.ScrollOffset;
        var inEditor = Avalonia.VisualExtensions.TranslatePoint(view, point + new Avalonia.Vector(0.1, 0), editor)!.Value;
        if (workbench.ScriptEditorView.TryGetOffsetFromPoint(inEditor, out var actual))
            Assert.Equal(offset, actual);
        else
        {
            // One pixel fraction beyond a rendered line end has no glyph/offset. It remains
            // a real outside-token hover and must dismiss the previous tooltip.
            Assert.True(allowUnmappedLineEnd);
            Assert.Equal(offset, editor.Document.GetLineByOffset(offset).EndOffset);
        }
        var inOwner = Avalonia.VisualExtensions.TranslatePoint(editor, inEditor, owner)!.Value;
        var pointer = new Avalonia.Input.Pointer(321, Avalonia.Input.PointerType.Mouse, true);
        editor.RaiseEvent(new Avalonia.Input.PointerEventArgs(stopped
            ? AvaloniaEdit.TextEditor.PointerHoverStoppedEvent : AvaloniaEdit.TextEditor.PointerHoverEvent, editor,
            pointer, owner, inOwner, 0, new Avalonia.Input.PointerPointProperties(), Avalonia.Input.KeyModifiers.None));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileTitleAndPathNotificationsFollowOwnedSaveAsWithoutInventingMutableDisplayName(bool dirty)
    {
        await InitializeRuntimeAsync();
        await FileTitleSaveNotificationsCoreAsync(dirty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FileTitleSaveNotificationsCoreAsync(bool dirty) => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        Directory.CreateDirectory(root);
        var file = workbench.Scripting.CurrentFile!;
        var editor = file.Editor;
        var notifications = new List<string?>();
        file.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        if (dirty) editor.Text = "owned unsaved text";
        Assert.Equal(dirty ? "Untitled1.ps1*" : "Untitled1.ps1", file.DisplayName);
        Assert.Equal(!dirty, file.IsSaved);
        notifications.Clear();
        var first = Path.Combine(root, "first owned.ps1");
        await workbench.ExecuteAsync("$psISE.CurrentFile.SaveAs('" + first.Replace("'", "''") + "')");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Equal(first, file.FullPath);
        Assert.Equal("first owned.ps1", file.DisplayName);
        Assert.False(file.IsUntitled);
        Assert.True(file.IsSaved);
        Assert.Equal(dirty ? "owned unsaved text" : "", await File.ReadAllTextAsync(first));
        Assert.Single(notifications, name => name == "FullPath");
        Assert.Single(notifications, name => name == "IsUntitled");
        Assert.Contains("DisplayName", notifications);
        Assert.Single(notifications, name => name == "IsSaved");
        var second = Path.Combine(root, "second owned.ps1");
        notifications.Clear();
        await workbench.ExecuteAsync("$psISE.CurrentFile.SaveAs('" + second.Replace("'", "''") + "')");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Equal(second, file.FullPath);
        Assert.Equal("second owned.ps1", file.DisplayName);
        Assert.Single(notifications, name => name == "FullPath");
        Assert.Single(notifications, name => name == "IsUntitled");
        Assert.Single(notifications, name => name == "DisplayName");
        Assert.Single(notifications, name => name == "IsSaved");
        Assert.Equal(dirty ? "owned unsaved text" : "", await File.ReadAllTextAsync(first));
        Assert.Equal(dirty ? "owned unsaved text" : "", await File.ReadAllTextAsync(second));
        Assert.Equal(2, Directory.GetFiles(root, "*.ps1").Length);
    });

    [AvaloniaTheory]
    [InlineData("add")]
    [InlineData("move")]
    [InlineData("replace")]
    [InlineData("remove")]
    [InlineData("reset")]
    public async Task WrapperSessionCollectionTranslatesAddMoveReplaceRemoveResetAndInvalidatesRemovedOwners(string action)
    {
        await InitializeRuntimeAsync();
        await SessionCollectionNotificationsCoreAsync(action);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SessionCollectionNotificationsCoreAsync(string action) => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var first = workbench.Workbench.SelectedSession!;
        var second = new SessionModel("PowerShell 2", Path.Combine(root, "second-catalog"));
        var replacement = new SessionModel("PowerShell 3", Path.Combine(root, "third-catalog"));
        var collection = workbench.Scripting.PowerShellTabs;
        var firstWrapper = collection[0];
        if (action != "add") workbench.Workbench.Sessions.Add(second);
        var secondWrapper = action == "add" ? null : collection[1];
        var properties = new List<string?>();
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
        collection.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        collection.CollectionChanged += (_, args) => events.Add(args);
        try
        {
            if (action == "add") workbench.Workbench.Sessions.Add(second);
            else if (action == "move") workbench.Workbench.Sessions.Move(1, 0);
            else if (action == "replace") workbench.Workbench.Sessions[1] = replacement;
            else if (action == "remove") workbench.Workbench.Sessions.Remove(second);
            else { workbench.Workbench.SelectedSession = null; workbench.Workbench.Sessions.Clear(); }
            Assert.Equal(action == "move" ? new[] { "Item[]" } : new[] { "Count", "Item[]" }, properties);
            var observed = Assert.Single(events);
            Assert.Equal(action switch
            {
                "add" => System.Collections.Specialized.NotifyCollectionChangedAction.Add,
                "move" => System.Collections.Specialized.NotifyCollectionChangedAction.Move,
                "replace" => System.Collections.Specialized.NotifyCollectionChangedAction.Replace,
                "remove" => System.Collections.Specialized.NotifyCollectionChangedAction.Remove,
                _ => System.Collections.Specialized.NotifyCollectionChangedAction.Reset
            }, observed.Action);
            if (action is "add" or "move")
            {
                Assert.Equal(action == "add" ? 1 : 0, observed.NewStartingIndex);
                Assert.Same(collection[action == "add" ? 1 : 0], Assert.Single(observed.NewItems!.Cast<object>()));
                Assert.Same(firstWrapper, collection[action == "add" ? 0 : 1]);
                if (action == "move")
                {
                    Assert.Equal(1, observed.OldStartingIndex);
                    Assert.Same(secondWrapper, collection[0]);
                }
            }
            else if (action is "replace" or "remove")
            {
                Assert.Equal(1, observed.OldStartingIndex);
                Assert.Throws<ObjectDisposedException>(() => secondWrapper!.DisplayName);
                Assert.Same(firstWrapper, collection[0]);
                if (action == "replace") Assert.Same(collection[1], Assert.Single(observed.NewItems!.Cast<object>()));
                else Assert.Single(collection);
                Assert.Same(secondWrapper, Assert.Single(observed.OldItems!.Cast<object>()));
            }
            else
            {
                Assert.Empty(collection);
                Assert.Throws<ObjectDisposedException>(() => firstWrapper.DisplayName);
                Assert.Throws<ObjectDisposedException>(() => secondWrapper!.DisplayName);
                Assert.Equal(SessionState.Ready, first.Engine.State);
                workbench.Workbench.Sessions.Add(first);
                workbench.SelectSession(first);
                Assert.NotSame(firstWrapper, Assert.Single(collection));
            }
        }
        finally
        {
            if (workbench.Workbench.Sessions.Contains(second)) workbench.Workbench.Sessions.Remove(second);
            if (workbench.Workbench.Sessions.Contains(replacement)) workbench.Workbench.Sessions.Remove(replacement);
            await second.Engine.DisposeAsync();
            await replacement.Engine.DisposeAsync();
            if (!workbench.Workbench.Sessions.Contains(first)) { workbench.Workbench.Sessions.Add(first); workbench.SelectSession(first); }
        }
    });

    [AvaloniaFact]
    public async Task RepeatedSelectedSessionPublishesExactRootSelectionPropertiesAndNullSelectionDoesNotDetachOwnedFiles()
    {
        await InitializeRuntimeAsync();
        await RootSelectionNotificationsCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task RootSelectionNotificationsCoreAsync() => WithModuleWorkbenchAsync(false, (workbench, _) =>
    {
        var root = workbench.Scripting;
        var session = workbench.Workbench.SelectedSession!;
        var file = root.CurrentFile!;
        var editor = file.Editor;
        var properties = new List<string?>();
        root.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        for (var repetition = 0; repetition < 2; repetition++)
        {
            properties.Clear();
            workbench.Workbench.SelectedSession = session;
            Assert.Equal(new[] { "CurrentPowerShellTab", "CurrentFile", "CurrentEditor" }, properties);
            Assert.Same(file, root.CurrentFile);
            Assert.Same(editor, root.CurrentEditor);
        }
        properties.Clear();
        workbench.Workbench.SelectedSession = null;
        Assert.Equal(new[] { "CurrentPowerShellTab", "CurrentFile", "CurrentEditor" }, properties);
        Assert.Equal("No PowerShell tab is selected.", Assert.Throws<InvalidOperationException>(() => root.CurrentPowerShellTab).Message);
        workbench.SelectSession(session);
        Assert.Same(file, root.CurrentFile);
        Assert.Same(editor, root.CurrentEditor);
        Assert.Equal("", editor.Text);
        return Task.CompletedTask;
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledPsIseFoldToggleMatchesDirectWrapperOnSameEngineAndActualSections(bool fromScript)
    {
        await InitializeRuntimeAsync();
        await ScriptFoldCompositionCoreAsync(fromScript);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ScriptFoldCompositionCoreAsync(bool fromScript) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var session = workbench.Workbench.SelectedSession!;
        var engine = session.Engine;
        var runspace = engine.LocalRunspaceId;
        var wrapper = workbench.Scripting.CurrentEditor!;
        wrapper.Text = "{\n$x\n}\n{\n$y\n}";
        await workbench.ScriptEditorView.AnalyzeAsync();
        var manager = Assert.Single(workbench.ScriptEditorView.TextEditor.TextArea.TextView.ElementGenerators
            .OfType<AvaloniaEdit.Folding.FoldingElementGenerator>()).FoldingManager!;
        Assert.Equal(new[] { (0, 6), (7, 13) }, manager.AllFoldings.Select(fold => (fold.StartOffset, fold.EndOffset)).ToArray());
        if (fromScript) await workbench.ExecuteAsync("$psISE.CurrentEditor.ToggleOutliningExpansion()");
        else wrapper.ToggleOutliningExpansion();
        Assert.All(manager.AllFoldings, fold => Assert.True(fold.IsFolded));
        if (fromScript) await workbench.ExecuteAsync("$psISE.CurrentEditor.ToggleOutliningExpansion()");
        else wrapper.ToggleOutliningExpansion();
        Assert.All(manager.AllFoldings, fold => Assert.False(fold.IsFolded));
        Assert.Same(wrapper, workbench.Scripting.CurrentEditor);
        Assert.Equal("{\n$x\n}\n{\n$y\n}", wrapper.Text);
        Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
        Assert.Equal(runspace, engine.LocalRunspaceId);
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedOwnedPaletteDrivesActualScriptAndConsoleTokenRenderingAndHighContrast(bool highContrast)
    {
        await InitializeRuntimeAsync();
        await PersistedPaletteRenderingCoreAsync(highContrast);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task PersistedPaletteRenderingCoreAsync(bool highContrast)
    {
        using var environment = new IseTestEnvironment();
        var path = Path.Combine(environment.DirectoryPath, "palette.json");
        var theme = new EditorTheme { Name = "Owned persisted palette" };
        theme.Colors["Script.Variable"] = "#13579B";
        theme.Colors["Console.Variable"] = "#2468AC";
        await new UserSettings
        {
            Theme = theme, AutoSaveMinutes = 0, CheckForUpdates = false, LoadProfiles = false, ShowCommands = false
        }.SaveAsync(path);
        var before = await File.ReadAllBytesAsync(path);
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            EnablePersistence = true, SettingsPath = path, SnippetDirectory = Path.Combine(environment.DirectoryPath, "palette-catalog"),
            StartingDirectory = environment.DirectoryPath,
            Preferences = new UserSettings { AutoSaveMinutes = 0, CheckForUpdates = false, LoadProfiles = false, ShowCommands = false }
        });
        var owner = new Window { Content = workbench, Width = 1100, Height = 800 };
        var previous = DesktopTheme.HighContrast;
        try
        {
            DesktopTheme.Refresh(highContrast: false);
            owner.Show();
            await workbench.InitializeAsync();
            var session = Assert.Single(workbench.Workbench.Sessions);
            var runspace = session.Engine.LocalRunspaceId;
            var script = workbench.ScriptEditorView;
            script.Document.Text = "$script = 42";
            await script.AnalyzeAsync();
            session.Input = "$console = 17";
            var console = workbench.FindControl<AvaloniaEdit.TextEditor>("ConsoleEditor")!;
            owner.UpdateLayout();
            await WaitForAsync(() => Phase2RenderedColorAt(console, session.Console.InputStart) ==
                    Avalonia.Media.Color.Parse("#2468AC"),
                () => "Console analysis did not apply the persisted variable palette.");
            Assert.Equal("Owned persisted palette", workbench.GetScriptingSettings().Theme.Name);
            Assert.Equal(Avalonia.Media.Color.Parse("#13579B"), Phase2RenderedColorAt(script.TextEditor, 0));
            Assert.Equal(Avalonia.Media.Color.Parse("#2468AC"), Phase2RenderedColorAt(console, session.Console.InputStart));
            var input = session.Input;
            var history = session.History.ToArray();
            DesktopTheme.Refresh(highContrast: highContrast);
            script.TextEditor.TextArea.TextView.Redraw();
            console.TextArea.TextView.Redraw();
            if (highContrast)
            {
                Assert.NotEqual(Avalonia.Media.Color.Parse("#13579B"), Phase2RenderedColorAt(script.TextEditor, 0));
                Assert.NotEqual(Avalonia.Media.Color.Parse("#2468AC"), Phase2RenderedColorAt(console, session.Console.InputStart));
            }
            else
            {
                Assert.Equal(Avalonia.Media.Color.Parse("#13579B"), Phase2RenderedColorAt(script.TextEditor, 0));
                Assert.Equal(Avalonia.Media.Color.Parse("#2468AC"), Phase2RenderedColorAt(console, session.Console.InputStart));
            }
            Assert.Equal("$script = 42", script.Document.Text);
            Assert.Equal(input, session.Input);
            Assert.Equal(history, session.History);
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            try { await workbench.DisposeAsync(); }
            finally { owner.Content = null; owner.Close(); DesktopTheme.Refresh(highContrast: previous); }
        }
    }

    private static Avalonia.Media.Color Phase2RenderedColorAt(AvaloniaEdit.TextEditor editor, int offset)
    {
        editor.UpdateLayout();
        var view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        var line = view.VisualLines.Single(line => line.FirstDocumentLine.Offset <= offset && line.LastDocumentLine.EndOffset >= offset);
        var element = line.Elements.Single(element => line.FirstDocumentLine.Offset + element.RelativeTextOffset <= offset &&
            line.FirstDocumentLine.Offset + element.RelativeTextOffset + element.DocumentLength > offset);
        return Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(element.TextRunProperties.ForegroundBrush).Color;
    }

    [AvaloniaTheory]
    [InlineData(null, 0, "$owned = 41  [Int32]")]
    [InlineData(null, 12, "$next: Variable is not available in the selected debugger scope.")]
    [InlineData("$global:owned", 1, "$global:owned: Scoped/provider variables cannot be resolved from the selected frame's variable snapshot.")]
    [InlineData("$env:Phase2Owned", 1, "$env:Phase2Owned: Scoped/provider variables cannot be resolved from the selected frame's variable snapshot.")]
    [InlineData("$OwNeD", 1, "$owned = 41  [Int32]")]
    public async Task ActualPausedHoverPinsMissingScopedCaseInsensitiveAndOtherStatementPolicy(
        string? replacement, int offset, string expected)
    {
        await InitializeRuntimeAsync();
        await PausedHoverPolicyCoreAsync(replacement, offset, expected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task PausedHoverPolicyCoreAsync(string? replacement, int offset, string expected) =>
        WithPhase2PausedScriptAsync(async (workbench, owner, session, _) =>
        {
            var runspace = session.Engine.LocalRunspaceId;
            var snapshot = session.DebugSnapshot!;
            var editor = workbench.ScriptEditorView;
            if (replacement is not null)
            {
                editor.Document.Text = replacement;
                await editor.AnalyzeAsync();
            }
            var text = editor.Document.Text;
            RaisePhase2Hover(owner, workbench, offset);
            await WaitForAsync(() => ToolTip.GetIsOpen(editor.TextEditor), () => "Actual variable policy hover did not open.");
            Assert.Equal(expected, Assert.IsType<TextBlock>(ToolTip.GetTip(editor.TextEditor)).Text);
            Assert.Same(snapshot, session.DebugSnapshot);
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);
            Assert.True(session.Engine.IsDebuggerPaused);
            Assert.Equal(text, editor.Document.Text);
            RaisePhase2Hover(owner, workbench, offset, stopped: true);
            Assert.False(ToolTip.GetIsOpen(editor.TextEditor));
            Assert.Null(ToolTip.GetTip(editor.TextEditor));
        });

    [AvaloniaTheory]
    [InlineData(19, null)]
    [InlineData(20, "You must provide a value expression following the '=' operator.")]
    [InlineData(21, "Unexpected token ')' in expression or statement.")]
    [InlineData(22, null)]
    public async Task ActualDiagnosticHoverHonorsZeroLengthAndTokenBoundariesAndCorrectionClearsTip(
        int offset, string? expected)
    {
        await InitializeRuntimeAsync();
        await DiagnosticHoverCoreAsync(offset, expected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task DiagnosticHoverCoreAsync(int offset, string? expected) =>
        WithModuleWorkbenchAsync(false, async (workbench, _) =>
        {
            var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
            var editor = workbench.ScriptEditorView;
            var session = workbench.Workbench.SelectedSession!;
            var runspace = session.Engine.LocalRunspaceId;
            Assert.False(session.Engine.IsDebuggerPaused);
            editor.Document.Text = "Write-Output ok\n$x = )\nWrite-Output end";
            await editor.AnalyzeAsync();
            RaisePhase2Hover(owner, workbench, 21);
            Assert.True(ToolTip.GetIsOpen(editor.TextEditor));
            Assert.Equal("Unexpected token ')' in expression or statement.",
                Assert.IsType<TextBlock>(ToolTip.GetTip(editor.TextEditor)).Text);
            RaisePhase2Hover(owner, workbench, offset, allowUnmappedLineEnd: offset == 22);
            if (expected is null)
            {
                Assert.False(ToolTip.GetIsOpen(editor.TextEditor));
                Assert.Null(ToolTip.GetTip(editor.TextEditor));
            }
            else
            {
                Assert.True(ToolTip.GetIsOpen(editor.TextEditor));
                Assert.Equal(expected, Assert.IsType<TextBlock>(ToolTip.GetTip(editor.TextEditor)).Text);
            }
            editor.Document.Text = "Write-Output ok\n$x = 1\nWrite-Output end";
            await editor.AnalyzeAsync();
            Assert.False(ToolTip.GetIsOpen(editor.TextEditor));
            Assert.Null(ToolTip.GetTip(editor.TextEditor));
            Assert.Empty(editor.Analysis.Diagnostics);
            Assert.Equal("Write-Output ok\n$x = 1\nWrite-Output end", editor.Document.Text);
            Assert.Equal(runspace, session.Engine.LocalRunspaceId);
        });

    [AvaloniaTheory]
    [InlineData("insert-at-anchor")]
    [InlineData("delete-anchor-line")]
    [InlineData("delete-characters-only")]
    [InlineData("toggle")]
    [InlineData("change-after-move")]
    [InlineData("stale-change-after-acknowledge")]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("reload")]
    [InlineData("invalid-kind")]
    [InlineData("invalid-zero")]
    [InlineData("invalid-past-end")]
    public async Task PublicScriptTabBreakpointAnchorsTrackEditsReloadAndEngineChangesWithoutLosingMetadata(string action)
    {
        await InitializeRuntimeAsync();
        ScriptTabAnchorsCore(action);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ScriptTabAnchorsCore(string action)
    {
        const string original = "one\ntwo\nthree\nfour\nfive";
        var file = new ScriptFile("Owned anchor.ps1") { Text = original };
        var tab = new ScriptTab(file);
        var first = new BreakpointSpec(BreakpointKind.Line, Line: 2, Condition: "$owned -gt 0", Enabled: false);
        var second = new BreakpointSpec(BreakpointKind.Line, Line: 4, Action: "Write-Output 'owned anchor'");
        tab.SetBreakpoint(first);
        tab.SetBreakpoint(second);
        Assert.Equal(new[] { first, second }, tab.LineBreakpoints);
        Assert.Equal(new[] { 2, 4 }, file.Breakpoints.Order().ToArray());
        var expectedText = original;
        BreakpointSpec[] expected = [first, second];
        if (action == "insert-at-anchor")
        {
            tab.Document.Insert(4, "#\n");
            expectedText = "one\n#\ntwo\nthree\nfour\nfive";
            expected = [first with { Line = 3 }, second with { Line = 5 }];
        }
        else if (action == "delete-anchor-line")
        {
            tab.Document.Remove(4, 4);
            expectedText = "one\nthree\nfour\nfive";
            expected = [second with { Line = 3 }];
        }
        else if (action == "delete-characters-only")
        {
            tab.Document.Remove(4, 3);
            expectedText = "one\n\nthree\nfour\nfive";
        }
        else if (action == "toggle")
        {
            tab.ToggleBreakpoint(2);
            Assert.Equal(new[] { second }, tab.LineBreakpoints);
            Assert.Equal(new[] { 4 }, file.Breakpoints.ToArray());
            tab.ToggleBreakpoint(2);
            expected = [second, new(BreakpointKind.Line, Line: 2)];
        }
        else if (action == "change-after-move")
        {
            tab.Document.Insert(0, "#\n");
            tab.ApplyBreakpointChange(first, first with { Enabled = true, Condition = "$owned -eq 99" });
            expectedText = "#\n" + original;
            expected = [first with { Line = 3, Enabled = true, Condition = "$owned -eq 99" }, second with { Line = 5 }];
        }
        else if (action == "stale-change-after-acknowledge")
        {
            tab.Document.Insert(0, "#\n");
            tab.AcknowledgeBreakpointLines();
            tab.ApplyBreakpointChange(first, null);
            Assert.Equal(new[] { first with { Line = 3 }, second with { Line = 5 } }, tab.LineBreakpoints);
            tab.ApplyBreakpointChange(first with { Line = 3 }, null);
            expectedText = "#\n" + original;
            expected = [second with { Line = 5 }];
        }
        else if (action == "replace")
        {
            tab.ReplaceBreakpoints([first with { Line = 0 }, second with { Line = 6 },
                first with { Line = 3 }, second with { Line = 3 }]);
            expected = [second with { Line = 3 }];
        }
        else if (action == "clear")
        {
            tab.ClearBreakpoints();
            tab.Document.Insert(0, "#\n");
            expectedText = "#\n" + original;
            expected = [];
        }
        else if (action == "reload")
        {
            tab.Document.Insert(0, "#\n");
            Assert.True(tab.Document.UndoStack.CanUndo);
            file.CaretOffset = 30;
            var replacement = new ScriptFile("Reloaded anchor.ps1") { Text = "a\nb\nc" };
            var notifications = new List<string?>();
            tab.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            tab.Reload(replacement);
            Assert.Same(replacement, tab.File);
            Assert.Equal("#\n" + original, file.Text);
            Assert.Equal(5, replacement.CaretOffset);
            Assert.False(tab.Document.UndoStack.CanUndo);
            Assert.False(tab.Document.UndoStack.CanRedo);
            Assert.Equal(new[] { "File" }, notifications);
            file = replacement;
            expectedText = "a\nb\nc";
            expected = [first with { Line = 3 }];
        }
        else
        {
            var invalid = action == "invalid-kind" ? first with { Kind = BreakpointKind.Command }
                : first with { Line = action == "invalid-zero" ? 0 : 6 };
            Assert.Equal("The breakpoint must refer to a line in this document.",
                Assert.Throws<ArgumentException>(() => tab.SetBreakpoint(invalid)).Message);
        }
        Assert.Equal(expectedText, tab.Document.Text);
        Assert.Equal(expectedText, file.Text);
        Assert.Equal(expected, tab.LineBreakpoints);
        Assert.Equal(expected.Select(spec => spec.Line).Order().ToArray(), file.Breakpoints.Order().ToArray());
        Assert.Same(file, tab.File);
    }

    [AvaloniaTheory]
    [InlineData("pick-and-run")]
    [InlineData("pick-owned-module")]
    [InlineData("picker-cancel")]
    [InlineData("picker-empty")]
    [InlineData("picker-close")]
    public async Task InRunspaceShowCommandWithoutNameUsesActualOwnedPickerFilterAcceptAndCancellation(string action)
    {
        await InitializeRuntimeAsync();
        await InRunspaceShowCommandPickerCoreAsync(action);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task InRunspaceShowCommandPickerCoreAsync(string action) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var runspace = engine.LocalRunspaceId;
        var owner = Assert.IsType<Window>(TopLevel.GetTopLevel(workbench));
        var output = new System.Collections.Concurrent.ConcurrentQueue<OutputEntry>();
        engine.Output += output.Enqueue;
        Task? request = null;
        var accept = action is "pick-and-run" or "pick-owned-module";
        try
        {
            await workbench.ExecuteAsync(action == "pick-owned-module" ? """
                $global:phase2PickerCalls = 0
                New-Module -Name Phase2OwnedPicker -ScriptBlock {
                    function Invoke-Phase2Picked {
                        $global:phase2PickerCalls++
                        'OWNED-PICK:' + $global:phase2PickerCalls
                    }
                    Export-ModuleMember -Function Invoke-Phase2Picked
                } | Import-Module
                """ : """
                $global:phase2PickerCalls = 0
                function global:Invoke-Phase2Picked {
                    $global:phase2PickerCalls++
                    'OWNED-PICK:' + $global:phase2PickerCalls
                }
                """);
            var editor = workbench.ScriptEditorView;
            editor.Document.Text = "owned picker unchanged";
            editor.Select(new(5, 6));
            output.Clear();
            request = workbench.ExecuteAsync("Show-Command -NoCommonParameter");
            await WaitForAsync(() => owner.OwnedWindows.OfType<ShowCommandPickerWindow>().Any() || request.IsCompleted,
                () => "Show-Command without Name did not show the actual owned picker.");
            Assert.False(request.IsCompleted);
            var picker = Assert.Single(owner.OwnedWindows.OfType<ShowCommandPickerWindow>());
            picker.FindControl<TextBox>("ShowCommandSearch")!.Text = action == "picker-empty"
                ? "NoSuchPhase2OwnedPickerCommand" : "invoke-phase2picked";
            picker.UpdateLayout();
            var list = picker.FindControl<ListBox>("ShowCommandList")!;
            var select = picker.FindControl<Button>("ShowCommandSelect")!;
            if (action == "picker-empty")
            {
                await WaitForAsync(() => list.ItemCount == 0, () => "The owned no-match search did not empty the picker.");
                Assert.Null(list.SelectedItem);
                Assert.False(select.IsEnabled);
                Assert.False(request.IsCompleted);
            }
            else
            {
                await WaitForAsync(() => list.ItemCount == 1, () => "The case-insensitive owned function search did not isolate one command.");
                var command = Assert.IsType<CommandDescription>(Assert.Single(list.Items));
                Assert.Equal("Invoke-Phase2Picked", command.Name);
                Assert.Equal(action == "pick-owned-module" ? "Phase2OwnedPicker" : "", command.Module);
                Assert.Same(command, list.SelectedItem);
                Assert.True(select.IsEnabled);
                if (action == "pick-owned-module")
                {
                    var modules = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(picker).OfType<ComboBox>());
                    modules.SelectedItem = "Phase2OwnedPicker";
                    Assert.Same(command, Assert.Single(list.Items));
                }
            }
            if (accept) ClickOwnedButton(select);
            else if (action == "picker-close") picker.Close();
            else ClickCancel(picker);
            if (accept)
            {
                await WaitForAsync(() => owner.OwnedWindows.OfType<ShowCommandWindow>().Any() || request.IsCompleted,
                    () => "Owned picker selection did not reach the actual in-runspace form.");
                Assert.False(request.IsCompleted);
                var form = Assert.Single(owner.OwnedWindows.OfType<ShowCommandWindow>());
                var view = form.FindControl<CommandFormView>("ShowCommandForm")!;
                Assert.Equal("Invoke-Phase2Picked", view.Form!.Description.Name);
                await WaitForAsync(() => view.Result is { IsValid: true },
                    () => "The picked command did not finish validating its form.");
                Assert.Equal(action == "pick-owned-module" ? "& 'Phase2OwnedPicker\\Invoke-Phase2Picked'" : "& 'Invoke-Phase2Picked'",
                    view.GetCommand());
                Assert.True(view.Result!.IsValid);
                ClickOwnedButton(form.FindControl<Button>("ShowCommandRun")!);
            }
            await request.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Empty(owner.OwnedWindows);
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            Assert.Equal(accept ? new[] { "OWNED-PICK:1" + Environment.NewLine } : [],
                output.Where(entry => entry.Kind == OutputKind.Output).Select(entry => entry.Text).ToArray());
            Assert.Equal("owned picker unchanged", editor.Document.Text);
            Assert.Equal(new Iseberg.Editor.EditorTextSpan(5, 6), editor.Selection);
            Assert.Equal(11, editor.CaretOffset);
            output.Clear();
            await workbench.ExecuteAsync("'PICKER-CALLS:' + $global:phase2PickerCalls");
            Assert.Contains(output, entry => entry.Kind == OutputKind.Output &&
                entry.Text == "PICKER-CALLS:" + (accept ? "1" : "0") + Environment.NewLine);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
            Assert.Equal(SessionState.Ready, engine.State);
        }
        finally
        {
            foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close();
            if (request is { IsCompleted: false }) { await engine.StopAsync(); await request; }
            engine.Output -= output.Enqueue;
        }
    });

    [AvaloniaTheory]
    [InlineData("single")]
    [InlineData("many-duplicates")]
    [InlineData("all-existing")]
    [InlineData("cancel")]
    [InlineData("empty")]
    [InlineData("nonlocal")]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("invalid-batch")]
    public async Task ActualImportSnippetPickerUsesOwnedWindowFeatureAndDurablyPublishesOnlyValidDistinctBatch(string action)
    {
        await InitializeRuntimeAsync();
        await ImportSnippetPickerCoreAsync(action);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ImportSnippetPickerCoreAsync(string action)
    {
        using var environment = new IseTestEnvironment();
        var root = environment.DirectoryPath;
        var catalog = Path.Combine(root, "catalog");
        var picker = System.Reflection.DispatchProxy.Create<Avalonia.Platform.Storage.IStorageProvider, Phase2PickerStorage>();
        var pickerState = (Phase2PickerStorage)(object)picker;
        var platform = System.Reflection.DispatchProxy.Create<Avalonia.Platform.IWindowImpl, Phase2OwnedWindowPlatform>();
        var platformState = (Phase2OwnedWindowPlatform)(object)platform;
        var backing = new Window();
        platformState.Inner = backing.PlatformImpl!;
        platformState.Storage = picker;
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            SnippetDirectory = catalog, StartingDirectory = root,
            Preferences = new UserSettings { AutoSaveMinutes = 0, CheckForUpdates = false, LoadProfiles = false, ShowCommands = false }
        });
        var owner = new Window(platform) { Content = workbench, Width = 1000, Height = 750 };
        var errors = new List<WorkbenchErrorEventArgs>();
        workbench.ErrorOccurred += (_, error) => errors.Add(error);
        FileStream? unreadable = null;
        try
        {
            owner.Show();
            await workbench.InitializeAsync();
            Assert.Same(picker, owner.StorageProvider);
            var engine = workbench.Workbench.SelectedSession!.Engine;
            var runspace = engine.LocalRunspaceId;
            var baseline = new PowerShellSnippet("Kept", "Baseline description", "baseline author", "baseline code");
            await workbench.SaveSnippetsAsync([baseline]);
            var baselinePath = Assert.Single(engine.Snippets.GetUserFiles()).FullName;
            var baselineBytes = await File.ReadAllBytesAsync(baselinePath);
            var first = new PowerShellSnippet("First & title", "First description", "Author β", "$owned = '<&>'\n", 5, false);
            var second = new PowerShellSnippet("Second", "Second description", "Author café", "Write-Output café");
            var firstPath = Path.Combine(root, "first.SNIPPETS.PS1XML");
            var secondPath = Path.Combine(root, "second.snippets.ps1xml");
            var invalidPath = Path.Combine(root, "invalid.snippets.ps1xml");
            await File.WriteAllTextAsync(firstPath,
                "<Snippets xmlns=\"http://schemas.microsoft.com/PowerShell/Snippets\"><Snippet Version=\"1.0.0\">" +
                "<Header><Title>First &amp; title</Title><Description>First description</Description><Author>Author β</Author></Header>" +
                "<Code><Script Language=\"PowerShell\" CaretOffset=\"5\" Indent=\"false\">$owned = '&lt;&amp;&gt;'\n</Script></Code></Snippet></Snippets>");
            await File.WriteAllTextAsync(secondPath,
                "<Snippets xmlns=\"http://schemas.microsoft.com/PowerShell/Snippets\"><Snippet Version=\"1.0.0\">" +
                "<Header><Title>Second</Title><Description>Second description</Description><Author>Author café</Author></Header>" +
                "<Code><Script Language=\"PowerShell\">Write-Output café</Script></Code></Snippet></Snippets>");
            await File.WriteAllTextAsync(invalidPath, "<not-a-snippet/>");
            var sourceBytes = await File.ReadAllBytesAsync(firstPath);
            var firstFile = Phase2StorageFileFor(new Uri(firstPath));
            var secondFile = Phase2StorageFileFor(new Uri(secondPath));
            IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> response = action switch
            {
                "single" or "unreadable" => [firstFile],
                "many-duplicates" => [firstFile, secondFile, firstFile],
                "all-existing" => [Phase2StorageFileFor(new Uri(baselinePath))],
                "nonlocal" => [Phase2StorageFileFor(new Uri("urn:phase2:owned-nonlocal"))],
                "missing" => [Phase2StorageFileFor(new Uri(Path.Combine(root, "missing.snippets.ps1xml")))],
                "invalid-batch" => [firstFile, Phase2StorageFileFor(new Uri(invalidPath))],
                _ => []
            };
            if (action == "unreadable") unreadable = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var editor = workbench.ScriptEditorView;
            editor.Document.Text = "before selected after";
            editor.Select(new(7, 8));
            var status = workbench.FindControl<TextBlock>("StatusText")!;
            var announced = new List<string?>();
            status.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBlock.TextProperty) announced.Add(status.Text);
            };
            var expectedAdditions = action == "single" ? 1 : action == "many-duplicates" ? 2 : 0;
            var expectedStatus = string.Format(UiText.Get("SnippetsImported"), expectedAdditions);
            if (action == "empty") pickerState.Response.SetResult(response);
            ClickPhase2Action(workbench, "ImportSnippets");
            await WaitForAsync(() => pickerState.Calls == 1, () => "Actual ImportSnippets did not call the owned window picker.");
            Assert.Equal(UiText.Get("ImportSnippets"), pickerState.Options!.Title);
            Assert.True(pickerState.Options.AllowMultiple);
            Assert.Equal(new[] { "*.snippets.ps1xml" }, Assert.Single(pickerState.Options.FileTypeFilter!).Patterns);
            Assert.Equal(baselineBytes, await File.ReadAllBytesAsync(baselinePath));
            if (action != "empty") pickerState.Response.SetResult(response);
            var failure = action is "nonlocal" or "missing" or "unreadable" or "invalid-batch";
            if (failure) await WaitForAsync(() => errors.Count == 1, () => "Import failure was not publicly reported.");
            else if (action is not ("cancel" or "empty"))
                await WaitForAsync(() => announced.Contains(expectedStatus), () => "Import never announced complete durable publication.");
            else
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            if (failure)
            {
                var error = Assert.Single(errors);
                Assert.Equal("Operation failed", error.Title);
                if (action == "missing") Assert.Equal(Path.Combine(root, "missing.snippets.ps1xml"),
                    Assert.IsType<FileNotFoundException>(error.Exception).FileName);
                else if (action == "invalid-batch") Assert.IsType<InvalidDataException>(error.Exception);
                else Assert.IsType<IOException>(error.Exception);
                if (action == "nonlocal") Assert.Equal("Only local snippet files are supported.", error.Exception.Message);
                if (action == "invalid-batch") Assert.Equal("Expected a PowerShell Snippets document.", error.Exception.Message);
                Assert.Single(announced, text => text == "Operation failed: " + error.Exception.Message);
            }
            else Assert.Empty(errors);
            var independent = await new IseSnippetService(catalog).LoadAsync();
            Assert.Empty(independent.Errors);
            var expected = action == "single" ? new[] { baseline, first } :
                action == "many-duplicates" ? new[] { baseline, first, second } : [baseline];
            Assert.Equal(expected.OrderBy(snippet => snippet.Title, StringComparer.Ordinal),
                independent.Snippets.OrderBy(snippet => snippet.Title, StringComparer.Ordinal));
            var sameEngine = await engine.Snippets.LoadAsync();
            Assert.Equal(expected.OrderBy(snippet => snippet.Title, StringComparer.Ordinal),
                sameEngine.Snippets.OrderBy(snippet => snippet.Title, StringComparer.Ordinal));
            // SaveSnippetsAsync atomically publishes one GUID-named file for the entire added batch.
            Assert.Equal(expectedAdditions > 0 ? 2 : 1, Directory.GetFiles(catalog, "*.snippets.ps1xml").Length);
            Assert.Equal(baselineBytes, await File.ReadAllBytesAsync(baselinePath));
            Assert.Equal("before selected after", editor.Document.Text);
            Assert.Equal(new Iseberg.Editor.EditorTextSpan(7, 8), editor.Selection);
            Assert.Equal(15, editor.CaretOffset);
            Assert.Equal(runspace, engine.LocalRunspaceId);
            Assert.Same(engine, Assert.Single(workbench.Workbench.Sessions).Engine);
            Assert.Empty(owner.OwnedWindows);
            Assert.Equal(1, pickerState.Calls);
            Assert.Equal(0, ((Phase2OwnedStorageFile)(object)firstFile).ReadCalls);
            unreadable?.Dispose();
            unreadable = null;
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(firstPath));
            if (action is "cancel" or "empty") Assert.Empty(announced);
            if (expectedAdditions > 0)
            {
                var reopened = new WorkbenchControl(new WorkbenchOptions
                {
                    SnippetDirectory = catalog, StartingDirectory = root,
                    Preferences = new UserSettings { AutoSaveMinutes = 0, CheckForUpdates = false, LoadProfiles = false, ShowCommands = false }
                });
                var reopenedOwner = new Window { Content = reopened, Width = 900, Height = 700 };
                try
                {
                    reopenedOwner.Show();
                    await reopened.InitializeAsync();
                    Assert.NotEqual(runspace, reopened.Workbench.SelectedSession!.Engine.LocalRunspaceId);
                    Assert.Equal(expected.OrderBy(snippet => snippet.Title, StringComparer.Ordinal),
                        reopened.Scripting.CurrentPowerShellTab.Snippets.Where(snippet => !snippet.IsBuiltIn)
                            .Select(snippet => new PowerShellSnippet(snippet.Title, snippet.Description, snippet.Author,
                                snippet.Code, snippet.CaretOffset, snippet.Indent))
                            .OrderBy(snippet => snippet.Title, StringComparer.Ordinal));
                    Assert.Equal(baselineBytes, await File.ReadAllBytesAsync(baselinePath));
                    Assert.Equal(expectedAdditions > 0 ? 2 : 1, Directory.GetFiles(catalog, "*.snippets.ps1xml").Length);
                }
                finally
                {
                    try { await reopened.DisposeAsync(); }
                    finally { reopenedOwner.Content = null; reopenedOwner.Close(); }
                }
            }
        }
        finally
        {
            unreadable?.Dispose();
            // Complete a pending owned picker if a fixture assertion prevented its response.
            pickerState.Response.TrySetResult([]);
            try { await workbench.DisposeAsync(); }
            finally { owner.Content = null; owner.Close(); backing.Close(); }
        }
    }

    private static Avalonia.Platform.Storage.IStorageFile Phase2StorageFileFor(Uri path)
    {
        var file = System.Reflection.DispatchProxy.Create<Avalonia.Platform.Storage.IStorageFile, Phase2OwnedStorageFile>();
        ((Phase2OwnedStorageFile)(object)file).OwnedPath = path;
        return file;
    }

    // These proxies invoke only public platform-interface methods. They decorate one newly
    // allocated headless window, never replace a locator/service or inspect private members.
    public class Phase2OwnedWindowPlatform : System.Reflection.DispatchProxy
    {
        public Avalonia.Platform.IWindowImpl Inner { get; set; } = null!;
        public Avalonia.Platform.Storage.IStorageProvider Storage { get; set; } = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException("A public platform method is required.");
            if (!method.IsPublic || method.DeclaringType?.IsInterface != true)
                throw new InvalidOperationException("Only public platform-interface dispatch is permitted.");
            if (method.Name == "TryGetFeature" && args![0] is Type type && type == typeof(Avalonia.Platform.Storage.IStorageProvider))
                return Storage;
            return method.Invoke(Inner, args);
        }
    }

    public class Phase2PickerStorage : System.Reflection.DispatchProxy
    {
        public TaskCompletionSource<IReadOnlyList<Avalonia.Platform.Storage.IStorageFile>> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Avalonia.Platform.Storage.FilePickerOpenOptions? Options { get; private set; }
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_CanOpen") return true;
            if (targetMethod.Name is "get_CanSave" or "get_CanPickFolder") return false;
            if (targetMethod.Name == "OpenFilePickerAsync")
            {
                Calls++;
                Options = (Avalonia.Platform.Storage.FilePickerOpenOptions)args![0]!;
                return Response.Task;
            }
            throw new NotSupportedException("Unexpected owned picker operation: " + targetMethod.Name);
        }
    }

    public class Phase2OwnedStorageFile : System.Reflection.DispatchProxy
    {
        public Uri OwnedPath { get; set; } = null!;
        public int ReadCalls { get; private set; }
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_Path") return OwnedPath;
            if (targetMethod.Name == "get_Name") return "owned.snippets.ps1xml";
            if (targetMethod.Name == "get_CanBookmark") return false;
            if (targetMethod.Name == "Dispose") return null;
            if (targetMethod.Name == "OpenReadAsync") ReadCalls++;
            throw new NotSupportedException("Unexpected owned storage-file operation: " + targetMethod.Name);
        }
    }

    [AvaloniaTheory]
    [InlineData(0, 6d, 1)]
    [InlineData(0, 72d, 30)]
    [InlineData(1, 6d, 1)]
    [InlineData(1, 72d, 30)]
    [InlineData(2, 6d, 1)]
    [InlineData(2, 72d, 30)]
    public async Task OptionsComboBoundariesAndDependentCompletionControlsPublishExactAcceptedSnapshot(
        int layout, double size, int timeout)
    {
        await InitializeRuntimeAsync();
        OptionsComboBoundaryCore(layout, size, timeout);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OptionsComboBoundaryCore(int layout, double size, int timeout)
    {
        UserSettings? accepted = null;
        var source = new UserSettings { AutoSaveMinutes = 0, FontFamily = "Phase3 owned font", FontSize = 28,
            IntelliSenseTimeoutSeconds = 17, Layout = "Right", ConsoleCompletionOnEnter = true, ScriptCompletionOnEnter = true };
        var dialog = new OptionsWindow(source, snapshot => { accepted = snapshot; return Task.CompletedTask; });
        var owner = new Window();
        try
        {
            owner.Show();
            _ = dialog.ShowDialog(owner);
            dialog.FindControl<ComboBox>("EditorFontSize")!.SelectedItem = size;
            dialog.FindControl<ComboBox>("CompletionTimeout")!.SelectedItem = timeout;
            dialog.FindControl<ComboBox>("PanePosition")!.SelectedIndex = layout;
            dialog.FindControl<CheckBox>("ConsoleIntelliSense")!.IsChecked = false;
            dialog.FindControl<CheckBox>("ScriptIntelliSense")!.IsChecked = false;
            Assert.False(dialog.FindControl<CheckBox>("ConsoleEnterSelects")!.IsEnabled);
            Assert.False(dialog.FindControl<CheckBox>("ScriptEnterSelects")!.IsEnabled);
            Assert.True(dialog.Draft.ConsoleCompletionOnEnter);
            Assert.True(dialog.Draft.ScriptCompletionOnEnter); // Disabling completion does not erase its Enter policy.
            dialog.FindControl<Button>("ApplyOptions")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(accepted);
            Assert.Equal(size, accepted.FontSize);
            Assert.Equal(timeout, accepted.IntelliSenseTimeoutSeconds);
            Assert.Equal(new[] { "Top", "Right", "Maximized" }[layout], accepted.Layout);
            Assert.Equal("Phase3 owned font", accepted.FontFamily);
            Assert.False(accepted.ConsoleIntelliSense);
            Assert.False(accepted.ScriptIntelliSense);
            Assert.True(accepted.ConsoleCompletionOnEnter);
            Assert.True(accepted.ScriptCompletionOnEnter);
            Assert.Equal(28, source.FontSize);
            Assert.Equal(17, source.IntelliSenseTimeoutSeconds);
            Assert.True(source.ConsoleIntelliSense);
            Assert.True(source.ScriptIntelliSense);
            dialog.FindControl<CheckBox>("ConsoleIntelliSense")!.IsChecked = true;
            dialog.FindControl<CheckBox>("ScriptIntelliSense")!.IsChecked = true;
            Assert.True(dialog.FindControl<CheckBox>("ConsoleEnterSelects")!.IsEnabled);
            Assert.True(dialog.FindControl<CheckBox>("ScriptEnterSelects")!.IsEnabled);
            dialog.FindControl<Button>("CancelOptions")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(accepted.ConsoleIntelliSense);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task OptionsUnselectedCombosRetainFontAndTimeoutButNormalizePaneToTopWithoutPublishingOnCancel()
    {
        await InitializeRuntimeAsync();
        await OptionsUnselectedComboCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OptionsUnselectedComboCoreAsync()
    {
        var calls = 0;
        var dialog = new OptionsWindow(new UserSettings { FontFamily = "Phase3 owned font", FontSize = 28,
            IntelliSenseTimeoutSeconds = 17, Layout = "Right", AutoSaveMinutes = 0 },
            _ => { calls++; return Task.CompletedTask; });
        var owner = new Window();
        try
        {
            owner.Show();
            _ = dialog.ShowDialog(owner);
            foreach (var name in new[] { "EditorFont", "EditorFontSize", "CompletionTimeout", "PanePosition" })
                dialog.FindControl<ComboBox>(name)!.SelectedIndex = -1;
            Assert.Equal("Phase3 owned font", dialog.Draft.FontFamily);
            Assert.Equal(28, dialog.Draft.FontSize);
            Assert.Equal(17, dialog.Draft.IntelliSenseTimeoutSeconds);
            Assert.Equal("Top", dialog.Draft.Layout);
            dialog.FindControl<CheckBox>("FixedWidthOnly")!.IsChecked = true;
            var fonts = dialog.FindControl<ComboBox>("EditorFont")!;
            await WaitForAsync(() => dialog.Draft.FixedWidthFontsOnly, () => "Fixed-width preference not published.");
            Assert.Equal(Assert.IsType<string>(fonts.SelectedItem), dialog.Draft.FontFamily);
            Assert.Contains(dialog.Draft.FontFamily, fonts.Items.Cast<string>());
            dialog.FindControl<Button>("CancelOptions")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, calls);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("rgb")]
    [InlineData("import")]
    [InlineData("defaults")]
    public async Task HostOwnedOptionsDisabledPaletteRoutesCannotPublishChangedThemeWithOtherwiseValidGeneralEdit(string route)
    {
        await InitializeRuntimeAsync();
        HostOwnedPaletteCore(route);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HostOwnedPaletteCore(string route)
    {
        var source = new UserSettings { ThemePreset = "Monochrome Green", Theme = EditorThemePresets.Original("Monochrome Green"),
            FontSize = 22, FontFamily = "Consolas", AutoSaveMinutes = 0 };
        UserSettings? accepted = null;
        var dialog = new OptionsWindow(source, snapshot => { accepted = snapshot; return Task.CompletedTask; }, hostOwnsTheme: true);
        var owner = new Window();
        try
        {
            owner.Show();
            _ = dialog.ShowDialog(owner);
            Assert.False(dialog.FindControl<TreeView>("ColorTree")!.IsEnabled);
            Assert.False(dialog.FindControl<Button>("ManageThemes")!.IsEnabled);
            if (route == "rgb")
            {
                var tree = dialog.FindControl<TreeView>("ColorTree")!;
                var script = Assert.IsType<TreeViewItem>(tree.Items[0]);
                tree.SelectedItem = script.Items.Cast<TreeViewItem>().Single(item => Equals(item.Tag, "Script.Background"));
                dialog.FindControl<CheckBox>("Hexadecimal")!.IsChecked = false;
                dialog.FindControl<TextBox>("RedValue")!.Text = "255";
            }
            else if (route == "import")
                Assert.Throws<NotSupportedException>(() => dialog.ApplyImportedTheme(
                    new ThemeFile { Theme = new() { Name = "forbidden owned import" }, FontFamily = "Lucida Console", FontSize = 18 }));
            else dialog.FindControl<Button>("RestoreDefaults")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            dialog.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = false;
            dialog.FindControl<Button>("ApplyOptions")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(accepted);
            Assert.False(accepted.ShowLineNumbers);
            Assert.Equal("Monochrome Green", accepted.ThemePreset);
            Assert.Equal("Monochrome Green", accepted.Theme.Name);
            Assert.Equal("#000000", accepted.Theme.Colors["Script.Background"]);
            Assert.Equal("#00FF00", accepted.Theme.Colors["Script.Foreground"]);
            Assert.Equal(source.Theme.Colors.OrderBy(pair => pair.Key), accepted.Theme.Colors.OrderBy(pair => pair.Key));
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("whatif")]
    [InlineData("create")]
    [InlineData("no-force")]
    [InlineData("force")]
    public async Task InstalledNewAndGetIseSnippetRouteShouldProcessForceAndFileInfoToOwnedService(string route)
    {
        await InitializeRuntimeAsync();
        await Phase4NewSnippetCoreAsync(route);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task Phase4NewSnippetCoreAsync(string route) => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var id = engine.LocalRunspaceId;
        var path = Path.Combine(root, "catalog", "Runtime.snippets.ps1xml");
        byte[]? bytes = null;
        if (route is "no-force" or "force")
        {
            await Task.Run(() => engine.Snippets.Create("Runtime", "old", "old", "old author", 1, false));
            bytes = await File.ReadAllBytesAsync(path);
        }
        var output = new List<OutputEntry>();
        engine.Output += output.Add;
        try
        {
            var modifier = route == "whatif" ? "-WhatIf" : route == "force" ? "-Force" : "";
            await engine.ExecuteAsync($$"""
                $global:phase4Sentinel = 'same engine'
                try {
                    New-IseSnippet -Title Runtime -Description 'runtime desc' -Text 'abc' -CaretOffset 3 {{modifier}} -ErrorAction Stop
                    'NEW:success'
                } catch { 'NEW:failure:' + $_.Exception.Message }
                $files = @(Get-IseSnippet)
                'FILES:' + $files.Count
                if ($files.Count) { 'TYPE:' + $files[0].GetType().FullName; 'PATH:' + $files[0].FullName }
                'ENGINE:' + $global:phase4Sentinel
                """);
            Assert.DoesNotContain(output, o => o.Kind == OutputKind.Error);
            Assert.Contains(output, o => o.Kind == OutputKind.Output && o.Text.Contains("ENGINE:same engine", StringComparison.Ordinal));
            if (route == "no-force")
            {
                Assert.Contains(output, o => o.Kind == OutputKind.Output && o.Text.Contains("NEW:failure:", StringComparison.Ordinal));
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
                Assert.Equal("old", Assert.Single((await engine.Snippets.LoadAsync()).Snippets).Code);
            }
            else
            {
                Assert.Contains(output, o => o.Kind == OutputKind.Output && o.Text.Contains("NEW:success", StringComparison.Ordinal));
                if (route == "whatif")
                {
                    Assert.False(Directory.Exists(engine.Snippets.UserDirectory));
                    Assert.Contains(output, o => o.Text.Contains("FILES:0", StringComparison.Ordinal));
                    Assert.Empty((await engine.Snippets.LoadAsync()).Snippets);
                }
                else
                    Assert.Equal(new PowerShellSnippet("Runtime", "runtime desc", "", "abc", 3, false),
                        Assert.Single((await engine.Snippets.LoadAsync()).Snippets));
            }
            if (route != "whatif")
            {
                Assert.Contains(output, o => o.Text.Contains("TYPE:System.IO.FileInfo", StringComparison.Ordinal));
                Assert.Contains(output, o => o.Text.Contains("PATH:" + path, StringComparison.Ordinal));
                Assert.Equal(new[] { path }, Directory.GetFiles(engine.Snippets.UserDirectory));
            }
            Assert.Equal(id, engine.LocalRunspaceId);
        }
        finally { engine.Output -= output.Add; }
    });

    [AvaloniaTheory]
    [InlineData("-Title '' -Description desc -Text abc", "Title")]
    [InlineData("-Title Owned -Description '' -Text abc", "Description")]
    [InlineData("-Title Owned -Description desc -Text ''", "Text")]
    [InlineData("-Title Owned -Description desc -Text abc -CaretOffset -1", "CaretOffset")]
    [InlineData("-Title Owned -Description desc -Text abc -CaretOffset 2147483648", "CaretOffset")]
    [InlineData("-Title Owned -Description desc -Text abc -CaretOffset 4", "caretOffset")]
    [InlineData("-Title '../escape' -Description desc -Text abc", "title")]
    public async Task InstalledNewIseSnippetValidationRejectsBeforeWritingOrEditing(string arguments, string parameter)
    {
        await InitializeRuntimeAsync();
        await Phase4SnippetValidationCoreAsync(arguments, parameter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task Phase4SnippetValidationCoreAsync(string arguments, string parameter) =>
        WithModuleWorkbenchAsync(false, async (workbench, _) =>
        {
            var engine = workbench.Workbench.SelectedSession!.Engine;
            workbench.Scripting.CurrentEditor!.Text = "untouched";
            var output = new List<OutputEntry>();
            engine.Output += output.Add;
            try
            {
                await engine.ExecuteAsync("try { New-IseSnippet " + arguments +
                    " -ErrorAction Stop; 'VALIDATION:unexpected success' } catch { 'VALIDATION:' + $_.Exception.Message }");
                Assert.DoesNotContain(output, o => o.Kind == OutputKind.Error);
                Assert.Contains(output, o => o.Kind == OutputKind.Output &&
                    o.Text.Contains("VALIDATION:", StringComparison.Ordinal) && o.Text.Contains(parameter, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(output, o => o.Kind == OutputKind.Output && o.Text.Contains("unexpected success", StringComparison.Ordinal));
                Assert.False(Directory.Exists(engine.Snippets.UserDirectory));
                Assert.Empty((await engine.Snippets.LoadAsync()).Snippets);
                Assert.Equal("untouched", workbench.Scripting.CurrentEditor.Text);
            }
            finally { engine.Output -= output.Add; }
        });

    [AvaloniaTheory]
    [InlineData("file")]
    [InlineData("folder")]
    [InlineData("recurse")]
    public async Task InstalledImportIseSnippetFileSystemRoutesKeepImportsInMemoryAndGetReturnsOnlyOwnedDiskFiles(string route)
    {
        await InitializeRuntimeAsync();
        await Phase4SnippetPathCoreAsync(route);
    }

    private static string Phase4CommandSnippetXml(string title) =>
        "<Snippets xmlns=\"http://schemas.microsoft.com/PowerShell/Snippets\"><Snippet Version=\"1.0.0\">" +
        "<Header><Title>" + title + "</Title><Description>desc</Description><Author>author</Author></Header>" +
        "<Code><Script Language=\"PowerShell\">abc</Script></Code></Snippet></Snippets>";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task Phase4SnippetPathCoreAsync(string route) => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        await Task.Run(() => engine.Snippets.Create("Disk", "disk", "disk", "", 0, false));
        var disk = Assert.Single(engine.Snippets.GetUserFiles()).FullName;
        var bytes = await File.ReadAllBytesAsync(disk);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var first = Path.Combine(source, "one.SNIPPETS.PS1XML");
        await File.WriteAllTextAsync(first, Phase4CommandSnippetXml("One"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "two.snippets.ps1xml"), Phase4CommandSnippetXml("Two"));
        await engine.SetWorkingDirectoryAsync(source);
        var output = new List<OutputEntry>();
        engine.Output += output.Add;
        try
        {
            await engine.ExecuteAsync("Import-IseSnippet -Path " + (route == "file" ? "'./one.SNIPPETS.PS1XML'" : "'.'") +
                (route == "recurse" ? " -Recurse" : "") + "; 'GET:' + ((@(Get-IseSnippet) | ForEach-Object FullName) -join '|')");
            Assert.DoesNotContain(output, o => o.Kind == OutputKind.Error);
            Assert.Contains(output, o => o.Kind == OutputKind.Output && o.Text.Contains("GET:" + disk, StringComparison.Ordinal));
            Assert.Equal(route == "recurse" ? new[] { "Disk", "Two", "One" } : new[] { "Disk", "One" },
                (await engine.Snippets.LoadAsync()).Snippets.Select(s => s.Title));
            Assert.Equal(new[] { disk }, engine.Snippets.GetUserFiles().Select(f => f.FullName));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(disk));
            Assert.Equal(new[] { "Disk" }, (await new IseSnippetService(engine.Snippets.UserDirectory).LoadAsync()).Snippets.Select(s => s.Title));
            Assert.Equal(Phase4CommandSnippetXml("One"), await File.ReadAllTextAsync(first));
        }
        finally { engine.Output -= output.Add; }
    });

    [AvaloniaTheory]
    [InlineData("variable", "ISE snippets require a FileSystem path.")]
    [InlineData("missing", "Specify an existing snippet file or directory.")]
    public async Task InstalledImportIseSnippetRejectsNonFileSystemAndMissingPathsWithoutPublishing(string route, string message)
    {
        await InitializeRuntimeAsync();
        await Phase4SnippetInvalidPathCoreAsync(route, message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task Phase4SnippetInvalidPathCoreAsync(string route, string message) => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var path = route == "variable" ? "Variable:phase4Missing" : Path.Combine(root, "missing.snippets.ps1xml");
        var output = new List<OutputEntry>();
        engine.Output += output.Add;
        try
        {
            await engine.ExecuteAsync("try { Import-IseSnippet -Path '" + path.Replace("'", "''") +
                "' -ErrorAction Stop; 'IMPORT:unexpected success' } catch { 'IMPORT:' + $_.Exception.Message }");
            Assert.DoesNotContain(output, o => o.Kind == OutputKind.Error);
            Assert.Contains(output, o => o.Kind == OutputKind.Output && o.Text.Contains("IMPORT:" + message, StringComparison.Ordinal));
            Assert.DoesNotContain(output, o => o.Kind == OutputKind.Output && o.Text.Contains("unexpected success", StringComparison.Ordinal));
            Assert.Empty((await engine.Snippets.LoadAsync()).Snippets);
            Assert.False(Directory.Exists(engine.Snippets.UserDirectory));
        }
        finally { engine.Output -= output.Add; }
    });

    [AvaloniaTheory]
    [InlineData("loaded")]
    [InlineData("loaded-flat")]
    [InlineData("available")]
    [InlineData("available-multiple")]
    [InlineData("no-snippets")]
    [InlineData("not-found")]
    public async Task InstalledImportIseSnippetModuleRoutesResolveOnlyOwnedLoadedOrExplicitAvailableModules(string route)
    {
        await InitializeRuntimeAsync();
        await Phase4SnippetModuleCoreAsync(route);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task Phase4SnippetModuleCoreAsync(string route) => WithModuleWorkbenchAsync(false, async (workbench, root) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var moduleName = "Phase4Owned" + Guid.NewGuid().ToString("N");
        var modulesRoot = Path.Combine(root, "modules");
        var manifest = Path.Combine(modulesRoot, "one", moduleName, moduleName + ".psd1");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        await File.WriteAllTextAsync(manifest, "@{ ModuleVersion = '1.0.0'; GUID = '11111111-1111-1111-1111-111111111111' }");
        var snippets = Path.Combine(Path.GetDirectoryName(manifest)!, "Snippets");
        if (route != "no-snippets")
        {
            Directory.CreateDirectory(Path.Combine(snippets, "nested"));
            await File.WriteAllTextAsync(Path.Combine(snippets, "one.snippets.ps1xml"), Phase4CommandSnippetXml("One"));
            await File.WriteAllTextAsync(Path.Combine(snippets, "nested", "nested.snippets.ps1xml"), Phase4CommandSnippetXml("Nested"));
        }
        if (route == "available-multiple")
        {
            var other = Path.Combine(modulesRoot, "two", moduleName);
            Directory.CreateDirectory(Path.Combine(other, "Snippets"));
            await File.WriteAllTextAsync(Path.Combine(other, moduleName + ".psd1"),
                "@{ ModuleVersion = '2.0.0'; GUID = '22222222-2222-2222-2222-222222222222' }");
            await File.WriteAllTextAsync(Path.Combine(other, "Snippets", "two.snippets.ps1xml"), Phase4CommandSnippetXml("Two"));
        }
        var available = route.StartsWith("available", StringComparison.Ordinal);
        var modulePath = route == "available-multiple" ? Path.Combine(modulesRoot, "*", moduleName, moduleName + ".psd1") : manifest;
        var parameter = available ? modulePath : moduleName;
        var output = new List<OutputEntry>();
        engine.Output += output.Add;
        try
        {
            if (!available && route != "not-found")
                await engine.ExecuteAsync("Import-Module '" + manifest.Replace("'", "''") + "' -ErrorAction Stop");
            await engine.ExecuteAsync("try { Import-IseSnippet -Module '" + parameter.Replace("'", "''") + "'" +
                (available ? " -ListAvailable" : "") + (route == "loaded-flat" ? "" : " -Recurse") +
                " -ErrorAction Stop; 'MODULE:success' } catch { 'MODULE:' + $_.Exception.Message }");
            Assert.DoesNotContain(output, o => o.Kind == OutputKind.Error);
            if (route == "not-found")
                Assert.Contains(output, o => o.Kind == OutputKind.Output && o.Text.Contains(
                    "Module '" + moduleName + "' was not found. Use -ListAvailable for installed modules.", StringComparison.Ordinal));
            else Assert.Contains(output, o => o.Kind == OutputKind.Output && o.Text.Contains("MODULE:success", StringComparison.Ordinal));
            var result = await engine.Snippets.LoadAsync();
            Assert.Empty(result.Errors);
            Assert.Equal(route is "no-snippets" or "not-found" ? Array.Empty<string>() :
                route == "available-multiple" ? new[] { "Nested", "One", "Two" } :
                route == "loaded-flat" ? new[] { "One" } : new[] { "Nested", "One" },
                result.Snippets.Select(s => s.Title).Order(StringComparer.Ordinal));
            Assert.Empty(engine.Snippets.GetUserFiles());
            Assert.False(Directory.Exists(engine.Snippets.UserDirectory));
        }
        finally { engine.Output -= output.Add; }
    });

    [AvaloniaTheory]
    [InlineData("Get-IseSnippet")]
    [InlineData("New-IseSnippet -Title Owned -Description desc -Text abc -WhatIf")]
    [InlineData("Import-IseSnippet -Path .")]
    public async Task InstalledSnippetCmdletsRejectOwnedPushedLocalRunspaceBeforeShouldProcessOrFilesystemAccess(string command)
    {
        await InitializeRuntimeAsync();
        await Phase4SnippetPushedRunspaceCoreAsync(command);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task Phase4SnippetPushedRunspaceCoreAsync(string command) => WithModuleWorkbenchAsync(false, async (workbench, _) =>
    {
        var engine = workbench.Workbench.SelectedSession!.Engine;
        var localId = engine.LocalRunspaceId;
        var output = new List<OutputEntry>();
        engine.Output += output.Add;
        try
        {
            await engine.ExecuteAsync("""
                $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault2()
                $iss.Commands.Add([System.Management.Automation.Runspaces.SessionStateCmdletEntry]::new(
                    'New-IseSnippet', [Iseberg.PowerShellHost.NewIseSnippetCommand], $null))
                $iss.Commands.Add([System.Management.Automation.Runspaces.SessionStateCmdletEntry]::new(
                    'Get-IseSnippet', [Iseberg.PowerShellHost.GetIseSnippetCommand], $null))
                $iss.Commands.Add([System.Management.Automation.Runspaces.SessionStateCmdletEntry]::new(
                    'Import-IseSnippet', [Iseberg.PowerShellHost.ImportIseSnippetCommand], $null))
                $global:phase4Pushed = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($Host, $iss)
                $global:phase4Pushed.Open()
                $Host.PushRunspace($global:phase4Pushed)
                """);
            Assert.DoesNotContain(output, o => o.Kind == OutputKind.Error);
            Assert.True(engine.IsRunspacePushed);
            Assert.False(engine.IsRemote);
            Assert.NotEqual(localId, engine.RunspaceId);
            await engine.ExecuteAsync("try { " + command +
                " -ErrorAction Stop; 'GUARD:unexpected success' } catch { 'GUARD:' + $_.Exception.Message }");
            Assert.Contains(output, o => o.Kind == OutputKind.Output && o.Text.Contains(
                "GUARD:ISE snippet commands require a local Iseberg PowerShell tab.", StringComparison.Ordinal));
            Assert.DoesNotContain(output, o => o.Kind == OutputKind.Output && o.Text.Contains("unexpected success", StringComparison.Ordinal));
            Assert.Empty((await engine.Snippets.LoadAsync()).Snippets);
            Assert.False(Directory.Exists(engine.Snippets.UserDirectory));
        }
        finally
        {
            if (engine.IsRunspacePushed) await engine.ExitRemoteSessionAsync();
            await engine.ExecuteAsync("if ($global:phase4Pushed) { $global:phase4Pushed.Dispose(); Remove-Variable phase4Pushed -Scope Global }");
            engine.Output -= output.Add;
        }
        Assert.Equal(localId, engine.RunspaceId);
        Assert.False(engine.IsRunspacePushed);
    });

    [AvaloniaTheory]
    [InlineData("Get-IseSnippet")]
    [InlineData("New-IseSnippet -Title Owned -Description desc -Text abc -WhatIf")]
    [InlineData("Import-IseSnippet -Path .")]
    public async Task RegisteredSnippetCmdletsWithoutWorkbenchHostRejectBeforeFilesystemAndShouldProcess(string command)
    {
        await InitializeRuntimeAsync();
        await Phase4SnippetWithoutHostCoreAsync(command);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Phase4SnippetWithoutHostCoreAsync(string command)
    {
        await using var engine = new PowerShellSession();
        await engine.InitializeAsync();
        var output = new List<OutputEntry>();
        engine.Output += output.Add;
        await engine.ExecuteAsync($$"""
            $state = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault2()
            $state.Commands.Add([System.Management.Automation.Runspaces.SessionStateCmdletEntry]::new(
                'Get-IseSnippet', [Iseberg.PowerShellHost.GetIseSnippetCommand], $null))
            $state.Commands.Add([System.Management.Automation.Runspaces.SessionStateCmdletEntry]::new(
                'New-IseSnippet', [Iseberg.PowerShellHost.NewIseSnippetCommand], $null))
            $state.Commands.Add([System.Management.Automation.Runspaces.SessionStateCmdletEntry]::new(
                'Import-IseSnippet', [Iseberg.PowerShellHost.ImportIseSnippetCommand], $null))
            $runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($state)
            $shell = [System.Management.Automation.PowerShell]::Create()
            try {
                $runspace.Open()
                $shell.Runspace = $runspace
                $command = '{{command.Replace("'", "''", StringComparison.Ordinal)}}'
                $null = $shell.AddScript("try { " + $command +
                    " -ErrorAction Stop; 'unexpected success' } catch { `$_.Exception.Message }")
                $results = $shell.Invoke()
                if ($shell.Streams.Error.Count -ne 0) { throw $shell.Streams.Error[0] }
                if ($results.Count -ne 1) { throw "Expected one guard result; got $($results.Count)." }
                $results[0]
            }
            finally {
                $shell.Dispose()
                $runspace.Dispose()
            }
            """);
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        Assert.Equal("ISE snippet commands require a local Iseberg PowerShell tab.",
            Assert.Single(output, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name == "System.Management.Automation");
    }
}
