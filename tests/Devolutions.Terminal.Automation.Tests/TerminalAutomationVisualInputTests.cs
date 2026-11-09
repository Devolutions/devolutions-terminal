using Devolutions.Terminal.Automation;
using Devolutions.Terminal.Core;
using Xunit;
using FakeConnection = Devolutions.Terminal.Automation.Tests.TerminalAutomationServiceTests.FakeConnection;

namespace Devolutions.Terminal.Automation.Tests;

public sealed class TerminalAutomationVisualInputTests
{
    [Fact]
    public async Task Mouse_ButtonTrackingEncodesPressDragReleaseAndModifiers()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?1002h\u001b[?1006h");

        service.Mouse(session.Id, "press", 1, 1, 2, "Shift,Control,Alt");
        service.Mouse(session.Id, "move", 2, 1);
        service.Mouse(session.Id, "release", 3, 2, 2);

        Assert.Equal(
            ["\u001b[<30;2;2M", "\u001b[<34;3;2M", "\u001b[<2;4;3m"],
            connection.Writes);
        Assert.Equal(TerminalMouseTrackingMode.ButtonEvent, service.Observe(session.Id).MouseTrackingMode);
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "move", 0, 0));
        Assert.Equal(3, connection.Writes.Count);
    }

    [Fact]
    public async Task Mouse_AllMotionSupportsNoButtonMotionAndFourWheelDirections()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?1003h\u001b[?1006h");
        service.Mouse(session.Id, "move", 0, 0);
        service.Mouse(session.Id, "scroll-up", 1, 1);
        service.Mouse(session.Id, "scroll-down", 1, 1);
        service.Mouse(session.Id, "scroll-left", 1, 1);
        service.Mouse(session.Id, "scroll-right", 1, 1, modifiers: "Control");
        Assert.Equal(
            ["\u001b[<35;1;1M", "\u001b[<64;2;2M", "\u001b[<65;2;2M", "\u001b[<66;2;2M", "\u001b[<83;2;2M"],
            connection.Writes);
        Assert.Equal(TerminalMouseTrackingMode.AllMotion, service.Observe(session.Id).MouseTrackingMode);
    }

    [Fact]
    public async Task Mouse_LegacyTrackingUsesX10BytesAndReleaseCode()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?1000h");
        service.Mouse(session.Id, "press", 1, 1);
        service.Mouse(session.Id, "release", 1, 1);
        Assert.Equal(["\u001b[M \"\"", "\u001b[M#\"\""], connection.Writes);
    }

    [Fact]
    public async Task Mouse_InvalidStateAndModeDoNotWriteOrLoseHeldButton()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "press", 0, 0));
        connection.EmitOutput("\u001b[?1000h\u001b[?1006h");
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "release", 0, 0));
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "move", 0, 0));
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "unknown", 0, 0));
        Assert.Empty(connection.Writes);
        service.Mouse(session.Id, "press", 0, 0, 1);
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "press", 0, 0, 2));
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "release", 0, 0, 2));
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "move", 0, 0));
        service.Mouse(session.Id, "release", 0, 0, 1);
        service.Mouse(session.Id, "press", 0, 0, 2);
        service.Mouse(session.Id, "release", 0, 0, 2);
        Assert.Equal(
            ["\u001b[<1;1;1M", "\u001b[<1;1;1m", "\u001b[<2;1;1M", "\u001b[<2;1;1m"],
            connection.Writes);
    }

    [Fact]
    public async Task Click_DuringDragIsRejectedWithoutReleasingHeldMouseButton()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?1002h\u001b[?1006h");
        service.Mouse(session.Id, "press", 1, 1, 2);

        Assert.Throws<InvalidOperationException>(() => service.Click(session.Id, 0, 0));
        Assert.Equal("\u001b[<2;2;2M", Assert.Single(connection.Writes));
        service.Mouse(session.Id, "move", 2, 1);
        service.Mouse(session.Id, "release", 2, 1, 2);
        service.Click(session.Id, 0, 0);

        Assert.Equal(
            ["\u001b[<2;2;2M", "\u001b[<34;3;2M", "\u001b[<2;3;2m", "\u001b[<0;1;1M\u001b[<0;1;1m"],
            connection.Writes);
    }

    [Fact]
    public async Task Mouse_DisablingAndReenablingTrackingClearsHeldButton()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?1002h\u001b[?1006h");
        service.Mouse(session.Id, "press", 1, 1, 2);
        connection.EmitOutput("\u001b[?1002l");
        Assert.False(service.Observe(session.Id).MouseTracking);
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "press", 0, 0));
        connection.EmitOutput("\u001b[?1002h");
        Assert.Throws<InvalidOperationException>(() => service.Mouse(session.Id, "release", 1, 1, 2));
        service.Mouse(session.Id, "press", 0, 0);
        service.Mouse(session.Id, "release", 0, 0);

        Assert.Equal(["\u001b[<2;2;2M", "\u001b[<0;1;1M", "\u001b[<0;1;1m"], connection.Writes);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(8, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 3, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 3)]
    public async Task Mouse_RejectsInvalidCoordinatesAndButton(int column, int row, int button)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?1000h");
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Mouse(session.Id, "press", column, row, button));
        Assert.Empty(connection.Writes);
    }

    [Theory]
    [InlineData("Meta")]
    [InlineData("128")]
    [InlineData("unknown")]
    public async Task Mouse_RejectsUnsupportedModifiers(string modifiers)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?1000h");
        Assert.Throws<ArgumentException>(() => service.Mouse(session.Id, "press", 0, 0, modifiers: modifiers));
        Assert.Empty(connection.Writes);
    }

    [Fact]
    public async Task KeyEvent_KittyEncodesPressRepeatReleaseAndExposesNegotiatedMode()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[>3u");
        service.KeyEvent(session.Id, "Up", "press", "Alt,Control");
        service.KeyEvent(session.Id, "Up", "repeat");
        service.KeyEvent(session.Id, "Up", "release");
        Assert.Equal(["\u001b[1;7A", "\u001b[1;1:2A", "\u001b[1;1:3A"], connection.Writes);
        Assert.Equal(KittyKeyboardFlags.DisambiguateEscapeCodes | KittyKeyboardFlags.ReportEventTypes,
            service.Observe(session.Id).InputMode.KittyFlags);
    }

    [Fact]
    public async Task KeyEvent_Win32EncodesPressRepeatAndRelease()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?9001h");
        service.KeyEvent(session.Id, "Enter", "press");
        service.KeyEvent(session.Id, "Enter", "repeat");
        service.KeyEvent(session.Id, "Enter", "release");
        Assert.Equal(
            ["\u001b[13;0;13;1;0;1_", "\u001b[13;0;13;1;0;1_", "\u001b[13;0;13;0;0;1_"],
            connection.Writes);
        Assert.True(service.Observe(session.Id).InputMode.Win32InputMode);
    }

    [Fact]
    public async Task KeyEvent_LegacyReleaseAndUnknownEventsFailWithoutWriting()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        Assert.Throws<InvalidOperationException>(() => service.KeyEvent(session.Id, "Enter", "release"));
        Assert.Throws<ArgumentException>(() => service.KeyEvent(session.Id, "Enter", "unknown"));
        Assert.Throws<ArgumentException>(() => service.KeyEvent(session.Id, "unknown", "press"));
        Assert.Throws<ArgumentException>(() => service.KeyEvent(session.Id, "Enter", "press", "128"));
        Assert.Empty(connection.Writes);
        service.KeyEvent(session.Id, "Enter", "press");
        service.KeyEvent(session.Id, "Enter", "repeat");
        Assert.Equal(["\r", "\r"], connection.Writes);
    }

    [Fact]
    public async Task SendKey_LegacyPrintableKeysUseInferredSymbolsAndAltPrefix()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));

        service.SendKey(session.Id, "A");
        service.SendKey(session.Id, "A", "Shift");
        service.SendKey(session.Id, "D3");
        service.SendKey(session.Id, "D3", "Shift");
        service.SendKey(session.Id, "Space");
        service.SendKey(session.Id, "A", "Alt");
        service.KeyEvent(session.Id, "A", "press", "Alt");

        Assert.Equal(["a", "A", "3", "#", " ", "\u001ba", "\u001ba"], connection.Writes);
    }

    [Fact]
    public async Task SendKey_ExplicitLayoutSymbolOverridesInferredPrintableCharacter()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));

        service.SendKey(session.Id, "A", "Alt", "€");

        Assert.Equal(["\u001b€"], connection.Writes);
    }

    [Fact]
    public async Task KeyEvent_KittyModeEncodesExplicitSymbolAsAssociatedText()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[>24u");

        service.KeyEvent(session.Id, "A", "press", keySymbol: "@");

        Assert.Equal(["\u001b[97;;64u"], connection.Writes);
    }

    [Fact]
    public async Task KeyEvent_Win32ModeEncodesExplicitUnicodeSymbol()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[?9001h");

        service.KeyEvent(session.Id, "A", "press", keySymbol: "€");

        Assert.Equal(["\u001b[65;0;8364;1;0;1_"], connection.Writes);
    }

    [Fact]
    public async Task ProbeAsync_KeyInputCarriesExplicitSymbolIntoKittyEncoding()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[>24u");
        var baselineHash = service.Capture(session.Id).Frame.Hash;
        connection.OnWrite = _ => connection.EmitOutput(TerminalAutomationVisualTests.SixelRed);

        var result = await service.ProbeAsync(session.Id, new()
        {
            Type = "key",
            Key = "A",
            KeySymbol = "@",
            Event = "press",
        }, TimeSpan.FromSeconds(2));

        Assert.Equal(["\u001b[97;;64u"], connection.Writes);
        Assert.True(result.Matched);
        Assert.NotEqual(baselineHash, result.Frame.Hash);
    }
}
