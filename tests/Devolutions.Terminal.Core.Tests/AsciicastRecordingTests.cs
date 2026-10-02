using System.Text.Json;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class AsciicastRecordingTests
{
    [Fact]
    public void ToJsonWritesAsciicastV2Ndjson()
    {
        var recording = new AsciicastRecording(120, 40, "demo")
        {
            Timestamp = 1_700_000_000,
        };
        recording.AppendFrame(0.25, "hello\r\n");

        var lines = recording.ToJson().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(120, header.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(40, header.RootElement.GetProperty("height").GetInt32());
        Assert.Equal("demo", header.RootElement.GetProperty("title").GetString());
        Assert.Equal("xterm-256color", header.RootElement.GetProperty("env").GetProperty("TERM").GetString());

        using var outputEvent = JsonDocument.Parse(lines[1]);
        Assert.Equal(0.25, outputEvent.RootElement[0].GetDouble());
        Assert.Equal("o", outputEvent.RootElement[1].GetString());
        Assert.Equal("hello\r\n", outputEvent.RootElement[2].GetString());
    }

    [Fact]
    public void FromJsonReadsOutputEventsAndIgnoresInputEvents()
    {
        const string json = """
            {"version":2,"width":90,"height":30,"timestamp":1700000000,"title":"shell","env":{"TERM":"screen","SHELL":"/bin/sh"}}
            [0.1,"o","prompt"]
            [0.2,"i","secret"]
            [0.3,"o","\r\nresult"]
            """;

        var recording = AsciicastRecording.FromJson(json);

        Assert.Equal(90, recording.Width);
        Assert.Equal(30, recording.Height);
        Assert.Equal(1_700_000_000, recording.Timestamp);
        Assert.Equal("shell", recording.Title);
        Assert.Equal("screen", recording.Env["TERM"]);
        Assert.Equal("/bin/sh", recording.Env["SHELL"]);
        Assert.Collection(
            recording.Frames,
            frame =>
            {
                Assert.Equal(0.1, frame.Timestamp);
                Assert.Equal("prompt", frame.Data);
            },
            frame =>
            {
                Assert.Equal(0.3, frame.Timestamp);
                Assert.Equal("\r\nresult", frame.Data);
            });
    }

    [Fact]
    public void FromJsonRejectsUnsupportedHeader()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AsciicastRecording.FromJson("""{"version":1,"width":80,"height":24}"""));

        Assert.Contains("asciicast v2 or v3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJsonWritesAsciicastV3WithRelativeEventIntervals()
    {
        var recording = new AsciicastRecording(120, 40, "demo", AsciicastFormat.V3)
        {
            Timestamp = 1_700_000_000,
        };
        recording.AppendFrame(0.25, "hello");
        recording.AppendFrame(1.0, " world");

        var lines = recording.ToJson().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(3, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(120, header.RootElement.GetProperty("term").GetProperty("cols").GetInt32());
        Assert.Equal(40, header.RootElement.GetProperty("term").GetProperty("rows").GetInt32());
        Assert.Equal("xterm-256color", header.RootElement.GetProperty("term").GetProperty("type").GetString());

        using var secondEvent = JsonDocument.Parse(lines[2]);
        Assert.Equal(0.75, secondEvent.RootElement[0].GetDouble(), precision: 10);
    }

    [Fact]
    public void FromJsonReadsV3IntervalsAsReplayTimestamps()
    {
        const string json = """
            {"version":3,"term":{"cols":90,"rows":30,"type":"screen"},"timestamp":1700000000,"title":"shell","env":{"SHELL":"/bin/sh"}}
            [0.1,"o","prompt"]
            [0.2,"i","secret"]
            [0.3,"o","\r\nresult"]
            # a comment
            """;

        var recording = AsciicastRecording.FromJson(json);

        Assert.Equal(AsciicastFormat.V3, recording.Format);
        Assert.Equal(90, recording.Width);
        Assert.Equal(30, recording.Height);
        Assert.Equal("screen", recording.Env["TERM"]);
        Assert.Collection(
            recording.Frames,
            frame => Assert.Equal(0.1, frame.Timestamp, precision: 10),
            frame => Assert.Equal(0.6, frame.Timestamp, precision: 10));
    }
}
