using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Iseberg;
using Iseberg.Core;
using Xunit;
using static Devolutions.Terminal.UI.Tests.PowerShellIseTestHarness;

namespace Devolutions.Terminal.UI.Tests;

public sealed class PortableIseProgressContinuationTests
{
    [AvaloniaFact]
    public async Task RenderedProgressPreservesSourceIdentityHierarchyAndInteriorUpdateOrder()
    {
        await InitializeRuntimeAsync();
        await SourceHierarchyCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SourceHierarchyCoreAsync() => WithProgressWorkbenchAsync(async (workbench, _) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        var document = session.SelectedFile;
        using var gate = new ProgressDeliveryGate(session.Engine);
        var script = string.Join(";", new[]
        {
            ProgressScript(7, 1, -1, "Parent seven", "running", 20),
            ProgressScript(7, 2, 1, "First child", "first", 30),
            ProgressScript(7, 3, 1, "Interior child", "waiting", 40),
            ProgressScript(7, 4, 1, "Last child", "last", 50),
            ProgressScript(8, 1, -1, "Parent eight", "foreign", 60),
            ProgressScript(8, 2, 1, "Foreign child", "other source", 65),
            ProgressScript(7, 3, 1, "Interior child", "updated", 55, operation: "café", seconds: 12)
        });
        var execution = workbench.ExecuteAsync(script);
        var lines = new[]
        {
            "Parent seven - running",
            "  First child - first",
            "  Interior child - waiting",
            "  Last child - last",
            "Parent eight - foreign",
            "  Foreign child - other source"
        };
        for (var index = 0; index < 6; index++)
        {
            var delivery = await gate.NextAsync();
            await AssertProgressAsync(workbench, string.Join(Environment.NewLine, lines.Take(index + 1)),
                new[] { 20, 30, 40, 50, 60, 65 }[index]);
            Assert.Equal(index < 4 ? 7 : 8, delivery.Update.SourceId);
            Assert.Equal(new[] { 1, 2, 3, 4, 1, 2 }[index], delivery.Update.ActivityId);
            Assert.Same(session, workbench.Workbench.SelectedSession);
            Assert.Same(document, session.SelectedFile);
            Assert.False(execution.IsCompleted);
            delivery.Release();
        }
        var update = await gate.NextAsync();
        Assert.Equal(new ProgressUpdate("Interior child", "updated", 55, false, 7, 3, 1, 12, "café"), update.Update);
        await AssertProgressAsync(workbench, string.Join(Environment.NewLine, new[]
        {
            "Parent seven - running", "  First child - first", "  Interior child - updated - café",
            "  Last child - last", "Parent eight - foreign", "  Foreign child - other source"
        }), 65);
        Assert.Same(session, workbench.Workbench.SelectedSession);
        Assert.Same(document, session.SelectedFile);
        update.Release();
        await execution.WaitAsync(TimeSpan.FromSeconds(60));
        await AssertReadyProgressClearedAsync(workbench, session);
    });

    [AvaloniaTheory]
    [InlineData(2, "Parent - running\n  Middle - queued\n  Last - queued\nForeign - foreign")]
    [InlineData(3, "Parent - running\n  First - queued\n  Last - queued\nForeign - foreign")]
    [InlineData(4, "Parent - running\n  First - queued\n  Middle - queued\nForeign - foreign")]
    public async Task RenderedChildCompletionRemovesOnlyFirstInteriorOrLastOwnedNode(int activity, string expected)
    {
        await InitializeRuntimeAsync();
        await ChildCompletionCoreAsync(activity, expected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ChildCompletionCoreAsync(int activity, string expected) => WithProgressWorkbenchAsync(async (workbench, _) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        var document = session.SelectedFile;
        using var gate = new ProgressDeliveryGate(session.Engine);
        var execution = workbench.ExecuteAsync(string.Join(";", new[]
        {
            ProgressScript(7, 1, -1, "Parent", "running", 20),
            ProgressScript(7, 2, 1, "First", "queued", 30),
            ProgressScript(7, 3, 1, "Middle", "queued", 40),
            ProgressScript(7, 4, 1, "Last", "queued", 50),
            ProgressScript(8, activity, -1, "Foreign", "foreign", 63),
            ProgressScript(7, activity, -1, "Completed child", "completed", 100, completed: true)
        }));
        for (var index = 0; index < 5; index++) (await gate.NextAsync()).Release();
        var completed = await gate.NextAsync();
        Assert.True(completed.Update.Completed);
        Assert.Equal(7, completed.Update.SourceId);
        Assert.Equal(activity, completed.Update.ActivityId);
        await AssertProgressAsync(workbench, expected.Replace("\n", Environment.NewLine, StringComparison.Ordinal), 63);
        Assert.Same(session, workbench.Workbench.SelectedSession);
        Assert.Same(document, session.SelectedFile);
        Assert.Equal(SessionState.Running, session.Engine.State);
        completed.Release();
        await execution.WaitAsync(TimeSpan.FromSeconds(60));
        await AssertReadyProgressClearedAsync(workbench, session);
    });

    [AvaloniaTheory]
    [InlineData(-1, true, 0)]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 1)]
    [InlineData(99, false, 99)]
    [InlineData(100, false, 100)]
    public async Task RenderedProgressPercentBoundariesExposeExactDeterminateAndIndeterminateState(
        int percent, bool indeterminate, int value)
    {
        await InitializeRuntimeAsync();
        await PercentCoreAsync(percent, indeterminate, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task PercentCoreAsync(int percent, bool indeterminate, int value) =>
        WithProgressWorkbenchAsync(async (workbench, _) =>
        {
            var session = Assert.Single(workbench.Workbench.Sessions);
            using var gate = new ProgressDeliveryGate(session.Engine);
            var execution = workbench.ExecuteAsync(ProgressScript(-7, 0, -2, "Boundary", "working", percent,
                operation: "owned operation", seconds: 17));
            var delivery = await gate.NextAsync();
            Assert.Equal(new ProgressUpdate("Boundary", "working", percent, false, -7, 0, -2, 17, "owned operation"), delivery.Update);
            await AssertProgressAsync(workbench, "Boundary - working - owned operation", value, indeterminate);
            delivery.Release();
            await execution.WaitAsync(TimeSpan.FromSeconds(60));
            await AssertReadyProgressClearedAsync(workbench, session);
        });

    [AvaloniaFact]
    public async Task RenderedProgressOrphansAndSourceLocalParentCompletionPreserveOtherTrees()
    {
        await InitializeRuntimeAsync();
        await OrphanCompletionCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task OrphanCompletionCoreAsync() => WithProgressWorkbenchAsync(async (workbench, _) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        using var gate = new ProgressDeliveryGate(session.Engine);
        var execution = workbench.ExecuteAsync(string.Join(";", new[]
        {
            ProgressScript(7, 3, 2, "Grandchild", "early", 30),
            ProgressScript(7, 2, 1, "Child", "orphan", 40),
            ProgressScript(8, 2, 1, "Foreign orphan", "kept", 70),
            ProgressScript(7, 1, -1, "Parent", "late", 20),
            ProgressScript(7, 1, -1, "Parent", "done", 100, completed: true),
            ProgressScript(7, 99, -1, "Unknown", "done", 100, completed: true),
            ProgressScript(8, 1, -1, "Absent foreign parent", "done", 100, completed: true)
        }));
        var expected = new[]
        {
            "Grandchild - early",
            "  Grandchild - early\nChild - orphan",
            "  Grandchild - early\nChild - orphan\nForeign orphan - kept",
            "    Grandchild - early\n  Child - orphan\nForeign orphan - kept\nParent - late",
            "Foreign orphan - kept",
            "Foreign orphan - kept",
            ""
        };
        var values = new[] { 30, 40, 70, 20, 70, 70, 0 };
        for (var index = 0; index < expected.Length; index++)
        {
            var delivery = await gate.NextAsync();
            await AssertProgressAsync(workbench,
                expected[index].Replace("\n", Environment.NewLine, StringComparison.Ordinal), values[index], visible: index < 6);
            delivery.Release();
        }
        await execution.WaitAsync(TimeSpan.FromSeconds(60));
        await AssertReadyProgressClearedAsync(workbench, session);
    });

    [AvaloniaFact]
    public async Task InactiveSessionProgressDoesNotRepaintAndSwitchingRestoresRetainedHierarchy()
    {
        await InitializeRuntimeAsync();
        await SessionIsolationCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SessionIsolationCoreAsync() => WithProgressWorkbenchAsync(async (workbench, _) =>
    {
        var first = Assert.Single(workbench.Workbench.Sessions);
        var second = await workbench.CreateSessionAsync();
        workbench.SelectSession(first);
        using var firstGate = new ProgressDeliveryGate(first.Engine);
        using var secondGate = new ProgressDeliveryGate(second.Engine);
        var firstExecution = first.Engine.ExecuteAsync(ProgressScript(7, 1, -1, "Selected", "first", 27));
        var firstDelivery = await firstGate.NextAsync();
        await AssertProgressAsync(workbench, "Selected - first", 27);
        var secondExecution = second.Engine.ExecuteAsync(string.Join(";", new[]
        {
            ProgressScript(7, 1, -1, "Inactive", "second", 61),
            ProgressScript(7, 2, 1, "Inactive child", "queued", 72)
        }));
        var secondParent = await secondGate.NextAsync();
        await AssertProgressAsync(workbench, "Selected - first", 27);
        Assert.Same(first, workbench.Workbench.SelectedSession);
        secondParent.Release();
        var secondChild = await secondGate.NextAsync();
        await AssertProgressAsync(workbench, "Selected - first", 27);
        workbench.SelectSession(second);
        await AssertProgressAsync(workbench, "Inactive - second" + Environment.NewLine + "  Inactive child - queued", 72);
        Assert.Same(second.SelectedFile!.Document, workbench.ScriptEditorView.Document);
        workbench.SelectSession(first);
        await AssertProgressAsync(workbench, "Selected - first", 27);
        Assert.Same(first.SelectedFile!.Document, workbench.ScriptEditorView.Document);
        secondChild.Release();
        await secondExecution.WaitAsync(TimeSpan.FromSeconds(60));
        workbench.SelectSession(second);
        await AssertReadyProgressClearedAsync(workbench, second);
        workbench.SelectSession(first);
        await AssertProgressAsync(workbench, "Selected - first", 27);
        firstDelivery.Release();
        await firstExecution.WaitAsync(TimeSpan.FromSeconds(60));
        await AssertReadyProgressClearedAsync(workbench, first);
    });

    [AvaloniaFact]
    public async Task RemovedSessionQueuedAndLaterProgressCannotReplaceSelectedHierarchy()
    {
        await InitializeRuntimeAsync();
        await RemovedSessionCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task RemovedSessionCoreAsync() => WithProgressWorkbenchAsync(async (workbench, _) =>
    {
        var retained = Assert.Single(workbench.Workbench.Sessions);
        var removed = await workbench.CreateSessionAsync();
        workbench.SelectSession(retained);
        using var retainedGate = new ProgressDeliveryGate(retained.Engine);
        using var removedGate = new ProgressDeliveryGate(removed.Engine);
        var retainedExecution = retained.Engine.ExecuteAsync(ProgressScript(7, 1, -1, "Retained", "running", 33));
        var retainedDelivery = await retainedGate.NextAsync();
        await AssertProgressAsync(workbench, "Retained - running", 33);
        // Do not yield the UI thread: the real callback is queued, but not yet consumed.
        var removedExecution = removed.Engine.ExecuteAsync(ProgressScript(7, 1, -1, "Removed", "queued", 66));
        var queued = removedGate.NextAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        workbench.Workbench.Sessions.Remove(removed);
        try
        {
            await AssertProgressAsync(workbench, "Retained - running", 33);
            queued.Release();
            await removedExecution.WaitAsync(TimeSpan.FromSeconds(60));
            var lateExecution = removed.Engine.ExecuteAsync(ProgressScript(7, 2, 1, "Removed late", "must not render", 88));
            var late = await removedGate.NextAsync();
            await AssertProgressAsync(workbench, "Retained - running", 33);
            Assert.Same(retained, Assert.Single(workbench.Workbench.Sessions));
            Assert.Same(retained, workbench.Workbench.SelectedSession);
            late.Release();
            await lateExecution.WaitAsync(TimeSpan.FromSeconds(60));
            await AssertProgressAsync(workbench, "Retained - running", 33);
        }
        finally
        {
            queued.Release();
            removedGate.ReleaseAll();
            await removed.Engine.DisposeAsync();
            retainedDelivery.Release();
            await retainedExecution.WaitAsync(TimeSpan.FromSeconds(60));
        }
    });

    [AvaloniaFact]
    public async Task PublicSessionCloseDropsRetainedProgressBeforeReplacementSessionStarts()
    {
        await InitializeRuntimeAsync();
        await SessionCloseCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task SessionCloseCoreAsync() => WithProgressWorkbenchAsync(async (workbench, owner) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        using var gate = new ProgressDeliveryGate(session.Engine);
        var execution = workbench.ExecuteAsync(ProgressScript(7, 1, -1, "Closing session", "running", 48));
        var delivery = await gate.NextAsync();
        await AssertProgressAsync(workbench, "Closing session - running", 48);
        var before = owner.OwnedWindows.ToArray();
        var close = workbench.CloseSessionAsync(session);
        await ChooseDialogAsync(owner, before, close, "Stop", delivery.Release);
        Assert.True(await close);
        await execution.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(SessionState.Disposed, session.Engine.State);
        var replacement = Assert.Single(workbench.Workbench.Sessions);
        Assert.NotSame(session, replacement);
        Assert.Same(replacement, workbench.Workbench.SelectedSession);
        await AssertReadyProgressClearedAsync(workbench, replacement);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Engine.ExecuteAsync("'late'"));
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkbenchCloseAndDisposalClearProgressAndRejectAlreadyQueuedDelivery(bool approvedClose)
    {
        await InitializeRuntimeAsync();
        await WorkbenchCloseCoreAsync(approvedClose);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task WorkbenchCloseCoreAsync(bool approvedClose) => WithProgressWorkbenchAsync(async (workbench, owner) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        using var gate = new ProgressDeliveryGate(session.Engine);
        var execution = workbench.ExecuteAsync(string.Join(";", new[]
        {
            ProgressScript(7, 1, -1, "Before close", "running", 35),
            ProgressScript(7, 2, 1, "Queued child", "must not appear", 75)
        }));
        var first = await gate.NextAsync();
        await AssertProgressAsync(workbench, "Before close - running", 35);
        first.Release();
        // Hold the dispatcher until the worker has published its next real progress event.
        var queued = gate.NextAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (approvedClose)
        {
            var before = owner.OwnedWindows.ToArray();
            var close = workbench.RequestCloseAsync();
            await ChooseDialogAsync(owner, before, close, "Stop", queued.Release);
            Assert.True(await close);
        }
        else
        {
            var disposing = workbench.DisposeAsync();
            Assert.True(workbench.IsDisposed);
            queued.Release();
            await disposing;
        }
        await execution.WaitAsync(TimeSpan.FromSeconds(60));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.True(workbench.IsDisposed);
        Assert.Equal(SessionState.Disposed, session.Engine.State);
        var text = workbench.FindControl<TextBlock>("ProgressText")!.Text;
        // Approved close remains cancelable while its Stop dialog is pending. Delivery during
        // that dialog is allowed; only irreversible disposal must reject queued callbacks.
        if (!approvedClose) Assert.DoesNotContain("Queued child", text, StringComparison.Ordinal);
        Assert.False(workbench.FindControl<Control>("ProgressPanel")!.IsVisible);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => workbench.ExecuteAsync("'late progress'"));
        await workbench.DisposeAsync();
        Assert.False(workbench.FindControl<Control>("ProgressPanel")!.IsVisible);
        Assert.True(owner.IsVisible);
    });

    [AvaloniaFact]
    public async Task CompletingLastRenderedChildSelectsPreviousSurvivorThenParentPercentage()
    {
        await InitializeRuntimeAsync();
        await LastSurvivorCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task LastSurvivorCoreAsync() => WithProgressWorkbenchAsync(async (workbench, _) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        using var gate = new ProgressDeliveryGate(session.Engine);
        var execution = workbench.ExecuteAsync(string.Join(";", new[]
        {
            ProgressScript(7, 1, -1, "Parent", "running", 20),
            ProgressScript(7, 2, 1, "First", "queued", 55),
            ProgressScript(7, 3, 1, "Last", "queued", 99),
            ProgressScript(7, 3, -1, "Last", "done", 100, completed: true),
            ProgressScript(7, 2, -1, "First", "done", 100, completed: true),
            ProgressScript(7, 1, -1, "Parent", "done", 100, completed: true)
        }));
        var expected = new[]
        {
            "Parent - running",
            "Parent - running\n  First - queued",
            "Parent - running\n  First - queued\n  Last - queued",
            "Parent - running\n  First - queued",
            "Parent - running",
            ""
        };
        for (var index = 0; index < expected.Length; index++)
        {
            var delivery = await gate.NextAsync();
            await AssertProgressAsync(workbench,
                expected[index].Replace("\n", Environment.NewLine, StringComparison.Ordinal),
                new[] { 20, 55, 99, 55, 20, 0 }[index], visible: index < 5);
            delivery.Release();
        }
        await execution.WaitAsync(TimeSpan.FromSeconds(60));
        await AssertReadyProgressClearedAsync(workbench, session);
    });

    [AvaloniaFact]
    public async Task RenderedProgressCyclesTerminateAndCompletionRemovesOnlyTheirSource()
    {
        await InitializeRuntimeAsync();
        await CycleCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task CycleCoreAsync() => WithProgressWorkbenchAsync(async (workbench, _) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        using var gate = new ProgressDeliveryGate(session.Engine);
        var execution = workbench.ExecuteAsync(string.Join(";", new[]
        {
            ProgressScript(7, 1, 2, "A", "cycle", 31),
            ProgressScript(7, 2, 1, "B", "cycle", 41),
            ProgressScript(7, 3, 2, "Descendant", "cycle", 71),
            ProgressScript(8, 1, -1, "Foreign", "kept", 81),
            ProgressScript(7, 1, -1, "A", "done", 100, completed: true)
        }));
        var expected = new[]
        {
            "A - cycle",
            "  A - cycle\n  B - cycle",
            "  A - cycle\n  B - cycle\n    Descendant - cycle",
            "  A - cycle\n  B - cycle\n    Descendant - cycle\nForeign - kept",
            "Foreign - kept"
        };
        for (var index = 0; index < expected.Length; index++)
        {
            var delivery = await gate.NextAsync();
            await AssertProgressAsync(workbench,
                expected[index].Replace("\n", Environment.NewLine, StringComparison.Ordinal),
                new[] { 31, 41, 71, 81, 81 }[index]);
            delivery.Release();
        }
        await execution.WaitAsync(TimeSpan.FromSeconds(60));
        await AssertReadyProgressClearedAsync(workbench, session);
    });

    [AvaloniaFact]
    public async Task RenderedProgressReparentingUpdatesDescendantDepthWithoutReorderingRecords()
    {
        await InitializeRuntimeAsync();
        await ReparentCoreAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ReparentCoreAsync() => WithProgressWorkbenchAsync(async (workbench, _) =>
    {
        var session = Assert.Single(workbench.Workbench.Sessions);
        using var gate = new ProgressDeliveryGate(session.Engine);
        var execution = workbench.ExecuteAsync(string.Join(";", new[]
        {
            ProgressScript(7, 1, -1, "Parent", "running", 21),
            ProgressScript(7, 2, 1, "Child", "nested", 41),
            ProgressScript(7, 3, 2, "Grandchild", "nested", 61),
            ProgressScript(7, 2, -1, "Child", "detached", 51),
            ProgressScript(7, 2, 1, "Child", "reattached", 52)
        }));
        for (var index = 0; index < 2; index++) (await gate.NextAsync()).Release();
        var nested = await gate.NextAsync();
        await AssertProgressAsync(workbench, "Parent - running" + Environment.NewLine +
            "  Child - nested" + Environment.NewLine + "    Grandchild - nested", 61);
        nested.Release();
        var detached = await gate.NextAsync();
        await AssertProgressAsync(workbench, "Parent - running" + Environment.NewLine +
            "Child - detached" + Environment.NewLine + "  Grandchild - nested", 61);
        detached.Release();
        var reattached = await gate.NextAsync();
        await AssertProgressAsync(workbench, "Parent - running" + Environment.NewLine +
            "  Child - reattached" + Environment.NewLine + "    Grandchild - nested", 61);
        reattached.Release();
        await execution.WaitAsync(TimeSpan.FromSeconds(60));
        await AssertReadyProgressClearedAsync(workbench, session);
    });

    private static string ProgressScript(long source, int activity, int parent, string label, string status, int percent,
        bool completed = false, string? operation = null, int seconds = -1) =>
        $"$p = [System.Management.Automation.ProgressRecord]::new({activity}, '{label}', '{status}'); " +
        $"$p.ParentActivityId = {parent}; $p.PercentComplete = {percent}; $p.SecondsRemaining = {seconds}; " +
        (operation is null ? "" : $"$p.CurrentOperation = '{operation}'; ") +
        (completed ? "$p.RecordType = [System.Management.Automation.ProgressRecordType]::Completed; " : "") +
        $"$Host.UI.WriteProgress([long]{source}, $p)";

    private static async Task AssertProgressAsync(WorkbenchControl workbench, string text, double value,
        bool indeterminate = false, bool visible = true)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(visible, workbench.FindControl<Control>("ProgressPanel")!.IsVisible);
        Assert.Equal(text, workbench.FindControl<TextBlock>("ProgressText")!.Text);
        var progress = workbench.FindControl<ProgressBar>("ScriptProgress")!;
        Assert.Equal(value, progress.Value);
        Assert.Equal(indeterminate, progress.IsIndeterminate);
    }

    private static async Task AssertReadyProgressClearedAsync(WorkbenchControl workbench, SessionModel session)
    {
        await WaitForAsync(() => !workbench.FindControl<Control>("ProgressPanel")!.IsVisible,
            () => "Ready session retained a visible progress hierarchy.");
        Assert.Equal(SessionState.Ready, session.Engine.State);
        Assert.False(workbench.FindControl<Control>("ProgressPanel")!.IsVisible);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task WithProgressWorkbenchAsync(Func<WorkbenchControl, Window, Task> test)
    {
        using var environment = new IseTestEnvironment();
        var workbench = new WorkbenchControl(new WorkbenchOptions
        {
            EnableCommandsPane = false,
            SnippetDirectory = Path.Combine(environment.DirectoryPath, "snippets"),
            StartingDirectory = environment.DirectoryPath,
            Preferences = new UserSettings { AutoSaveMinutes = 0, LoadProfiles = false, CheckForUpdates = false }
        });
        var errors = new List<Exception>();
        workbench.ErrorOccurred += (_, error) => errors.Add(error.Exception);
        var owner = new Window { Width = 1100, Height = 800, Content = workbench };
        try
        {
            owner.Show();
            await workbench.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await test(workbench, owner);
            Assert.Empty(errors);
        }
        finally
        {
            try { await workbench.DisposeAsync(); }
            finally { owner.Content = null; owner.Close(); }
        }
    }

    // Existing public engine notifications provide a deterministic pipeline-thread barrier.
    // No production callback is invoked or reflected: every update originates in $Host.UI.WriteProgress.
    private sealed class ProgressDeliveryGate : IDisposable
    {
        private readonly PowerShellSession engine;
        private readonly Channel<Delivery> deliveries = Channel.CreateUnbounded<Delivery>();
        private readonly List<Delivery> outstanding = [];
        private bool disposed;

        public ProgressDeliveryGate(PowerShellSession engine)
        {
            this.engine = engine;
            engine.ProgressChanged += OnProgress;
        }

        private void OnProgress(ProgressUpdate update)
        {
            Delivery delivery;
            lock (outstanding)
            {
                if (disposed) return;
                delivery = new(update);
                outstanding.Add(delivery);
            }
            deliveries.Writer.TryWrite(delivery);
            delivery.Released.Task.GetAwaiter().GetResult();
        }

        public Task<Delivery> NextAsync() => deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        public void ReleaseAll()
        {
            lock (outstanding)
                foreach (var delivery in outstanding) delivery.Release();
        }

        public void Dispose()
        {
            lock (outstanding)
            {
                disposed = true;
                foreach (var delivery in outstanding) delivery.Release();
            }
            engine.ProgressChanged -= OnProgress;
        }

        public sealed class Delivery(ProgressUpdate update)
        {
            public ProgressUpdate Update { get; } = update;
            public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Release() => Released.TrySetResult();
        }
    }
}
