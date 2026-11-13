using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class ProgramStatusTests
{
    [Fact]
    public void QueryRepliesAndReportsCanBeSplitAcrossInputChunks()
    {
        using var engine = new TerminalEngine();
        var responses = new List<string>();
        engine.ResponseReady += (_, bytes) => responses.Add(Encoding.UTF8.GetString(bytes));
        engine.Feed("\u001b]7501;?");
        engine.Feed("\u001b");
        engine.Feed("\\");
        Assert.Equal("\u001b]7501;?\u001b\\", Assert.Single(responses));

        engine.Feed("\u001b]7501;state=blocked:kind=permission:app=terraform:msg=QXBwcm92ZQ");
        Assert.Empty(engine.ProgramStatus.Records);
        engine.Feed("\u0007");
        var record = engine.ProgramStatus.Records[string.Empty];
        Assert.Equal(TerminalProgramState.Blocked, record.State);
        Assert.Equal(TerminalProgramBlockKind.Permission, record.Kind);
        Assert.Equal("terraform", record.App);
        Assert.Equal("Approve", record.Message);
        Assert.Single(responses);
    }

    [Fact]
    public void ReportsReplaceRecordsAndInheritAppFromNearestExistingAncestor()
    {
        using var engine = new TerminalEngine();
        Send(engine, "state=working:app=deploy:progress=40:msg=SGVsbG8=");
        Send(engine, "state=working:id=build:app=builder");
        Send(engine, "state=blocked:id=build/test:kind=question:progress=85:title=VGVzdA");
        var before = engine.ProgramStatus;
        var child = before.Records["build/test"];
        Assert.Equal(TerminalProgramBlockKind.Question, child.Kind);
        Assert.Equal(85, child.Progress);
        Assert.Equal("Test", child.Title);
        Assert.Equal("builder", before.GetEffectiveApp("build/test"));

        Send(engine, "state=done:id=build");
        Assert.Null(engine.ProgramStatus.Records["build"].App);
        Assert.Equal("deploy", engine.ProgramStatus.GetEffectiveApp("build/test"));
        Assert.Equal("builder", before.GetEffectiveApp("build/test"));

        Send(engine, "state=done:kind=auth:progress=99");
        var root = engine.ProgramStatus.Records[string.Empty];
        Assert.Null(root.App);
        Assert.Null(root.Message);
        Assert.Null(root.Kind);
        Assert.Null(root.Progress);
    }

    [Fact]
    public void ClearRemovesOnlySubtreeAndRootClearRemovesAll()
    {
        using var engine = new TerminalEngine();
        foreach (var id in new[] { "build", "build/test", "builder", "build/test/unit" })
            Send(engine, $"state=working:id={id}");
        Send(engine, "state=clear:id=build");
        Assert.Equal("builder", Assert.Single(engine.ProgramStatus.Records).Key);
        Send(engine, "state=clear");
        Assert.Empty(engine.ProgramStatus.Records);
    }

    [Theory]
    [InlineData("state=working:id=")]
    [InlineData("state=working:id=a//b")]
    [InlineData("state=working:id=a!")]
    [InlineData("state=unknown")]
    [InlineData("app=cargo")]
    [InlineData("state=working:msg=/w==")]
    [InlineData("state=working:msg=!!!!:title=/w==")]
    [InlineData("state=working:msg=Cg==")]
    [InlineData("state=working:msg=SG=")]
    [InlineData("state=working:msg=SGVsbA=")]
    [InlineData("state=working:title=woU=")]
    [InlineData("state=working:msg=a")]
    public void InvalidReportsDoNotAlterExistingRecords(string body)
    {
        using var engine = new TerminalEngine();
        Send(engine, "state=done:app=original");
        var before = engine.ProgramStatus;
        Send(engine, body);
        Assert.Same(before, engine.ProgramStatus);
    }

    [Fact]
    public void MalformedPairsAreSkippedAndLastValidValueWins()
    {
        using var engine = new TerminalEngine();
        Send(engine, "state=idle:broken:=value:BAD=x:state=working:app=bad!:progress=101:kind=auth:app=cargo");
        var record = engine.ProgramStatus.Records[string.Empty];
        Assert.Equal(TerminalProgramState.Working, record.State);
        Assert.Null(record.Progress);
        Assert.Null(record.Kind);
        Assert.Equal("cargo", record.App);
    }

    [Fact]
    public void LastValidIdWinsAndInvalidWireBytesAreMeasuredAndSkippedAsBytes()
    {
        using var engine = new TerminalEngine();
        Send(engine, "state=working:id=a!:id=valid");
        Assert.Equal("valid", Assert.Single(engine.ProgramStatus.Records).Key);
        var before = engine.ProgramStatus;
        Send(engine, "state=working:id=a!");
        Assert.Same(before, engine.ProgramStatus);
        Send(engine, "state=working:id=a//b:id=valid");
        Assert.Equal("valid", Assert.Single(engine.ProgramStatus.Records).Key);
        before = engine.ProgramStatus;
        Send(engine, "state=working:id=a//b");
        Assert.Same(before, engine.ProgramStatus);

        var prefix = Encoding.ASCII.GetBytes("\u001b]7501;state=working:app=");
        var suffix = Encoding.ASCII.GetBytes(":app=cargo\u001b\\");
        var bytes = prefix.Concat(Enumerable.Repeat((byte)0xff, 11)).Concat(suffix).ToArray();
        engine.Feed(bytes);
        Assert.Equal("cargo", engine.ProgramStatus.Records[""].App);

        var invalidKey = Encoding.ASCII.GetBytes("\u001b]7501;state=working:");
        bytes = invalidKey.Concat(Enumerable.Repeat((byte)0xff, 6))
            .Concat(Encoding.ASCII.GetBytes("=x\u001b\\")).ToArray();
        engine.Feed(bytes);
        Assert.Equal(TerminalProgramState.Working, engine.ProgramStatus.Records[""].State);

        before = engine.ProgramStatus;
        bytes = Encoding.UTF8.GetBytes("\u001b]7501;state=\u00a0working\u001b\\");
        engine.Feed(bytes);
        Assert.Same(before, engine.ProgramStatus);

        Send(engine, "state= working\t");
        Assert.Equal(TerminalProgramState.Working, engine.ProgramStatus.Records[""].State);
    }

    [Fact]
    public void LimitsAreCheckedForAllPairsBeforeApplyingChanges()
    {
        using var engine = new TerminalEngine();
        Send(engine, "state=done");
        var before = engine.ProgramStatus;
        foreach (var body in new[]
        {
            $"state=working:{new string('x', 17)}=1",
            $"state=working:app={new string('a', 33)}:app=ok",
            $"state=working:id={new string('a', 33)}",
            "state=working:id=a/b/c/d/e/f/g/h/i",
            $"state=working:msg={new string('A', 2733)}:msg=SGk=",
            $"state=working:title={new string('A', 257)}",
            $"state=working:msg={Convert.ToBase64String(new byte[2049])}",
        })
        {
            Send(engine, body);
            Assert.Same(before, engine.ProgramStatus);
        }
    }

    [Fact]
    public void MaximumTextAndIdLimitsAreAccepted()
    {
        using var engine = new TerminalEngine();
        var id = string.Join('/', new[] { new string('a', 31), new string('b', 31), new string('c', 31), new string('d', 32) });
        var message = new string('m', 2048);
        var title = new string('t', 192);
        Send(engine, $"state=working:id={id}:msg={Convert.ToBase64String(Encoding.UTF8.GetBytes(message))}:title={Convert.ToBase64String(Encoding.UTF8.GetBytes(title))}");
        var record = engine.ProgramStatus.Records[id];
        Assert.Equal(message, record.Message);
        Assert.Equal(title, record.Title);
    }

    [Fact]
    public void SequenceSizeLimitCountsIntroducerTerminatorAndIgnoredBytes()
    {
        using var engine = new TerminalEngine();
        const string prefix = "\u001b]7501;state=working:x=";
        engine.Feed(prefix + new string('a', 4096 - prefix.Length - 1) + "\u0007");
        Assert.Single(engine.ProgramStatus.Records);
        var before = engine.ProgramStatus;
        engine.Feed(prefix + new string('a', 4096 - prefix.Length) + "\u0007");
        Assert.Same(before, engine.ProgramStatus);
        engine.Feed(prefix + new string('a', 4096 - prefix.Length - 2) + "\u0001\u0001\u0007");
        Assert.Same(before, engine.ProgramStatus);
        engine.Feed(prefix + new string('a', 4096 - prefix.Length - 2) + "\u001b\\");
        Assert.NotSame(before, engine.ProgramStatus);
        before = engine.ProgramStatus;
        engine.Feed(prefix + new string('a', 4096 - prefix.Length - 1) + "\u001b\\");
        Assert.Same(before, engine.ProgramStatus);
        Send(engine, "state=done");
        Assert.Equal(TerminalProgramState.Done, engine.ProgramStatus.Records[string.Empty].State);
    }

    [Fact]
    public void RecordCapEvictsLeastRecentlyUpdated()
    {
        using var engine = new TerminalEngine();
        for (var i = 0; i < 256; i++)
            Send(engine, $"state=working:id=r{i}");
        Send(engine, "state=done:id=r0");
        Send(engine, "state=working:id=extra");
        Assert.Equal(256, engine.ProgramStatus.Records.Count);
        Assert.True(engine.ProgramStatus.Records.ContainsKey("r0"));
        Assert.False(engine.ProgramStatus.Records.ContainsKey("r1"));
    }

    [Fact]
    public void PromptAndProcessExitRemoveOnlyTransientRecordsWhileResetRemovesAll()
    {
        using var engine = new TerminalEngine();
        Send(engine, "state=working");
        Send(engine, "state=blocked:id=child");
        Send(engine, "state=done:id=child/done");
        Send(engine, "state=error:id=error");
        Send(engine, "state=idle:id=idle");
        engine.Feed("\u001b[?1049h\u001b[!p\u001b[?1049l");
        Assert.Equal(5, engine.ProgramStatus.Records.Count);
        engine.Feed("\u001b]133;A\u0007");
        Assert.Equal(3, engine.ProgramStatus.Records.Count);
        Send(engine, "state=working");
        engine.ProgramStatusProcessExited();
        Assert.Equal(3, engine.ProgramStatus.Records.Count);
        engine.Feed("\u001bc");
        Assert.Empty(engine.ProgramStatus.Records);
    }

    private static void Send(TerminalEngine engine, string body) => engine.Feed($"\u001b]7501;{body}\u001b\\");
}
