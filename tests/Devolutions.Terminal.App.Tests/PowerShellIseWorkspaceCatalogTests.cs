using System.Diagnostics;
using System.Text.Json;
using Devolutions.Terminal.App.Views;
using Iseberg.Core;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class PowerShellIseWorkspaceCatalogTests
{
    private const string Profile = "{11111111-1111-1111-1111-111111111111}";
    private static readonly Guid Workspace = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task RecordProfilePreservesExistingCaseInsensitiveIdentityAndRejectsForeignOwner()
    {
        using var fixture = new CatalogFixture();
        var directory = fixture.DirectoryFor(Workspace);
        Directory.CreateDirectory(directory);
        await PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, "Owned-Profile");
        var marker = Path.Combine(directory, "profile.json");
        Assert.Equal("\"Owned-Profile\"", await File.ReadAllTextAsync(marker));
        await File.WriteAllTextAsync(marker, " \r\n\"Owned-\\u0050rofile\"\t");
        var bytes = await File.ReadAllBytesAsync(marker);

        await PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, "Owned-Profile");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(marker));
        await PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, "owned-profile");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(marker));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, "Foreign-Profile"));
        Assert.Equal("This ISE workspace belongs to a different profile.", error.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(marker));
        Assert.Equal(new[] { marker }, Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData(".", null)]
    [InlineData("relative-workspace", null)]
    [InlineData(@"C:workspace", null)]
    [InlineData(@"\workspace", null)]
    [InlineData("relative-workspace", Profile)]
    public async Task RecordProfileRequiresAbsoluteWorkspaceBeforeProfileValidation(string path, string? profile)
    {
        using var fixture = new CatalogFixture();
        var workspace = profile is null ? path : Path.Combine("DtIseUncreated." + Guid.NewGuid().ToString("N"), path);
        Assert.False(Path.IsPathFullyQualified(workspace));
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => PowerShellIseWorkspaceCatalog.RecordProfileAsync(workspace, profile));

        Assert.Equal("workspaceDirectory", error.ParamName);
        Assert.Contains("An absolute workspace directory is required.", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
        if (profile is not null)
        {
            Assert.False(Directory.Exists(workspace));
            Assert.False(File.Exists(Path.Combine(workspace, "profile.json")));
        }
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    public async Task RecordProfileRejectsEmptyStoredOwnerWithoutRebindingOrLeavingTemporaryFiles(string identity)
    {
        using var fixture = new CatalogFixture();
        var directory = await fixture.CreateAsync(Workspace, Profile);
        var marker = Path.Combine(directory, "profile.json");
        await File.WriteAllTextAsync(marker, identity);
        var before = fixture.Bytes(directory);
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, Profile));

        Assert.Equal(identity, await File.ReadAllTextAsync(marker));
        fixture.AssertUnchanged(before);
        Assert.Equal(files, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ConcurrentProfileRecordsPublishOneOwnerAndNeverRebind()
    {
        using var fixture = new CatalogFixture();
        var directory = fixture.DirectoryFor(Workspace);
        Directory.CreateDirectory(directory);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = Enumerable.Range(0, 8).Select(async index =>
        {
            var profile = "Contender-" + index;
            await start.Task;
            var error = await Record.ExceptionAsync(
                () => PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, profile));
            return (Profile: profile, Error: error);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(records);
        var owner = Assert.Single(results, result => result.Error is null).Profile;
        foreach (var result in results.Where(result => result.Error is not null))
        {
            if (result.Error is IOException storageError)
                Assert.Contains(storageError.HResult & 0xffff, new[] { 17, 80, 183 });
            else
                Assert.IsType<InvalidOperationException>(result.Error);
        }

        var marker = Path.Combine(directory, "profile.json");
        Assert.Equal(owner, JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(marker)));
        var bytes = await File.ReadAllBytesAsync(marker);
        await PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, owner);
        foreach (var result in results.Where(result => result.Profile != owner))
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, result.Profile));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(marker));
        Assert.Equal(new[] { marker }, Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingProfileDoesNotCreateMarkerOrRecoverWorkspace(string? profile)
    {
        using var fixture = new CatalogFixture();
        var missing = Path.Combine(fixture.Root, "absent");
        await PowerShellIseWorkspaceCatalog.RecordProfileAsync(missing, profile);
        Assert.Null(await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(missing, profile));
        Assert.False(Directory.Exists(missing));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task SameProfileReleasedWorkspaceIsFoundWithoutConsumingOrRewritingData()
    {
        using var fixture = new CatalogFixture();
        var directory = await fixture.CreateAsync(Workspace, "Owned-Profile");
        var before = fixture.Bytes(directory);
        Assert.Equal(Workspace, await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, "owned-profile"));
        Assert.Equal(Workspace, await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, "OWNED-PROFILE"));
        fixture.AssertUnchanged(before);
        var recovered = Assert.Single(await new ScriptRecovery(Path.Combine(directory, "settings.json.recovery")).ReadAsync());
        Assert.Equal("owned draft", recovered.Text);
        Assert.Equal(0, recovered.OwnerProcessId);
        using var lease = new WorkbenchPersistenceLease(Path.Combine(directory, "settings.json"));
        Assert.Throws<IOException>(() => new WorkbenchPersistenceLease(Path.Combine(directory, "settings.json")));
    }

    [Fact]
    public async Task ForeignProfileAndInvalidDirectoryNamesAreNotConsumed()
    {
        using var fixture = new CatalogFixture();
        var foreign = await fixture.CreateAsync(Workspace, "foreign");
        var foreignBytes = fixture.Bytes(foreign);
        var invalid = Path.Combine(fixture.Root, "Workspaces", "not-a-workspace-id");
        Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(Path.Combine(invalid, "profile.json"), "{broken");
        Assert.Null(await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));

        var matching = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await fixture.CreateAsync(matching, Profile);
        Assert.Equal(matching, await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));
        fixture.AssertUnchanged(foreignBytes);
        Assert.Equal("{broken", await File.ReadAllTextAsync(Path.Combine(invalid, "profile.json")));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("snapshot")]
    [InlineData("lease")]
    public async Task LiveStateSnapshotOrLeaseProtectsWorkspaceUntilOwnershipIsReleased(string protection)
    {
        using var fixture = new CatalogFixture();
        var directory = await fixture.CreateAsync(Workspace, Profile);
        var settings = Path.Combine(directory, "settings.json");
        using var process = Process.GetCurrentProcess();
        if (protection == "state")
            await new WorkbenchStateStore(settings + ".workbench.json").SaveAsync(new()
            {
                OwnerProcessId = Environment.ProcessId,
                OwnerStartedUtc = process.StartTime.ToUniversalTime()
            });
        if (protection == "snapshot")
            await new ScriptRecovery(settings + ".recovery").SaveAsync(
                CatalogFixture.DraftId, ScriptFile.FromRecovery("draft.ps1", "owned draft"));
        var lease = protection == "lease" ? new WorkbenchPersistenceLease(settings) : null;
        try
        {
            var before = fixture.Bytes(directory, includeLock: false);
            Assert.Null(await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));
            fixture.AssertUnchanged(before);
        }
        finally { lease?.Dispose(); }

        await new WorkbenchStateStore(settings + ".workbench.json").SaveAsync(new());
        await new ScriptRecovery(settings + ".recovery").SaveAsync(
            CatalogFixture.DraftId, ScriptFile.FromRecovery("draft.ps1", "owned draft"), released: true);
        Assert.Equal(Workspace, await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));
    }

    [Fact]
    public async Task ReusedPidWithDifferentStartTimeDoesNotProtectAbandonedWorkspace()
    {
        using var fixture = new CatalogFixture();
        var directory = await fixture.CreateAsync(Workspace, Profile);
        using var process = Process.GetCurrentProcess();
        var staleStart = process.StartTime.ToUniversalTime().AddTicks(-1);
        var settings = Path.Combine(directory, "settings.json");
        await new WorkbenchStateStore(settings + ".workbench.json").SaveAsync(new()
        {
            OwnerProcessId = Environment.ProcessId, OwnerStartedUtc = staleStart
        });
        var snapshot = Path.Combine(settings + ".recovery", CatalogFixture.DraftId.ToString("N") + ".json");
        await File.WriteAllTextAsync(snapshot, JsonSerializer.Serialize(new RecoveredScript(
            CatalogFixture.DraftId, "draft.ps1", null, "owned draft", Environment.ProcessId, staleStart)));

        Assert.Equal(Workspace, await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));
        Assert.Equal("owned draft", Assert.Single(await new ScriptRecovery(settings + ".recovery").ReadAsync()).Text);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("snapshot")]
    [InlineData("marker")]
    public async Task MissingMetadataOrDirtySnapshotDoesNotOfferRecovery(string missing)
    {
        using var fixture = new CatalogFixture();
        var directory = await fixture.CreateAsync(Workspace, Profile);
        var path = missing switch
        {
            "state" => Path.Combine(directory, "settings.json.workbench.json"),
            "snapshot" => Path.Combine(directory, "settings.json.recovery", CatalogFixture.DraftId.ToString("N") + ".json"),
            _ => Path.Combine(directory, "profile.json")
        };
        File.Delete(path);
        var before = fixture.Bytes(directory);
        Assert.Null(await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));
        fixture.AssertUnchanged(before);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("profile.json")]
    [InlineData("settings.json.workbench.json")]
    [InlineData("settings.json.recovery")]
    public async Task CorruptCatalogStoragePropagatesWithoutOverwritingData(string target)
    {
        using var fixture = new CatalogFixture();
        var directory = await fixture.CreateAsync(Workspace, Profile);
        var path = target == "settings.json.recovery"
            ? Path.Combine(directory, target, CatalogFixture.DraftId.ToString("N") + ".json")
            : Path.Combine(directory, target);
        await File.WriteAllTextAsync(path, "{broken");
        var before = fixture.Bytes(directory);

        await Assert.ThrowsAsync<JsonException>(
            () => PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));
        fixture.AssertUnchanged(before);
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    private sealed class CatalogFixture : IDisposable
    {
        public static readonly Guid DraftId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DtIseCatalog." + Guid.NewGuid().ToString("N"));
        public CatalogFixture() => Directory.CreateDirectory(Root);
        public string DirectoryFor(Guid id) => Path.Combine(Root, "Workspaces", id.ToString("N"));

        public async Task<string> CreateAsync(Guid id, string profile)
        {
            var directory = DirectoryFor(id);
            Directory.CreateDirectory(directory);
            await PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, profile);
            var settings = Path.Combine(directory, "settings.json");
            await new WorkbenchStateStore(settings + ".workbench.json").SaveAsync(new());
            await new ScriptRecovery(settings + ".recovery").SaveAsync(
                DraftId, ScriptFile.FromRecovery("draft.ps1", "owned draft"), released: true);
            return directory;
        }

        public Dictionary<string, byte[]> Bytes(string directory, bool includeLock = true) =>
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => includeLock || !path.EndsWith(".lock", StringComparison.Ordinal))
                .ToDictionary(path => path, File.ReadAllBytes);

        public void AssertUnchanged(Dictionary<string, byte[]> expected)
        {
            foreach (var (path, bytes) in expected) Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public async Task NewestReleasedWorkspaceWinsAndLeasedNewestFallsBackWithoutConsumingEither()
    {
        using var fixture = new CatalogFixture();
        var olderId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var newerId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var older = await fixture.CreateAsync(olderId, Profile);
        var newer = await fixture.CreateAsync(newerId, Profile);
        using (new WorkbenchPersistenceLease(Path.Combine(older, "settings.json"))) { }
        using (new WorkbenchPersistenceLease(Path.Combine(newer, "settings.json"))) { }
        Directory.SetLastWriteTimeUtc(older, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(newer, new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var olderBytes = fixture.Bytes(older);
        var newerBytes = fixture.Bytes(newer);
        var files = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(newerId, await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));
        using (new WorkbenchPersistenceLease(Path.Combine(newer, "settings.json")))
            Assert.Equal(olderId, await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));
        Assert.Equal(newerId, await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));

        fixture.AssertUnchanged(olderBytes);
        fixture.AssertUnchanged(newerBytes);
        Assert.Equal(files, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("owned draft", Assert.Single(await new ScriptRecovery(Path.Combine(older, "settings.json.recovery")).ReadAsync()).Text);
        Assert.Equal("owned draft", Assert.Single(await new ScriptRecovery(Path.Combine(newer, "settings.json.recovery")).ReadAsync()).Text);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("find")]
    public async Task JsonNullProfileIdentityIsInvalidDataAndPreservesEveryFile(string operation)
    {
        using var fixture = new CatalogFixture();
        var directory = await fixture.CreateAsync(Workspace, Profile);
        var marker = Path.Combine(directory, "profile.json");
        await File.WriteAllTextAsync(marker, "null");
        var before = fixture.Bytes(directory);
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        var error = operation == "record"
            ? await Assert.ThrowsAsync<InvalidDataException>(() => PowerShellIseWorkspaceCatalog.RecordProfileAsync(directory, Profile))
            : await Assert.ThrowsAsync<InvalidDataException>(() => PowerShellIseWorkspaceCatalog.FindRecoverableAsync(fixture.Root, Profile));

        Assert.Equal("Empty ISE profile identity: " + marker, error.Message);
        Assert.Equal(new byte[] { 110, 117, 108, 108 }, await File.ReadAllBytesAsync(marker));
        fixture.AssertUnchanged(before);
        Assert.Equal(files, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }
}
