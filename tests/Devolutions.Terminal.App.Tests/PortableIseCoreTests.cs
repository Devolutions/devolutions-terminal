using System.Diagnostics;
using System.Text.Json;
using Iseberg.Core;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class PortableIseCoreTests : IClassFixture<PortableIseRuntimeFixture>
{
    [Fact]
    public void PersistenceLeaseContentionPreservesFirstOwnerAndLockBytes()
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "settings.json");
        File.WriteAllBytes(path + ".lock", [17, 31, 47]);
        var first = new WorkbenchPersistenceLease(path);
        try
        {
            Assert.Throws<IOException>(() => new WorkbenchPersistenceLease(path));
            using (var independent = new WorkbenchPersistenceLease(Path.Combine(sandbox.Root, "other.json")))
            {
                // A failed acquisition must not have released the first owner's handle.
                Assert.Throws<IOException>(() => new WorkbenchPersistenceLease(path));
            }

            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".lock"));
        }
        finally { first.Dispose(); }
        first.Dispose();
        Assert.Equal(new byte[] { 17, 31, 47 }, File.ReadAllBytes(path + ".lock"));
        using (var reacquired = new WorkbenchPersistenceLease(path))
            Assert.Throws<IOException>(() => new WorkbenchPersistenceLease(path));
        Assert.Equal(new byte[] { 17, 31, 47 }, File.ReadAllBytes(path + ".lock"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/settings.json")]
    public void PersistenceLeaseRejectsNonAbsolutePath(string path)
    {
        Assert.Equal("settingsPath", Assert.Throws<ArgumentException>(
            () => new WorkbenchPersistenceLease(path)).ParamName);
    }

    [Fact]
    public void PersistenceLeaseRejectsNullPath()
    {
        Assert.Equal("settingsPath", Assert.Throws<ArgumentNullException>(
            () => new WorkbenchPersistenceLease(null!)).ParamName);
    }

    [Fact]
    public void PersistenceLeaseCreatesOnlyEmptyLockAtExactNestedPath()
    {
        using var sandbox = new RecoverySandbox();
        var directory = Path.Combine(sandbox.Root, "nested");
        var path = Path.Combine(directory, "settings.json");
        using (var lease = new WorkbenchPersistenceLease(path))
        {
            Assert.Equal(new[] { path + ".lock" }, Directory.GetFiles(directory));
            Assert.Throws<IOException>(() => new WorkbenchPersistenceLease(path));
        }
        Assert.Empty(File.ReadAllBytes(path + ".lock"));
        Assert.False(File.Exists(path));
        using var reacquired = new WorkbenchPersistenceLease(path);
        Assert.Equal(new[] { path + ".lock" }, Directory.GetFiles(directory));
    }

    [Fact]
    public async Task MissingRecoveryAndStateAreEmptyWithoutCreatingStores()
    {
        using var sandbox = new RecoverySandbox();
        var missing = Path.Combine(sandbox.Root, "missing");
        var recovery = new ScriptRecovery(missing);
        Assert.Empty(await recovery.ReadAsync());
        recovery.Remove(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Assert.Null(await new WorkbenchStateStore(Path.Combine(missing, "state.json")).LoadAsync());
        Assert.False(Directory.Exists(missing));
        Assert.Empty(await new ScriptRecovery(sandbox.Root).ReadAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Root));
    }

    [Theory]
    [InlineData("", 65001, false, "UTF-8")]
    [InlineData("$x = 'café'\r\n$x\n", 1200, true, "UTF-16 LE")]
    public async Task ReleasedRecoveryPreservesDirtyDraftFields(
        string text, int codePage, bool bom, string encodingName)
    {
        using var sandbox = new RecoverySandbox();
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var file = ScriptFile.FromRecovery("Draft café.ps1", text, new ScriptEncoding(codePage, bom));
        Assert.True(file.IsDirty);
        var recovery = new ScriptRecovery(sandbox.Root);
        await recovery.SaveAsync(id, file, "PowerShell 7", released: true);

        var result = Assert.Single(await recovery.ReadAsync());
        Assert.Equal(id, result.Id);
        Assert.Equal("Draft café.ps1", result.Name);
        Assert.Null(result.Path);
        Assert.Equal(text, result.Text);
        Assert.Equal(0, result.OwnerProcessId);
        Assert.Equal("PowerShell 7", result.SessionName);
        Assert.Equal(new ScriptEncoding(codePage, bom, encodingName), result.Encoding);
        using var ownProcess = Process.GetCurrentProcess();
        Assert.Equal(ownProcess.StartTime.ToUniversalTime(), result.OwnerStartedUtc);
        Assert.Equal(new[] { id.ToString("N") + ".json" },
            Directory.GetFiles(sandbox.Root).Select(Path.GetFileName).ToArray());

        var restored = ScriptFile.FromRecovery(result.Name, result.Text, result.Encoding);
        Assert.Equal(text, restored.Text);
        Assert.Equal("Draft café.ps1*", restored.Title);
        Assert.True(restored.IsDirty);
        Assert.Null(restored.Path);
    }

    [Fact]
    public async Task LiveRecoveryRecordsOwnProcessButIsNotEligibleUntilReleased()
    {
        using var sandbox = new RecoverySandbox();
        var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var recovery = new ScriptRecovery(sandbox.Root);
        var file = ScriptFile.FromRecovery("live.ps1", "Write-Output 'live'");
        await recovery.SaveAsync(id, file);
        var path = sandbox.Snapshot(id);
        var bytes = await File.ReadAllBytesAsync(path);
        using var json = JsonDocument.Parse(bytes);
        var record = json.RootElement;
        Assert.Equal(Environment.ProcessId, record.GetProperty("OwnerProcessId").GetInt32());
        using var ownProcess = Process.GetCurrentProcess();
        Assert.Equal(ownProcess.StartTime.ToUniversalTime(), record.GetProperty("OwnerStartedUtc").GetDateTime());
        Assert.Equal("live.ps1", record.GetProperty("Name").GetString());
        Assert.Equal("Write-Output 'live'", record.GetProperty("Text").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("SessionName").ValueKind);
        Assert.Empty(await recovery.ReadAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        await recovery.SaveAsync(id, file, released: true);
        var released = Assert.Single(await recovery.ReadAsync());
        Assert.Equal(0, released.OwnerProcessId);
        Assert.Equal("Write-Output 'live'", released.Text);
    }

    [Fact]
    public async Task DirtySavedRecoveryRecordsAbsolutePathAndCurrentEncoding()
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "saved.ps1");
        var file = ScriptFile.FromBytes(path, [65, 66, 67]);
        file.Text = "changed\r\n";
        file.SetEncoding(new ScriptEncoding(65001, true));
        var recoveryDirectory = Path.Combine(sandbox.Root, "recovery");
        var recovery = new ScriptRecovery(recoveryDirectory);
        await recovery.SaveAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"), file, released: true);
        var result = Assert.Single(await recovery.ReadAsync());
        Assert.Equal(path, result.Path);
        Assert.Equal("saved.ps1", result.Name);
        Assert.Equal("changed\r\n", result.Text);
        Assert.Equal(new ScriptEncoding(65001, true, "UTF-8 BOM"), result.Encoding);
        Assert.False(File.Exists(path));
        Assert.True(file.IsDirty);
    }

    [Fact]
    public async Task CleanSaveRemovesOnlyOwnSnapshot()
    {
        using var sandbox = new RecoverySandbox();
        var recovery = new ScriptRecovery(sandbox.Root);
        var own = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sibling = Guid.Parse("22222222-2222-2222-2222-222222222222");
        await recovery.SaveAsync(own, ScriptFile.FromRecovery("own.ps1", "own"), released: true);
        await recovery.SaveAsync(sibling, ScriptFile.FromRecovery("sibling.ps1", "sibling"), released: true);
        var siblingBytes = await File.ReadAllBytesAsync(sandbox.Snapshot(sibling));
        await recovery.SaveAsync(own, ScriptFile.CreateUntitled("clean.ps1"));
        Assert.False(File.Exists(sandbox.Snapshot(own)));
        Assert.Equal(siblingBytes, await File.ReadAllBytesAsync(sandbox.Snapshot(sibling)));
        var remaining = Assert.Single(await recovery.ReadAsync());
        Assert.Equal(sibling, remaining.Id);
        Assert.Equal("sibling", remaining.Text);
        recovery.Remove(own);
        Assert.Equal(siblingBytes, await File.ReadAllBytesAsync(sandbox.Snapshot(sibling)));
    }

    [Fact]
    public async Task RecoveryReadOrdersFilesAndIgnoresNonJsonSiblings()
    {
        using var sandbox = new RecoverySandbox();
        var recovery = new ScriptRecovery(sandbox.Root);
        await recovery.SaveAsync(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            ScriptFile.FromRecovery("last.ps1", "last"), released: true);
        await recovery.SaveAsync(Guid.Parse("00000001-0000-0000-0000-000000000000"),
            ScriptFile.FromRecovery("first.ps1", ""), released: true);
        await File.WriteAllTextAsync(Path.Combine(sandbox.Root, "unfinished.tmp"), "not JSON");
        var results = await recovery.ReadAsync();
        Assert.Equal(new[] { "first.ps1", "last.ps1" }, results.Select(s => s.Name).ToArray());
        Assert.Equal(new[] { "", "last" }, results.Select(s => s.Text).ToArray());
        Assert.Equal("not JSON", await File.ReadAllTextAsync(Path.Combine(sandbox.Root, "unfinished.tmp")));
    }

    [Fact]
    public async Task LegacyNoOwnerRecoveryIsEligibleWithExactOptionalDefaults()
    {
        using var sandbox = new RecoverySandbox();
        const string content = """
            {"Id":"11111111-1111-1111-1111-111111111111","Name":"legacy.ps1","Path":null,"Text":"legacy\n"}
            """;
        var path = Path.Combine(sandbox.Root, "legacy.json");
        await File.WriteAllTextAsync(path, content);
        var result = Assert.Single(await new ScriptRecovery(sandbox.Root).ReadAsync());
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.Id);
        Assert.Equal("legacy.ps1", result.Name);
        Assert.Equal("legacy\n", result.Text);
        Assert.Null(result.Path);
        Assert.Null(result.Encoding);
        Assert.Null(result.SessionName);
        Assert.Equal(0, result.OwnerProcessId);
        Assert.Equal(DateTime.MinValue, result.OwnerStartedUtc);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"Id":false,"Name":"draft","Text":""}""")]
    public async Task MalformedRecoveryIsAnExplicitErrorWithoutRewriting(string content)
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "bad.json");
        await File.WriteAllTextAsync(path, content);
        var bytes = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<JsonException>(() => new ScriptRecovery(sandbox.Root).ReadAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(sandbox.Root));
    }

    [Theory]
    [InlineData("null", "Empty recovery file: ")]
    [InlineData("""{"Id":"00000000-0000-0000-0000-000000000000","Name":"draft","Text":""}""", "Invalid recovery file: ")]
    [InlineData("""{"Id":"11111111-1111-1111-1111-111111111111","Name":" ","Text":""}""", "Invalid recovery file: ")]
    [InlineData("""{"Id":"11111111-1111-1111-1111-111111111111","Name":null,"Text":""}""", "Invalid recovery file: ")]
    [InlineData("""{"Id":"11111111-1111-1111-1111-111111111111","Name":"draft","Text":null}""", "Invalid recovery file: ")]
    public async Task RecoveryValidationRejectsInvalidIdentityNameAndText(string content, string message)
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "invalid.json");
        await File.WriteAllTextAsync(path, content);
        var bytes = await File.ReadAllBytesAsync(path);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ScriptRecovery(sandbox.Root).ReadAsync());
        Assert.Equal(message + path, error.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RecoveryValidationRejectsNonPositiveCodePageWithoutRewriting(int codePage)
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "invalid.json");
        var content = $$$"""
            {"Id":"11111111-1111-1111-1111-111111111111","Name":"draft","Text":"",
             "Encoding":{"CodePage":{{{codePage}}},"EmitBom":false}}
            """;
        await File.WriteAllTextAsync(path, content);
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new ScriptRecovery(sandbox.Root).ReadAsync());
        Assert.Equal("CodePage", error.ParamName);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RecoveryValidationRejectsUnsupportedBomWithoutRewriting()
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "invalid.json");
        const string content = """
            {"Id":"11111111-1111-1111-1111-111111111111","Name":"draft","Text":"",
             "Encoding":{"CodePage":20127,"EmitBom":true}}
            """;
        await File.WriteAllTextAsync(path, content);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new ScriptRecovery(sandbox.Root).ReadAsync());
        Assert.Equal("EmitBom", error.ParamName);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void ProgressIdentityUpdatesPreserveSourcesSiblingsAndInsertionOrder()
    {
        var tracker = new ProgressTracker();
        Assert.Empty(tracker.Active);
        tracker.Update(new("Parent", "running", 10, false, 7, 1));
        tracker.Update(new("First", "queued", -1, false, 7, 2, 1));
        tracker.Update(new("Second", "queued", 0, false, 7, 3, 1));
        tracker.Update(new("Other source", "independent", 40, false, 8, 2));
        tracker.Update(new("First", "working", 55, false, 7, 2, 1, 12, "copy café"));

        Assert.Equal(new ProgressUpdate[]
        {
            new("Parent", "running", 10, false, 7, 1),
            new("First", "working", 55, false, 7, 2, 1, 12, "copy café"),
            new("Second", "queued", 0, false, 7, 3, 1),
            new("Other source", "independent", 40, false, 8, 2)
        }, tracker.Active);
        Assert.Equal(new[] { 0, 1, 1, 0 }, tracker.Active.Select(tracker.Depth).ToArray());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ProgressChildCompletionRemovesOnlyItsOwnNode(int completed)
    {
        var tracker = new ProgressTracker();
        tracker.Update(new("Parent", "ready", 1, false, 7, 1));
        tracker.Update(new("First", "ready", 2, false, 7, 2, 1));
        tracker.Update(new("Interior", "ready", 3, false, 7, 3, 1));
        tracker.Update(new("Last", "ready", 4, false, 7, 4, 1));
        tracker.Update(new("Other source", "ready", 5, false, 8, completed));
        tracker.Update(new("Finished", "done", 100, true, 7, completed, 1));
        var expectedChildren = completed switch
        {
            2 => new ProgressUpdate[]
            {
                new("Parent", "ready", 1, false, 7, 1),
                new("Interior", "ready", 3, false, 7, 3, 1),
                new("Last", "ready", 4, false, 7, 4, 1),
                new("Other source", "ready", 5, false, 8, 2)
            },
            3 => new ProgressUpdate[]
            {
                new("Parent", "ready", 1, false, 7, 1),
                new("First", "ready", 2, false, 7, 2, 1),
                new("Last", "ready", 4, false, 7, 4, 1),
                new("Other source", "ready", 5, false, 8, 3)
            },
            _ => new ProgressUpdate[]
            {
                new("Parent", "ready", 1, false, 7, 1),
                new("First", "ready", 2, false, 7, 2, 1),
                new("Interior", "ready", 3, false, 7, 3, 1),
                new("Other source", "ready", 5, false, 8, 4)
            }
        };
        Assert.Equal(expectedChildren, tracker.Active);
        Assert.All(tracker.Active.Where(p => p.ActivityId != 1 && p.SourceId == 7),
            p => Assert.Equal(1, tracker.Depth(p)));
    }

    [Fact]
    public void ProgressParentCompletionCascadesDescendantsOnlyWithinItsSource()
    {
        var tracker = new ProgressTracker();
        // Descendants precede their parents to exercise repeated traversal rather than one pass.
        tracker.Update(new("Grandchild", "active", 5, false, 7, 3, 2));
        tracker.Update(new("Child", "active", 10, false, 7, 2, 1));
        tracker.Update(new("Parent", "active", 20, false, 7, 1));
        tracker.Update(new("Sibling tree", "active", 30, false, 7, 4));
        tracker.Update(new("Foreign child", "active", 40, false, 8, 2, 1));
        tracker.Update(new("Foreign parent", "active", 50, false, 8, 1));
        Assert.Equal(2, tracker.Depth(new("Grandchild", "active", 5, false, 7, 3, 2)));
        tracker.Update(new("Parent", "done", 100, true, 7, 1));
        Assert.Equal(new ProgressUpdate[]
        {
            new("Sibling tree", "active", 30, false, 7, 4),
            new("Foreign child", "active", 40, false, 8, 2, 1),
            new("Foreign parent", "active", 50, false, 8, 1)
        }, tracker.Active);
        Assert.Equal(new[] { 0, 1, 0 }, tracker.Active.Select(tracker.Depth).ToArray());
    }

    [Fact]
    public void ProgressOrphansCyclesAndUnknownCompletionFollowImplementedPolicy()
    {
        var tracker = new ProgressTracker();
        tracker.Update(new("Orphan", "waiting", -1, false, 7, 3, 99));
        tracker.Update(new("Foreign parent", "active", 10, false, 8, 99));
        Assert.Equal(0, tracker.Depth(new("Orphan", "waiting", -1, false, 7, 3, 99)));
        tracker.Update(new("Unknown", "done", 100, true, 7, 77));
        Assert.Equal(new ProgressUpdate[]
        {
            new("Orphan", "waiting", -1, false, 7, 3, 99),
            new("Foreign parent", "active", 10, false, 8, 99)
        }, tracker.Active);
        // Completion of an absent parent still removes its orphaned descendants.
        tracker.Update(new("Absent parent", "done", 100, true, 7, 99));
        Assert.Equal(new ProgressUpdate[] { new("Foreign parent", "active", 10, false, 8, 99) }, tracker.Active);
        tracker.Update(new("Cycle A", "active", 1, false, 7, 1, 2));
        tracker.Update(new("Cycle B", "active", 2, false, 7, 2, 1));
        tracker.Update(new("Below cycle", "active", 3, false, 7, 4, 1));
        Assert.Equal(1, tracker.Depth(new("Cycle A", "active", 1, false, 7, 1, 2)));
        Assert.Equal(1, tracker.Depth(new("Cycle B", "active", 2, false, 7, 2, 1)));
        Assert.Equal(2, tracker.Depth(new("Below cycle", "active", 3, false, 7, 4, 1)));
        tracker.Update(new("Cycle A", "done", 100, true, 7, 1, 2));
        Assert.Equal(new ProgressUpdate[] { new("Foreign parent", "active", 10, false, 8, 99) }, tracker.Active);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    public void ProgressAcceptedPercentAndOperationValuesRemainExact(int percent)
    {
        var tracker = new ProgressTracker();
        tracker.Update(new("Copy", "transferring", percent, false, 11, 5, -1, 17, "file.txt"));
        Assert.Equal(new ProgressUpdate[]
        {
            new("Copy", "transferring", percent, false, 11, 5, -1, 17, "file.txt")
        }, tracker.Active);
        Assert.Equal(0, tracker.Depth(tracker.Active[0]));
    }

    [Theory]
    [InlineData(-2, 2, -1, false)]
    [InlineData(101, 2, -1, false)]
    [InlineData(50, -1, -2, false)]
    [InlineData(50, 2, 2, false)]
    [InlineData(-2, 1, -1, true)]
    [InlineData(101, 1, -1, true)]
    [InlineData(50, 1, 1, true)]
    public void ProgressInvalidUpdatesRejectWithoutChangingValidTree(int percent, int activity, int parent, bool completed)
    {
        var tracker = new ProgressTracker();
        tracker.Update(new("Kept", "active", 25, false, 7, 1));
        var error = Assert.Throws<ArgumentException>(
            () => tracker.Update(new("Invalid", "bad", percent, completed, 7, activity, parent)));
        Assert.Equal("progress", error.ParamName);
        Assert.Equal(new ProgressUpdate[] { new("Kept", "active", 25, false, 7, 1) }, tracker.Active);
    }

    [Fact]
    public void ProgressNullRejectsAndClearResetsWithoutMutatingPriorSnapshot()
    {
        var tracker = new ProgressTracker();
        tracker.Update(new("Kept", "active", 25, false, 7, 1));
        var prior = tracker.Active;
        Assert.Equal("progress", Assert.Throws<ArgumentNullException>(() => tracker.Update(null!)).ParamName);
        Assert.Equal(new ProgressUpdate[] { new("Kept", "active", 25, false, 7, 1) }, tracker.Active);
        tracker.Clear();
        tracker.Clear();
        Assert.Empty(tracker.Active);
        Assert.Equal(new ProgressUpdate[] { new("Kept", "active", 25, false, 7, 1) }, prior);
        tracker.Update(new("New", "started", 0, false, 8, 2, 1));
        Assert.Equal(new ProgressUpdate[] { new("New", "started", 0, false, 8, 2, 1) }, tracker.Active);
        Assert.Equal(0, tracker.Depth(tracker.Active[0]));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, -2)]
    [InlineData(1, -2)]
    public void ProgressZeroActivityNegativeSourceAndNegativeParentRemainExact(int activity, int parent)
    {
        var tracker = new ProgressTracker();
        tracker.Update(new("Negative source", "active", 1, false, -7, activity, parent));
        tracker.Update(new("Default source", "independent", 2, false, 0, activity, parent));
        Assert.Equal(new ProgressUpdate[]
        {
            new("Negative source", "active", 1, false, -7, activity, parent),
            new("Default source", "independent", 2, false, 0, activity, parent)
        }, tracker.Active);
        Assert.Equal(new[] { 0, 0 }, tracker.Active.Select(tracker.Depth).ToArray());
    }

    private sealed class RecoverySandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DtIseCore." + Guid.NewGuid().ToString("N"));

        public RecoverySandbox() => Directory.CreateDirectory(Root);
        public string Snapshot(Guid id) => Path.Combine(Root, id.ToString("N") + ".json");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Theory]
    [InlineData("{\n$x\n}", 0, 6)]
    [InlineData("@{\nA=1\n}", 0, 8)]
    [InlineData("#region r\n$x\n#endregion", 0, 23)]
    [InlineData("  #ReGiOn r\n$x\n  #EnDrEgIoN", 2, 27)]
    [InlineData("<#\nhi\n#>", 0, 8)]
    [InlineData("'a\nb'", 0, 5)]
    [InlineData("\"a\nb\"", 0, 5)]
    [InlineData("@'\na\n'@", 0, 7)]
    [InlineData("@\"\na\n\"@", 0, 7)]
    [InlineData("\"{\n}\"", 0, 5)]
    [InlineData("<#\n{\n}\n#>", 0, 9)]
    [InlineData("@'\n#region x\n'@", 0, 15)]
    [InlineData("@'\n'@", 0, 5)]
    public void AnalysisClosedMultilineConstructsHaveExactFolds(string text, int start, int end)
    {
        var analysis = EditorAnalysis.Analyze(text);
        Assert.False(analysis.IsXml);
        Assert.Empty(analysis.Errors);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(new[] { (start, end) }, analysis.Folds);
    }

    [Fact]
    public void AnalysisNestedRegionsAndBracesAreSortedByAuthoredOffsets()
    {
        const string text = "#region outer\n{\n#region inner\n$x\n#endregion\n}\n#endregion";
        var analysis = EditorAnalysis.Analyze(text);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(new[] { (0, 56), (14, 45), (16, 43) }, analysis.Folds);
    }

    [Theory]
    [InlineData("{ $x }")]
    [InlineData("#regionElse\n$x\n#endregion")]
    [InlineData("$x = 1 #region r\n$x\n#endregion")]
    [InlineData("#region outer\n#region inner\n$x\n#endregion")]
    [InlineData("#endregion\n$x")]
    [InlineData("<#\nunfinished")]
    [InlineData("@'\nunfinished")]
    [InlineData("'unfinished\n")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("'{}'")]
    [InlineData("# {\n# }")]
    [InlineData("<##>")]
    [InlineData("<# single line #>")]
    public void AnalysisDoesNotFoldSingleLineInvalidDirectivesOrUnclosedConstructs(string text)
    {
        Assert.Empty(EditorAnalysis.Analyze(text).Folds);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("xml", false)]
    [InlineData("a.xml.ps1", false)]
    [InlineData("a.ps1", false)]
    [InlineData("folder.xml/a.ps1", false)]
    [InlineData("folder/a.XmL", true)]
    [InlineData("a.PS1XML", true)]
    [InlineData(".xml", true)]
    public void XmlDocumentModeUsesOnlyTheFinalCaseInsensitiveExtension(string? path, bool expected)
    {
        Assert.Equal(expected, EditorAnalysis.IsXmlDocument(path));
        Assert.Equal(expected, EditorAnalysis.Analyze("<r/>", path).IsXml);
    }

    [Fact]
    public void XmlAnalysisHasExactTagAttributeQuotedValueCommentAndFoldRanges()
    {
        const string text = "<r a=\"v\">\n<!--x\ny-->\n<c/>\n</r>";
        var analysis = EditorAnalysis.Analyze(text, "owned.ps1xml");
        Assert.True(analysis.IsXml);
        Assert.Empty(analysis.Tokens);
        Assert.Empty(analysis.Errors);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(new XmlTokenSpan[]
        {
            new(1, 2, "Tag"), new(3, 4, "Attribute"), new(5, 8, "Value"),
            new(10, 20, "Comment"), new(22, 23, "Tag"), new(28, 29, "Tag")
        }, analysis.XmlTokens);
        Assert.Equal(new[] { (0, 30), (10, 20) }, analysis.Folds);
    }

    [Theory]
    [InlineData("", 0, 0, "Root element is missing.")]
    [InlineData("<r>\n</x>", 6, 7, "does not match")]
    [InlineData("<r", 0, 1, "Data at the root level is invalid")]
    [InlineData("<r a=1/>", 5, 6, "unexpected token")]
    [InlineData("<!DOCTYPE r [<!ENTITY x 'owned'>]><r>&x;</r>", 0, 1, "DTD is prohibited")]
    public void XmlDiagnosticsUseInvalidXmlAndClampTheOneCharacterSpan(
        string text, int start, int end, string messagePart)
    {
        var analysis = EditorAnalysis.Analyze(text, "owned.xml");
        var diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("InvalidXml", diagnostic.Code);
        Assert.Equal(start, diagnostic.Start);
        Assert.Equal(end, diagnostic.End);
        Assert.Contains(messagePart, diagnostic.Message, StringComparison.Ordinal);
        // The diagnostic policy preserves XmlReader's complete (potentially localized) message.
        // This oracle calls the framework, not EditorAnalysis or a production escaping helper.
        var xmlError = Assert.Throws<System.Xml.XmlException>(() =>
        {
            using var reader = System.Xml.XmlReader.Create(new StringReader(text), new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null
            });
            while (reader.Read()) { }
        });
        Assert.Equal(xmlError.Message, diagnostic.Message);
        Assert.Empty(analysis.Errors);
        Assert.Empty(analysis.Folds);
    }

    [Fact]
    public void PowerShellDiagnosticsRetainParserCodeMessageAndExactIncompleteExtent()
    {
        var analysis = EditorAnalysis.Analyze("'unterminated");
        var error = Assert.Single(analysis.Errors);
        Assert.Equal("TerminatorExpectedAtEndOfString", error.ErrorId);
        Assert.True(error.IncompleteInput);
        Assert.Equal(0, error.Extent.StartOffset);
        Assert.Equal(13, error.Extent.EndOffset);
        var diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("TerminatorExpectedAtEndOfString", diagnostic.Code);
        Assert.Equal(error.Message, diagnostic.Message);
        Assert.Equal(0, diagnostic.Start);
        Assert.Equal(13, diagnostic.End);
        Assert.Empty(analysis.Folds);
        Assert.Equal("text", Assert.Throws<ArgumentNullException>(() => EditorAnalysis.Analyze(null!)).ParamName);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, "real")]
    [InlineData(4, "real")]
    [InlineData(5, null)]
    [InlineData(10, null)]
    [InlineData(20, null)]
    [InlineData(21, "nested")]
    [InlineData(27, "nested")]
    [InlineData(28, null)]
    [InlineData(32, null)]
    [InlineData(41, "last")]
    [InlineData(45, "last")]
    [InlineData(46, null)]
    public void VariableLookupHonorsTokenExtentsAndIgnoresCommentsAndLiteralStrings(int offset, string? name)
    {
        const string text = "$real; '# $fake'; \"x $nested\" # $comment\n$last";
        var result = EditorAnalysis.VariableAtOffset(EditorAnalysis.Analyze(text).Tokens, offset);
        if (name is null) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            Assert.Equal(name, result.VariablePath.UserPath);
            Assert.Equal(name == "real" ? 0 : name == "nested" ? 21 : 41, result.Extent.StartOffset);
            Assert.Equal(name == "real" ? 5 : name == "nested" ? 28 : 46, result.Extent.EndOffset);
        }
    }

    [Theory]
    [InlineData(1, 1, 0, 6)]
    [InlineData(1, 9, 8, 24)]
    [InlineData(4, 1, 25, 40)]
    [InlineData(1, 2, -1, -1)]
    [InlineData(1, 7, -1, -1)]
    [InlineData(2, 3, 17, 22)]
    [InlineData(3, 1, -1, -1)]
    [InlineData(4, 16, -1, -1)]
    [InlineData(0, 1, -1, -1)]
    [InlineData(1, 0, -1, -1)]
    [InlineData(5, 1, -1, -1)]
    [InlineData(1, 100, -1, -1)]
    public void StatementLookupRequiresAnExactStatementStartAndKeepsMultilineExtent(
        int line, int column, int start, int end)
    {
        const string text = "$x = 1; $y = (\n  2 + 3\n)\nWrite-Output $y";
        var statement = EditorAnalysis.StatementAtPosition(text, line, column);
        if (start < 0) Assert.Null(statement);
        else Assert.Equal((start, end), statement);
    }

    [Theory]
    [InlineData("\n", 7)]
    [InlineData("\r", 7)]
    [InlineData("\r\n", 8)]
    public void StatementLookupHandlesEachLineDelimiterWithoutMovingAdjacentStatements(string newline, int start)
    {
        Assert.Equal((start, start + 6), EditorAnalysis.StatementAtPosition("$x = 1" + newline + "$y = 2", 2, 1));
        Assert.Null(EditorAnalysis.StatementAtPosition("", 1, 1));
    }

    [Theory]
    [InlineData("Dark Console, Light Editor (default)", "#012456", "#F5F5F5", "#FFFFFF", "#000000", "#A82D00", "#696969", 9d)]
    [InlineData("Light Console, Dark Editor", "#FFFFFF", "#626262", "#012456", "#F5F5F5", "#FF4500", "#D3D3D3", 9d)]
    [InlineData("Dark Console, Dark Editor", "#012456", "#F5F5F5", "#012456", "#F5F5F5", "#FF4500", "#D3D3D3", 9d)]
    [InlineData("Light Console, Light Editor", "#FFFFFF", "#626262", "#FFFFFF", "#000000", "#FF4500", "#A9A9A9", 9d)]
    [InlineData("Monochrome Green", "#000000", "#00FF00", "#000000", "#00FF00", "#00BF00", "#007F00", 11d)]
    [InlineData("Presentation", "#000000", "#F5F5F5", "#FFFFFF", "#000000", "#FF4500", "#A9A9A9", 20d)]
    public void OriginalBuiltInThemePaletteMatrix(string name, string consoleBackground, string consoleForeground,
        string scriptBackground, string scriptForeground, string variable, string scriptOperator, double points)
    {
        var theme = EditorThemePresets.Original(name);
        Assert.Equal(name, theme.Name);
        Assert.Equal(consoleBackground, theme.Colors["Console.Background"]);
        Assert.Equal(consoleForeground, theme.Colors["Console.Foreground"]);
        Assert.Equal(scriptBackground, theme.Colors["Script.Background"]);
        Assert.Equal(scriptForeground, theme.Colors["Script.Foreground"]);
        Assert.Equal(variable, theme.Colors["Script.Variable"]);
        Assert.Equal(scriptOperator, theme.Colors["Script.Operator"]);
        Assert.Equal("#8B0000", theme.Colors["Xml.Tag"]);
        Assert.Equal("#FF0000", theme.Colors["Xml.Attribute"]);
        Assert.Equal("#00008B", theme.Colors["Xml.Value"]);
        Assert.Equal(points, EditorThemePresets.FontSizePoints(name));
        theme.Colors["Script.Variable"] = "#123456";
        Assert.Equal(variable, EditorThemePresets.Original(name).Colors["Script.Variable"]);
    }

    [Fact]
    public void OriginalThemeCatalogHasExactOrderAndIndependentInstances()
    {
        string[] names =
        [
            "Dark Console, Light Editor (default)", "Light Console, Dark Editor", "Dark Console, Dark Editor",
            "Light Console, Light Editor", "Monochrome Green", "Presentation"
        ];
        Assert.Equal(names, EditorThemePresets.OriginalNames);
        var themes = EditorThemePresets.OriginalBuiltIns();
        Assert.Equal(names, themes.Select(theme => theme.Name).ToArray());
        themes[0].Colors["Script.Background"] = "#123456";
        Assert.Equal("#FFFFFF", themes[3].Colors["Script.Background"]);
        Assert.Equal("#FFFFFF", EditorThemePresets.OriginalBuiltIns()[0].Colors["Script.Background"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Dark")]
    [InlineData("presentation")]
    [InlineData("Future Theme")]
    public void OriginalThemeResolverRejectsUnknownAndAliasNamesWithoutSilentFallback(string name)
    {
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => EditorThemePresets.Original(name)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => EditorThemePresets.FontSizePoints(name)).ParamName);
    }

    [Theory]
    [InlineData("Dark Console, Light Editor (default)")]
    [InlineData("Light Console, Dark Editor")]
    [InlineData("Dark Console, Dark Editor")]
    [InlineData("Light Console, Light Editor")]
    [InlineData("Monochrome Green")]
    [InlineData("Presentation")]
    [InlineData("Follow DT")]
    public async Task ThemePresetPersistsAlongsideDetachedNestedSettings(string name)
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "theme.json");
        var settings = new UserSettings
        {
            ThemePreset = name, Zoom = 175, FontSize = 16, LoadProfiles = true,
            Theme = new EditorTheme { Name = "Owned palette" },
            RecentFiles = ["owned.ps1"], CustomThemes = [new EditorTheme { Name = "Custom retained" }]
        };
        var copy = settings.Copy();
        copy.Theme.Colors["Script.Foreground"] = "#123456";
        copy.CustomThemes[0].Name = "Changed copy";
        copy.RecentFiles.Clear();
        Assert.Equal("#000000", settings.Theme.Colors["Script.Foreground"]);
        Assert.Equal("Custom retained", settings.CustomThemes[0].Name);
        Assert.Equal(new[] { "owned.ps1" }, settings.RecentFiles);
        await settings.SaveAsync(path);
        var loaded = await UserSettings.LoadAsync(path);
        Assert.Equal(name, loaded.ThemePreset);
        Assert.Equal("Owned palette", loaded.Theme.Name);
        Assert.Equal(175, loaded.Zoom);
        Assert.Equal(16, loaded.FontSize);
        Assert.True(loaded.LoadProfiles);
        Assert.Equal(new[] { "owned.ps1" }, loaded.RecentFiles);
        Assert.Equal("Custom retained", Assert.Single(loaded.CustomThemes).Name);
        Assert.Equal(new[] { path }, Directory.GetFiles(sandbox.Root));
    }

    [Fact]
    public async Task ScriptFileNotificationsPinChangedOnlyTextEncodingDirtyTitleRevertAndSave()
    {
        using var sandbox = new RecoverySandbox();
        var file = ScriptFile.CreateUntitled("owned.ps1");
        var events = new List<(object? Source, string? Name)>();
        file.PropertyChanged += (sender, args) => events.Add((sender, args.PropertyName));
        file.Text = "$x = 1";
        Assert.All(events, entry => Assert.Same(file, entry.Source));
        Assert.Equal(new[] { "Text", "IsDirty", "Title" }, events.Select(entry => entry.Name).ToArray());
        Assert.True(file.IsDirty);
        Assert.Equal("owned.ps1*", file.Title);
        events.Clear();
        file.Text = "$x = 1";
        Assert.Empty(events);
        file.Text = "";
        Assert.Equal(new[] { "Text", "IsDirty", "Title" }, events.Select(entry => entry.Name).ToArray());
        Assert.False(file.IsDirty);
        Assert.Equal("owned.ps1", file.Title);
        events.Clear();
        file.SetEncoding(new ScriptEncoding(65001, true));
        Assert.Equal(new[] { "EncodingChoice", "EncodingName", "IsDirty", "Title" },
            events.Select(entry => entry.Name).ToArray());
        Assert.True(file.IsDirty);
        Assert.Equal("owned.ps1*", file.Title);
        events.Clear();
        file.SetEncoding(new ScriptEncoding(65001, true));
        Assert.Empty(events);
        file.SetEncoding(new ScriptEncoding(65001, false));
        Assert.False(file.IsDirty);
        Assert.Equal("owned.ps1", file.Title);
        file.Text = "café";
        events.Clear();
        var path = Path.Combine(sandbox.Root, "saved.ps1");
        await file.SaveAsync(path);
        Assert.Equal(new[] { "Path", "Name", "Title", "IsDirty", "SavedVersion", "SavedEncodingChoice" },
            events.Select(entry => entry.Name).ToArray());
        Assert.All(events, entry => Assert.Same(file, entry.Source));
        Assert.Equal("saved.ps1", file.Title);
        Assert.False(file.IsDirty);
        Assert.Equal(new byte[] { 99, 97, 102, 195, 169 }, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("", 0, null)]
    [InlineData("# Get-Date", 4, null)]
    [InlineData("'Get-Date'", 4, null)]
    [InlineData("Write-Output 'Get-Date'", 17, "Write-Output")]
    [InlineData("Write-Output (Get-Date)", 18, "Get-Date")]
    [InlineData("Get-Date | Write-Output", 17, "Write-Output")]
    [InlineData("Get-Date -Format o", 13, "Get-Date")]
    public void CommandContextForHelpFindsTheSmallestEnclosingCommandRatherThanAnArgument(
        string text, int caret, string? command)
    {
        Assert.Equal(command, EditorAnalysis.CommandNameAtCaret(text, caret));
    }

    [Theory]
    [InlineData("{ $x }", 0, 0, 5)]
    [InlineData("{ $x }", 1, 0, 5)]
    [InlineData("{ $x }", 5, 0, 5)]
    [InlineData("{ $x }", 6, 0, 5)]
    [InlineData("{ $x }", 3, -1, -1)]
    [InlineData("'{}'", 1, -1, -1)]
    [InlineData("# {}", 2, -1, -1)]
    [InlineData("{", 0, -1, -1)]
    [InlineData("@{ A=1 }", 1, 1, 7)]
    public void BraceContextIgnoresLiteralAndCommentCharactersAndSupportsAdjacentCaret(
        string text, int caret, int open, int close)
    {
        var result = EditorAnalysis.MatchingBrace(text, caret);
        if (open < 0) Assert.Null(result);
        else Assert.Equal((open, close), result);
    }

    [Theory]
    [InlineData("Dark Console, Light Editor (default)",
        "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,696969,8B0000,006161,A82D00,0000FF",
        "B0C4DE,E0FFFF,EE82EE,FFE4B5,98FB98,E0FFFF,E0FFFF,F5F5F5,FFE4C4,D3D3D3,DB7093,8FBC8F,FF4500,E0FFFF",
        "FF9494,FF8C00,00FFFF,00FFFF", "#012456")]
    [InlineData("Light Console, Dark Editor",
        "B0C4DE,E0FFFF,EE82EE,FFE4B5,98FB98,E0FFFF,E0FFFF,F5F5F5,FFE4C4,D3D3D3,DB7093,8FBC8F,FF4500,E0FFFF",
        "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,A9A9A9,8B0000,008080,FF4500,0000FF",
        "E50000,B26200,007F7F,007F7F", "#012456")]
    [InlineData("Dark Console, Dark Editor",
        "B0C4DE,E0FFFF,EE82EE,FFE4B5,98FB98,E0FFFF,E0FFFF,F5F5F5,FFE4C4,D3D3D3,DB7093,8FBC8F,FF4500,E0FFFF",
        "B0C4DE,E0FFFF,EE82EE,FFE4B5,98FB98,E0FFFF,E0FFFF,F5F5F5,FFE4C4,D3D3D3,DB7093,8FBC8F,FF4500,E0FFFF",
        "FF9494,FF8C00,00FFFF,00FFFF", "#012456")]
    [InlineData("Light Console, Light Editor",
        "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,A9A9A9,8B0000,008080,FF4500,0000FF",
        "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,A9A9A9,8B0000,008080,FF4500,0000FF",
        "E50000,B26200,007F7F,007F7F", "#012456")]
    [InlineData("Monochrome Green",
        "009F00,00BF00,00DF00,00FF00,007F00,00DF00,00FF00,00DF00,009F00,007F00,00DF00,00FF00,00BF00,00BF00",
        "00FF00,00DF00,00BF00,009F00,007F00,00DF00,00DF00,00BF00,009F00,009F00,00FF00,009F00,00BF00,00DF00",
        "FF0000,FF8C00,0000FF,0000FF", "#000000")]
    [InlineData("Presentation",
        "00BFFF,0000FF,8A2BE2,000080,006400,00008B,00008B,000000,800080,A9A9A9,8B0000,008080,FF4500,0000FF",
        "B0C4DE,E0FFFF,EE82EE,FFE4B5,98FB98,E0FFFF,E0FFFF,F5F5F5,FFE4C4,D3D3D3,DB7093,8FBC8F,FF4500,E0FFFF",
        "FF0000,FF8C00,0000FF,0000FF", "#000000")]
    public void OriginalThemeTokenAndOutputStreamPalettesAreIndependentlySpecifiedForEveryPreset(
        string name, string scriptColors, string consoleColors, string streamColors, string textBackground)
    {
        var theme = EditorThemePresets.Original(name);
        string[] categories =
        [
            "Attribute", "Command", "CommandArgument", "Parameter", "Comment", "Keyword", "Label",
            "Member", "Number", "Operator", "String", "Type", "Variable", "Function"
        ];
        var script = scriptColors.Split(',');
        var console = consoleColors.Split(',');
        Assert.Equal(14, script.Length);
        Assert.Equal(14, console.Length);
        for (var index = 0; index < categories.Length; index++)
        {
            Assert.Equal("#" + script[index], theme.Colors["Script." + categories[index]]);
            Assert.Equal("#" + console[index], theme.Colors["Console." + categories[index]]);
        }
        Assert.Equal(streamColors.Split(',').Select(color => "#" + color).ToArray(),
            new[] { "Error", "Warning", "Verbose", "Debug" }.Select(kind => theme.Colors["Stream." + kind]).ToArray());
        Assert.Equal(textBackground, theme.Colors["Console.TextBackground"]);
        Assert.Equal("#006400", theme.Colors["Xml.Comment"]);
    }

    [Fact]
    public void AdjacentClosedRegionsHaveIndependentSortedFolds()
    {
        const string text = "#region a\n$x\n#endregion\n#region b\n$y\n#endregion";
        var analysis = EditorAnalysis.Analyze(text);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(new[] { (0, 23), (24, 47) }, analysis.Folds);
    }

    [Fact]
    public void XmlAnalysisSeparatesDeclarationAndCdataTokensAndKeepsNestedMultilineFolds()
    {
        var declaration = EditorAnalysis.Analyze("<?xml version='1.0'?>\n<r/>", "owned.xml");
        Assert.Empty(declaration.Diagnostics);
        Assert.Equal(new XmlTokenSpan[] { new(0, 21, "Tag"), new(23, 24, "Tag") }, declaration.XmlTokens);
        Assert.Empty(declaration.Folds);
        var cdata = EditorAnalysis.Analyze("<r><![CDATA[x\ny]]></r>", "owned.xml");
        Assert.Empty(cdata.Diagnostics);
        Assert.Equal(new XmlTokenSpan[] { new(1, 2, "Tag"), new(3, 18, "Value"), new(20, 21, "Tag") }, cdata.XmlTokens);
        Assert.Equal(new[] { (0, 22), (3, 18) }, cdata.Folds);
    }

    [Fact]
    public void NestedAndAdjacentBraceFoldsUseExactIndependentOffsets()
    {
        var nested = EditorAnalysis.Analyze("{\n{\n$x\n}\n}");
        Assert.Empty(nested.Diagnostics);
        Assert.Equal(new[] { (0, 10), (2, 8) }, nested.Folds);
        var adjacent = EditorAnalysis.Analyze("{\n$x\n}\n{\n$y\n}");
        Assert.Empty(adjacent.Diagnostics);
        Assert.Equal(new[] { (0, 6), (7, 13) }, adjacent.Folds);
    }

    [Theory]
    [InlineData("", 0, null)]
    [InlineData("$", 0, null)]
    [InlineData("$", 2, null)]
    [InlineData("$", -1, null)]
    [InlineData("$", 1, "Variable")]
    [InlineData("# $", 3, null)]
    [InlineData("'$'", 2, null)]
    [InlineData("\"$\"", 2, "Variable")]
    [InlineData("@'\n$\n'@", 4, null)]
    [InlineData("@\"\n$\n\"@", 4, "Variable")]
    [InlineData("\"word\"", 3, null)]
    [InlineData("Get-", 4, "Command,ParameterName")]
    [InlineData("Get-Item -", 10, "Command,ParameterName")]
    [InlineData("[string]::", 10, "Method,Property")]
    [InlineData("$x.", 3, "Method,Namespace,Property,Type")]
    [InlineData("1.", 2, null)]
    [InlineData("1 .", 3, null)]
    [InlineData("*.", 2, null)]
    [InlineData("?.", 2, null)]
    [InlineData(" .", 2, null)]
    [InlineData("[", 1, "Namespace,Type")]
    [InlineData("Get-Item -Path ", 15, "ParameterValue")]
    [InlineData("Get-Item ", 9, null)]
    [InlineData("dir/", 4, "ProviderContainer,ProviderItem")]
    [InlineData("dir\\", 4, "ProviderContainer,ProviderItem")]
    [InlineData("'dir/'", 5, "ProviderContainer,ProviderItem")]
    [InlineData("\"dir\\\"", 5, "ProviderContainer,ProviderItem")]
    [InlineData("# dir/", 6, null)]
    [InlineData("word", 4, null)]
    public void AutomaticCompletionContextReturnsExactSupportedCategoriesOrNull(
        string text, int caret, string? categories)
    {
        var filter = EditorAnalysis.CompletionFilter(text, caret);
        if (categories is null)
            Assert.Null(filter);
        else
        {
            Assert.NotNull(filter);
            Assert.Equal(categories.Split(','), filter.Select(category => category.ToString()).Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData(@"..\escape")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\0b")]
    [InlineData("a.")]
    [InlineData("a ")]
    public async Task DirectSnippetCreateRejectsFilenameTitlesWithoutPublishing(string? title)
    {
        using var sandbox = new RecoverySandbox();
        var directory = Path.Combine(sandbox.Root, "catalog");
        var service = new IseSnippetService(directory);
        var error = Assert.ThrowsAny<ArgumentException>(() => service.Create(title!, "desc", "abc", "author", 0, false));
        Assert.Equal("title", error.ParamName);
        Assert.Empty(service.GetUserFiles());
        Assert.Empty((await service.LoadAsync()).Snippets);
        Assert.Empty(Directory.GetFileSystemEntries(sandbox.Root));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(-2)]
    [InlineData(5)]
    public void DirectSnippetCreateRejectsCaretOutsideTextBeforeCreatingDirectory(int caret)
    {
        using var sandbox = new RecoverySandbox();
        var service = new IseSnippetService(Path.Combine(sandbox.Root, "catalog"));
        Assert.Equal("caretOffset", Assert.Throws<ArgumentOutOfRangeException>(
            () => service.Create("Owned", "desc", "abc", "author", caret, true)).ParamName);
        Assert.Empty(Directory.GetFileSystemEntries(sandbox.Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task DirectSnippetCreateForceReplacesAndNoForcePreservesExactBytes(int caret)
    {
        using var sandbox = new RecoverySandbox();
        var service = new IseSnippetService(Path.Combine(sandbox.Root, "catalog"));
        Assert.Equal(Path.Combine(sandbox.Root, "catalog"), service.UserDirectory);
        service.Create("Owned café &", "old <description>", "abc", "β", caret, false);
        var path = Assert.Single(service.GetUserFiles()).FullName;
        Assert.Equal(Path.Combine(service.UserDirectory, "Owned café &.snippets.ps1xml"), path);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(new PowerShellSnippet("Owned café &", "old <description>", "β", "abc", caret, false),
            Assert.Single((await service.LoadAsync()).Snippets));
        Assert.Throws<IOException>(() => service.Create("Owned café &", "new", "xyz", "new author", 1, false));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(service.UserDirectory));
        service.Create("Owned café &", "new", "xyz", "new author", 1, true);
        Assert.Equal(new PowerShellSnippet("Owned café &", "new", "new author", "xyz", 1, false),
            Assert.Single((await service.LoadAsync()).Snippets));
        Assert.NotEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(service.UserDirectory));
    }

    [Theory]
    [InlineData("invalid-xml")]
    [InlineData("locked-destination")]
    [InlineData("directory-destination")]
    public async Task DirectSnippetCreateFailedReplacementPreservesBaselineAndCleansTemporaryFile(string failure)
    {
        using var sandbox = new RecoverySandbox();
        var service = new IseSnippetService(Path.Combine(sandbox.Root, "catalog"));
        service.Create("Kept", "baseline", "old", "author", 2, false);
        var baseline = Assert.Single(service.GetUserFiles()).FullName;
        var bytes = await File.ReadAllBytesAsync(baseline);
        if (failure == "directory-destination")
        {
            var collision = Path.Combine(service.UserDirectory, "Blocked.snippets.ps1xml");
            Directory.CreateDirectory(collision);
            Assert.Throws<UnauthorizedAccessException>(() => service.Create("Blocked", "new", "new", "author", 1, true));
            Assert.Empty(Directory.GetFileSystemEntries(collision));
        }
        else if (failure == "locked-destination")
        {
            using var held = new FileStream(baseline, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Throws<UnauthorizedAccessException>(() => service.Create("Kept", "new", "new", "author", 1, true));
        }
        else
            Assert.Throws<ArgumentException>(() => service.Create("Kept", "new", "\0", "author", 0, true));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(baseline));
        Assert.Equal(new[] { baseline }, Directory.GetFiles(service.UserDirectory));
        Assert.Equal(new PowerShellSnippet("Kept", "baseline", "author", "old", 2, false),
            Assert.Single((await service.LoadAsync()).Snippets));
    }

    private static string Phase4SnippetXml(string title = "Owned", string code = "abc",
        string version = "1.0.0", string attributes = "") =>
        "<Snippets xmlns=\"http://schemas.microsoft.com/PowerShell/Snippets\"><Snippet Version=\"" + version + "\">" +
        "<Header><Title>" + title + "</Title><Description>desc</Description><Author>author</Author></Header>" +
        "<Code><Script Language=\"PowerShell\"" + attributes + ">" + code + "</Script></Code></Snippet></Snippets>";

    [Theory]
    [InlineData("root", "Expected a PowerShell Snippets document.")]
    [InlineData("namespace", "Expected a PowerShell Snippets document.")]
    [InlineData("empty", "The file contains no snippets.")]
    [InlineData("foreign-entry", "The file contains no snippets.")]
    [InlineData("version-missing", "Only snippet schema version 1.0.0 is supported.")]
    [InlineData("version-malformed", "Only snippet schema version 1.0.0 is supported.")]
    [InlineData("version-future", "Only snippet schema version 1.0.0 is supported.")]
    [InlineData("header", "A snippet is missing its Header.")]
    [InlineData("title", "A snippet is missing Title.")]
    [InlineData("description", "A snippet is missing Description.")]
    [InlineData("author", "A snippet is missing Author.")]
    [InlineData("blank-title", "A snippet title cannot be empty.")]
    [InlineData("whitespace-title", "A snippet title cannot be empty.")]
    [InlineData("code", "Snippet 'Owned' is missing its PowerShell Script.")]
    [InlineData("language-missing", "Snippet 'Owned' is missing its PowerShell Script.")]
    [InlineData("language-other", "Snippet 'Owned' is missing its PowerShell Script.")]
    [InlineData("caret-negative", "Snippet 'Owned' has an invalid CaretOffset.")]
    [InlineData("caret-over", "Snippet 'Owned' has an invalid CaretOffset.")]
    [InlineData("caret-overflow", "Snippet 'Owned' has an invalid CaretOffset.")]
    [InlineData("caret-text", "Snippet 'Owned' has an invalid CaretOffset.")]
    [InlineData("indent", "Snippet 'Owned' has an invalid Indent value.")]
    [InlineData("caret-negative-later", "Snippet 'Owned' has an invalid CaretOffset.")]
    [InlineData("caret-over-later", "Snippet 'Owned' has an invalid CaretOffset.")]
    public void SnippetSchemaRejectsEachDistinctInvalidPartition(string partition, string message)
    {
        var xml = Phase4SnippetXml();
        xml = partition switch
        {
            "root" => xml.Replace("<Snippets ", "<Other ").Replace("</Snippets>", "</Other>"),
            "namespace" => xml.Replace("http://schemas.microsoft.com/PowerShell/Snippets", "urn:owned"),
            "empty" => "<Snippets xmlns=\"http://schemas.microsoft.com/PowerShell/Snippets\"/>",
            "foreign-entry" => xml.Replace("<Snippet Version=", "<foreign:Snippet xmlns:foreign=\"urn:owned\" Version=")
                .Replace("</Snippet>", "</foreign:Snippet>"),
            "version-missing" => xml.Replace(" Version=\"1.0.0\"", ""),
            "version-malformed" => Phase4SnippetXml(version: "not-version"),
            "version-future" => Phase4SnippetXml(version: "1.0.1"),
            "header" => xml.Replace("<Header><Title>Owned</Title><Description>desc</Description><Author>author</Author></Header>", ""),
            "title" => xml.Replace("<Title>Owned</Title>", ""),
            "description" => xml.Replace("<Description>desc</Description>", ""),
            "author" => xml.Replace("<Author>author</Author>", ""),
            "blank-title" => Phase4SnippetXml(title: ""),
            "whitespace-title" => Phase4SnippetXml(title: " \t"),
            "code" => xml.Replace("<Code><Script Language=\"PowerShell\">abc</Script></Code>", ""),
            "language-missing" => xml.Replace(" Language=\"PowerShell\"", ""),
            "language-other" => xml.Replace("Language=\"PowerShell\"", "Language=\"CSharp\""),
            "caret-negative" => Phase4SnippetXml(attributes: " CaretOffset=\"-1\""),
            "caret-over" => Phase4SnippetXml(attributes: " CaretOffset=\"4\""),
            "caret-overflow" => Phase4SnippetXml(attributes: " CaretOffset=\"2147483648\""),
            "caret-text" => Phase4SnippetXml(attributes: " CaretOffset=\"NaN\""),
            "indent" => Phase4SnippetXml(attributes: " Indent=\"1\""),
            "caret-negative-later" => Phase4SnippetXml(attributes: " CaretOffset=\"-2\""),
            "caret-over-later" => Phase4SnippetXml(attributes: " CaretOffset=\"5\""),
            _ => throw new ArgumentException(partition)
        };
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => SnippetCatalog.Parse(xml)).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Snippets")]
    [InlineData("<!DOCTYPE Snippets [<!ENTITY owned 'local'>]><Snippets xmlns=\"http://schemas.microsoft.com/PowerShell/Snippets\">&owned;</Snippets>")]
    public void SnippetXmlMalformedAndDtdDocumentsRejectWithoutResolvingExternalData(string xml)
    {
        Assert.Throws<System.Xml.XmlException>(() => SnippetCatalog.Parse(xml));
    }

    [Theory]
    [InlineData("0.9", "", -1, true)]
    [InlineData("1.0", " CaretOffset=\"0\" Indent=\"false\"", 0, false)]
    [InlineData("1.0.0", " CaretOffset=\"3\" Indent=\"TRUE\"", 3, true)]
    public void SnippetSchemaAcceptsSupportedVersionsCaretEndpointsAndDefaults(
        string version, string attributes, int caret, bool indent)
    {
        var xml = Phase4SnippetXml(version: version, attributes: attributes)
            .Replace("Language=\"PowerShell\"", "Language=\"pOwErShElL\"")
            .Replace("<Script Language=", "<Script Language=\"CSharp\">ignored</Script><Script Language=");
        Assert.Equal(new PowerShellSnippet("Owned", "desc", "author", "abc", caret, indent),
            Assert.Single(SnippetCatalog.Parse(xml)));
    }

    [Theory]
    [InlineData(1_999_999, true)]
    [InlineData(2_000_000, true)]
    [InlineData(2_000_001, false)]
    public void SnippetXmlDocumentSizeEnforcesExactLimit(int size, bool accepted)
    {
        var prefix = Phase4SnippetXml(code: "");
        var xml = Phase4SnippetXml(code: new string('x', size - prefix.Length));
        Assert.Equal(size, xml.Length);
        if (accepted) Assert.Equal(size - prefix.Length, Assert.Single(SnippetCatalog.Parse(xml)).Code.Length);
        else Assert.Throws<System.Xml.XmlException>(() => SnippetCatalog.Parse(xml));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(9, true)]
    public void SnippetSerializeUsesIndependentXmlOracleForUnicodeEscapingAndAllNewlines(int caret, bool indent)
    {
        const string code = "α\rβ\nγ\r\n<&";
        var xml = SnippetCatalog.Serialize([new PowerShellSnippet("T<&", "D café", "A β", code, caret, indent)]);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/PowerShell/Snippets";
        Assert.Equal(ns + "Snippets", document.Root!.Name);
        var entry = Assert.Single(document.Root.Elements());
        Assert.Equal("1.0.0", entry.Attribute("Version")!.Value);
        Assert.Equal(new[] { "T<&", "D café", "A β" },
            entry.Element(ns + "Header")!.Elements().Select(e => e.Value));
        var script = Assert.Single(entry.Element(ns + "Code")!.Elements());
        Assert.Equal("PowerShell", script.Attribute("Language")!.Value);
        Assert.Equal(indent ? "true" : "false", script.Attribute("Indent")!.Value);
        Assert.Equal(caret < 0 ? null : caret.ToString(), script.Attribute("CaretOffset")?.Value);
        Assert.Equal(code, script.Value);
        Assert.Contains("&lt;&amp;", xml);
        Assert.Contains("&#xD;", xml);
        Assert.Equal(new PowerShellSnippet("T<&", "D café", "A β", code, caret, indent),
            Assert.Single(SnippetCatalog.Parse(xml)));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData(" ", 0)]
    [InlineData("Bad", -2)]
    [InlineData("Bad", 4)]
    [InlineData("Bad", -3)]
    [InlineData("Bad", 5)]
    public async Task SnippetSaveInvalidSecondEntryDoesNotReplaceOrCreateDirectory(string title, int caret)
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "kept.snippets.ps1xml");
        await File.WriteAllTextAsync(path, Phase4SnippetXml("Kept"));
        var bytes = await File.ReadAllBytesAsync(path);
        PowerShellSnippet[] batch = [new("Valid", "desc", "author", "abc"), new(title, "desc", "author", "abc", caret)];
        await Assert.ThrowsAsync<InvalidDataException>(() => SnippetCatalog.SaveAsync(path, batch));
        await Assert.ThrowsAsync<InvalidDataException>(() => SnippetCatalog.SaveAsync(Path.Combine(sandbox.Root, "missing", "new"), batch));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(sandbox.Root));
    }

    [Theory]
    [InlineData("a\nb", 1, true, "a\r\n  b", 1)]
    [InlineData("a\nb", 2, true, "a\r\n  b", 5)]
    [InlineData("a\rb", 2, true, "a\r\n  b", 5)]
    [InlineData("a\r\nb", 2, true, "a\r\n  b", 5)]
    [InlineData("a\r\nb", 3, true, "a\r\n  b", 5)]
    [InlineData("a\r\nb", -1, true, "a\r\n  b", 6)]
    [InlineData("a\nb", -2, false, "a\r\nb", 4)]
    [InlineData("α\nβ", 2, false, "α\r\nβ", 3)]
    [InlineData("", 0, true, "", 0)]
    [InlineData("a\n", 2, true, "a\r\n  ", 5)]
    public void SnippetExpandPinsDelimiterInteriorCaretIndentationAndEndPolicy(
        string code, int caret, bool indent, string expectedText, int expectedCaret)
    {
        var snippet = new PowerShellSnippet("T", "D", "A", code, caret, indent);
        Assert.Equal((expectedText, expectedCaret), snippet.Expand("  ", "\r\n"));
        Assert.Equal("T", snippet.DisplayTitle);
        Assert.Equal(code, snippet.Text);
        Assert.Equal("T", snippet.ToString());
    }

    [Fact]
    public void SnippetExpandRejectsCaretBeyondTextRatherThanSilentlyClamping()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerShellSnippet("T", "D", "A", "abc", 4).Expand("", "\n"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSnippetImportUsesOrdinalPathsRecordDistinctnessAndExplicitRecursion(bool recurse)
    {
        using var sandbox = new RecoverySandbox();
        var source = Path.Combine(sandbox.Root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "z.snippets.ps1xml"), Phase4SnippetXml("Zulu"));
        await File.WriteAllTextAsync(Path.Combine(source, "A.SNIPPETS.PS1XML"), Phase4SnippetXml("Alpha"));
        await File.WriteAllTextAsync(Path.Combine(source, "b.snippets.ps1xml"), Phase4SnippetXml("Alpha"));
        await File.WriteAllTextAsync(Path.Combine(source, "c.snippets.ps1xml"), Phase4SnippetXml("Alpha", "different"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "d.snippets.ps1xml"), Phase4SnippetXml("Nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "ignored.xml"), "<invalid/>");
        var service = new IseSnippetService(Path.Combine(sandbox.Root, "catalog"));
        service.Import(source, recurse);
        service.Import(Path.Combine(source, "A.SNIPPETS.PS1XML"), false);
        var loaded = await service.LoadAsync();
        Assert.Empty(loaded.Errors);
        Assert.Equal(recurse ? new[] { "Alpha", "Alpha", "Nested", "Zulu" } : new[] { "Alpha", "Alpha", "Zulu" },
            loaded.Snippets.Select(s => s.Title));
        Assert.Equal(recurse ? new[] { "abc", "different", "abc", "abc" } : new[] { "abc", "different", "abc" },
            loaded.Snippets.Select(s => s.Code));
        Assert.Empty(service.GetUserFiles());
        Assert.False(Directory.Exists(service.UserDirectory));
        Assert.Empty((await new IseSnippetService(service.UserDirectory).LoadAsync()).Snippets);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("multiple")]
    public async Task DirectSnippetImportEmptyDirectoryAndSingleOrMultipleEntriesDoNotCopyFiles(string partition)
    {
        using var sandbox = new RecoverySandbox();
        var source = Path.Combine(sandbox.Root, "source");
        Directory.CreateDirectory(source);
        var service = new IseSnippetService(Path.Combine(sandbox.Root, "catalog"));
        var path = Path.Combine(source, "one.snippets.ps1xml");
        var xml = Phase4SnippetXml("One");
        if (partition == "multiple") xml = xml.Replace("</Snippets>",
            "<Snippet Version=\"1.0.0\"><Header><Title>Two</Title><Description>desc</Description><Author>author</Author></Header>" +
            "<Code><Script Language=\"PowerShell\">abc</Script></Code></Snippet></Snippets>");
        if (partition != "empty") await File.WriteAllTextAsync(path, xml);
        service.Import(partition == "empty" ? source : path, false);
        Assert.Equal(partition == "empty" ? Array.Empty<string>() : partition == "single" ? new[] { "One" } : new[] { "One", "Two" },
            (await service.LoadAsync()).Snippets.Select(s => s.Title));
        Assert.Empty(service.GetUserFiles());
        if (partition != "empty") Assert.Equal(xml, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-extension")]
    [InlineData("nonlocal")]
    [InlineData("locked")]
    [InlineData("locked-later-file")]
    [InlineData("invalid-later-file")]
    [InlineData("invalid-later-entry")]
    public async Task DirectSnippetImportFailuresKeepPreviouslyImportedAndDiskEntriesAtomic(string partition)
    {
        using var sandbox = new RecoverySandbox();
        var service = new IseSnippetService(Path.Combine(sandbox.Root, "catalog"));
        service.Create("Disk", "desc", "abc", "author", 0, false);
        var diskPath = Assert.Single(service.GetUserFiles()).FullName;
        var bytes = await File.ReadAllBytesAsync(diskPath);
        var prior = Path.Combine(sandbox.Root, "prior.snippets.ps1xml");
        await File.WriteAllTextAsync(prior, Phase4SnippetXml("Prior"));
        service.Import(prior, false);
        var source = Path.Combine(sandbox.Root, "source");
        Directory.CreateDirectory(source);
        var first = Path.Combine(source, "a.snippets.ps1xml");
        await File.WriteAllTextAsync(first, Phase4SnippetXml("NeverPublished"));
        var lockedLater = Path.Combine(source, "z.snippets.ps1xml");
        if (partition == "locked-later-file") await File.WriteAllTextAsync(lockedLater, Phase4SnippetXml("Locked"));
        var path = partition switch
        {
            "missing" => Path.Combine(source, "missing.snippets.ps1xml"),
            "wrong-extension" => Path.Combine(source, "wrong.xml"),
            "nonlocal" => "urn:phase4:owned",
            "locked" => first,
            _ => source
        };
        if (partition == "wrong-extension") await File.WriteAllTextAsync(path, Phase4SnippetXml());
        if (partition == "invalid-later-file") await File.WriteAllTextAsync(Path.Combine(source, "z.snippets.ps1xml"), "<wrong/>");
        if (partition == "invalid-later-entry")
            await File.WriteAllTextAsync(first, Phase4SnippetXml("NeverPublished")
                .Replace("</Snippets>", "<Snippet Version=\"9.0\"/></Snippets>"));
        using (var held = partition is "locked" or "locked-later-file" ?
            new FileStream(partition == "locked" ? first : lockedLater, FileMode.Open, FileAccess.Read, FileShare.None) : null)
        {
            if (partition is "missing" or "wrong-extension" or "nonlocal")
                Assert.Equal(path, Assert.Throws<FileNotFoundException>(() => service.Import(path, true)).FileName);
            else if (partition is "locked" or "locked-later-file") Assert.Throws<IOException>(() => service.Import(path, true));
            else Assert.Throws<InvalidDataException>(() => service.Import(path, true));
        }
        var loaded = await service.LoadAsync();
        Assert.Empty(loaded.Errors);
        Assert.Equal(new[] { "Disk", "Prior" }, loaded.Snippets.Select(s => s.Title));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(diskPath));
        Assert.Equal(new[] { diskPath }, Directory.GetFiles(service.UserDirectory));
    }

    [Fact]
    public async Task SnippetLoadMergesDiskBeforeImportsAndReportsPathQualifiedErrorsWithoutLosingValidSiblings()
    {
        using var sandbox = new RecoverySandbox();
        var service = new IseSnippetService(Path.Combine(sandbox.Root, "catalog"));
        Directory.CreateDirectory(Path.Combine(service.UserDirectory, "nested"));
        var alpha = Path.Combine(service.UserDirectory, "A.SNIPPETS.PS1XML");
        var zulu = Path.Combine(service.UserDirectory, "z.snippets.ps1xml");
        var invalid = Path.Combine(service.UserDirectory, "nested", "bad.snippets.ps1xml");
        var unreadable = Path.Combine(service.UserDirectory, "nested", "locked.snippets.ps1xml");
        var malformed = Path.Combine(service.UserDirectory, "nested", "malformed.snippets.ps1xml");
        await File.WriteAllTextAsync(alpha, Phase4SnippetXml("Alpha"));
        await File.WriteAllTextAsync(zulu, Phase4SnippetXml("Zulu"));
        await File.WriteAllTextAsync(invalid, "<wrong/>");
        await File.WriteAllTextAsync(unreadable, Phase4SnippetXml("Locked"));
        await File.WriteAllTextAsync(malformed, "<Snippets");
        await File.WriteAllTextAsync(Path.Combine(service.UserDirectory, "ignored.txt"), "ignored");
        var imported = Path.Combine(sandbox.Root, "import.snippets.ps1xml");
        await File.WriteAllTextAsync(imported, Phase4SnippetXml("Alpha").Replace("</Snippets>",
            "<Snippet Version=\"1.0.0\"><Header><Title>Imported</Title><Description>desc</Description><Author>author</Author></Header><Code><Script Language=\"PowerShell\">xyz</Script></Code></Snippet></Snippets>"));
        service.Import(imported, false);
        using (var held = new FileStream(unreadable, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = await service.LoadAsync();
            Assert.Equal(new[] { "Alpha", "Zulu", "Imported" }, loaded.Snippets.Select(s => s.Title));
            Assert.Equal(3, loaded.Errors.Count);
            Assert.Equal(invalid + ": Expected a PowerShell Snippets document.", loaded.Errors[0]);
            Assert.StartsWith(unreadable + ": ", loaded.Errors[1]);
            Assert.StartsWith(malformed + ": ", loaded.Errors[2]);
            Assert.Equal(new[] { alpha, invalid, unreadable, malformed, zulu }, service.GetUserFiles().Select(f => f.FullName));
            var catalog = await SnippetCatalog.LoadAsync([Path.Combine(sandbox.Root, "missing"), service.UserDirectory, service.UserDirectory]);
            Assert.Equal(new[] { "Alpha", "Zulu" }, catalog.Snippets.Select(s => s.Title));
            Assert.Equal(loaded.Errors.Concat(loaded.Errors), catalog.Errors);
        }
        Assert.Equal("<wrong/>", await File.ReadAllTextAsync(invalid));
        Assert.Equal(new[] { "Alpha", "Locked", "Zulu", "Imported" }, (await service.LoadAsync()).Snippets.Select(s => s.Title));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DirectSnippetCreatePermitsEmptyCodeAndMetadataWithZeroCaret(string description)
    {
        using var sandbox = new RecoverySandbox();
        var service = new IseSnippetService(Path.Combine(sandbox.Root, "catalog"));
        service.Create(".Owned inner space", description, "", "", 0, false);
        var path = Assert.Single(service.GetUserFiles()).FullName;
        Assert.Equal(Path.Combine(service.UserDirectory, ".Owned inner space.snippets.ps1xml"), path);
        Assert.Equal(new PowerShellSnippet(".Owned inner space", description, "", "", 0, false),
            Assert.Single((await service.LoadAsync()).Snippets));
    }

    [Theory]
    [InlineData(true, "α\n--β\n--γ\n--δ", 12)]
    [InlineData(false, "α\nβ\nγ\nδ", 6)]
    public void SnippetExpandNormalizesEveryMixedDelimiterAndDoesNotIndentFirstLine(bool indent, string text, int caret)
    {
        var snippet = new PowerShellSnippet("T", "", "", "α\rβ\nγ\r\nδ", 7, indent);
        Assert.Equal((text, caret), snippet.Expand("--", "\n"));
    }

    [Fact]
    public async Task SnippetSaveDefaultOverwritePublishesUtf8WithoutBomAndNoOverwriteCleansStaging()
    {
        using var sandbox = new RecoverySandbox();
        var path = Path.Combine(sandbox.Root, "nested", "owned.snippets.ps1xml");
        await SnippetCatalog.SaveAsync(path, [new("Kept", "desc", "author", "old")]);
        var bytes = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<IOException>(() => SnippetCatalog.SaveAsync(path, [new("New", "", "", "α")], overwrite: false));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
        await SnippetCatalog.SaveAsync(path, [new("New", "", "", "α"), new("Second", "", "", "β", 0, false)]);
        var newBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal((byte)'<', newBytes[0]);
        Assert.Contains("α", System.Text.Encoding.UTF8.GetString(newBytes));
        var loaded = await SnippetCatalog.LoadAsync([Path.GetDirectoryName(path)!]);
        Assert.Empty(loaded.Errors);
        Assert.Equal(new PowerShellSnippet[] { new("New", "", "", "α"), new("Second", "", "", "β", 0, false) }, loaded.Snippets);
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void SnippetSerializeEmptyBatchIsEmptySchemaAndParseReportsNoSnippets()
    {
        var xml = SnippetCatalog.Serialize([]);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("{http://schemas.microsoft.com/PowerShell/Snippets}Snippets", document.Root!.Name.ToString());
        Assert.Empty(document.Root.Elements());
        Assert.Equal("The file contains no snippets.", Assert.Throws<InvalidDataException>(() => SnippetCatalog.Parse(xml)).Message);
    }

    [Fact]
    public void FreshLayoutSessionRetainsClassicCompatibilityAndTerminalDefaultWithoutOptIn()
    {
        var session = new Devolutions.Terminal.App.Models.TerminalSessionDescriptor
        {
            SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ProfileName = "Owned ordinary", Commandline = "cmd.exe", StartingDirectory = @"C:\owned"
        };
        Assert.Equal(Devolutions.Terminal.Settings.ProfileKind.Terminal, session.Kind);
        Assert.Equal("Classic ISE", session.IseColorTheme);
        Assert.False(session.IseLoadProfiles);
        var tab = new Devolutions.Terminal.App.Models.TabLayoutDescriptor
        {
            TabId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ActiveSessionId = session.SessionId, Root = new() { Session = session }
        };
        var layout = new Devolutions.Terminal.App.Models.TerminalWindowLayoutDescriptor { ActiveTabId = tab.TabId, Tabs = [tab] };
        var json = Devolutions.Terminal.App.Models.TerminalLayoutSerializer.SerializeTabs(layout);
        Assert.Equal("Classic ISE", json[0]!["tabs"]![0]!["root"]!["session"]!["iseColorTheme"]!.GetValue<string>());
        var restored = Devolutions.Terminal.App.Models.TerminalLayoutSerializer.DeserializeTabs(json)!;
        var actual = Assert.Single(restored.Tabs).Root.Session!;
        Assert.Equal("cmd.exe", actual.Commandline);
        Assert.Equal("Owned ordinary", actual.ProfileName);
        Assert.Equal(@"C:\owned", actual.StartingDirectory);
        Assert.Equal(session.SessionId, actual.SessionId);
        Assert.Equal(Devolutions.Terminal.Settings.ProfileKind.Terminal, actual.Kind);
        Assert.Equal("Classic ISE", actual.IseColorTheme);
        Assert.False(actual.IseLoadProfiles);
    }
}
