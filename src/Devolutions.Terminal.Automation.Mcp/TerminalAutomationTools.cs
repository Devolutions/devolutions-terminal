using System.ComponentModel;
using Devolutions.Terminal.Automation;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace Devolutions.Terminal.Automation.Mcp;

[McpServerToolType]
public sealed class TerminalAutomationTools(AutomationIpcClient client)
{
    [McpServerTool, Description("Renders a PNG offscreen, including kitty/sixel images. Returns native image content and SHA-256 pixel hash/geometry. No desktop window. Cursor excluded by default for stable comparisons.")]
    public async Task<IEnumerable<ContentBlock>> Capture(
        string sessionId, TerminalAutomationRegion? region = null, bool includeCursor = false,
        CancellationToken cancellationToken = default)
    {
        var capture = await client.CaptureAsync(new AutomationIpcRequest
        {
            Type = "capture",
            SessionId = sessionId,
            Region = region,
            IncludeCursor = includeCursor,
        }, cancellationToken).ConfigureAwait(false);
        return [
            new TextContentBlock { Text = AutomationIpcProtocol.SerializeJson(capture.Frame) },
            ImageContentBlock.FromBytes(Convert.FromBase64String(capture.PngBase64), "image/png"),
        ];
    }

    [McpServerTool, Description("Waits for rendered pixels to differ from a capture hash, including image-only updates. Use the same cell region and a cursor-free capture. This detects a change, not semantic correctness.")]
    public Task<TerminalAutomationVisualWaitResult> WaitForChange(
        string sessionId, string hash, int timeoutMilliseconds = 10_000,
        TerminalAutomationRegion? region = null, CancellationToken cancellationToken = default) =>
        client.WaitForChangeAsync(new AutomationIpcRequest
        {
            Type = "wait-change",
            SessionId = sessionId,
            Hash = hash,
            TimeoutMilliseconds = timeoutMilliseconds,
            Region = region,
        }, cancellationToken);

    [McpServerTool, Description("Atomically captures a pixel baseline, sends terminal input, and times the first visible change. Types: text, paste, key, click, mouse. Use a target region to exclude unrelated animations. Not remote-server or physical-display latency.")]
    public Task<TerminalAutomationVisualWaitResult> ProbeResponse(
        string sessionId, TerminalAutomationInput input, int timeoutMilliseconds = 10_000,
        TerminalAutomationRegion? region = null, CancellationToken cancellationToken = default) =>
        client.ProbeAsync(new AutomationIpcRequest
        {
            Type = "probe",
            SessionId = sessionId,
            Input = input,
            TimeoutMilliseconds = timeoutMilliseconds,
            Region = region,
        }, cancellationToken);

    [McpServerTool, Description("Samples offscreen frames for responsiveness: pixel changes/sec, frame gaps, render p50/p95/max, received bytes and parse time. Sampling can miss intermediate frames; static content is not necessarily a stall. Duration16..30000ms, interval16..1000ms, <=600 samples. Other tools can send input concurrently.")]
    public Task<TerminalAutomationPerformance> Measure(
        string sessionId, int durationMilliseconds = 1_000, int intervalMilliseconds = 16,
        TerminalAutomationRegion? region = null, CancellationToken cancellationToken = default) =>
        client.MeasureAsync(new AutomationIpcRequest
        {
            Type = "measure",
            SessionId = sessionId,
            DurationMilliseconds = durationMilliseconds,
            IntervalMilliseconds = intervalMilliseconds,
            Region = region,
        }, cancellationToken);

    [McpServerTool, Description("Sends terminal-local mouse press/release/move or scroll-up/down/left/right. Supports drag state and Shift/Control/Alt. Motion requires the application's negotiated motion mode; never moves the OS pointer.")]
    public Task Mouse(string sessionId, string eventType, int column, int row, int button = 0,
        string? modifiers = null, CancellationToken cancellationToken = default) =>
        client.SendAsync(new AutomationIpcRequest
        {
            Type = "mouse",
            SessionId = sessionId,
            Event = eventType,
            Column = column,
            Row = row,
            Button = button,
            Modifiers = modifiers,
        }, cancellationToken);

    [McpServerTool, Description("Sends a terminal key press/repeat/release. Supply keySymbol for layout-sensitive printable keys; it must be one Unicode scalar. Release requires a negotiated event-capable keyboard mode (kitty or Win32); unsupported events return errors, not global keystrokes.")]
    public Task KeyEvent(string sessionId, string key, string eventType, string? modifiers = null,
        string? keySymbol = null, CancellationToken cancellationToken = default) =>
        client.SendAsync(new AutomationIpcRequest
        {
            Type = "key-event",
            SessionId = sessionId,
            Key = key,
            Event = eventType,
            Modifiers = modifiers,
            KeySymbol = keySymbol,
        }, cancellationToken);

    [McpServerTool, Description("Starts a local process in a headless Devolutions Terminal session.")]
    public Task<TerminalAutomationSessionInfo> Start(
        [Description("The process command line to run in the pseudo-terminal.")] string commandLine,
        [Description("Optional working directory for the process.")] string? workingDirectory = null,
        [Description("Terminal width in character cells.")] int columns = 120,
        [Description("Terminal height in character cells.")] int rows = 30,
        CancellationToken cancellationToken = default) =>
        client.StartAsync(new AutomationIpcRequest
        {
            Type = "start",
            CommandLine = commandLine,
            WorkingDirectory = workingDirectory,
            Columns = columns,
            Rows = rows,
        }, cancellationToken);

    [McpServerTool, Description("Lists active and exited headless terminal sessions.")]
    public Task<IReadOnlyList<TerminalAutomationSessionInfo>> List(CancellationToken cancellationToken = default) =>
        client.ListAsync(cancellationToken);

    [McpServerTool, Description("Returns terminal screen rows, sparse cells and styles, cursor, and modes.")]
    public Task<TerminalAutomationSnapshot> Observe(
        [Description("The terminal session identifier.")] string sessionId,
        CancellationToken cancellationToken = default) =>
        client.ObserveAsync(new AutomationIpcRequest
        {
            Type = "observe",
            SessionId = sessionId,
        }, cancellationToken);

    [McpServerTool, Description("Sends literal text to the terminal process.")]
    public Task SendText(
        [Description("The terminal session identifier.")] string sessionId,
        [Description("Text to send.")] string text,
        CancellationToken cancellationToken = default) =>
        client.SendAsync(new AutomationIpcRequest
        {
            Type = "send-text",
            SessionId = sessionId,
            Text = text,
        }, cancellationToken);

    [McpServerTool, Description("Pastes text using the terminal's bracketed-paste mode when enabled.")]
    public Task Paste(
        [Description("The terminal session identifier.")] string sessionId,
        [Description("Text to paste.")] string text,
        CancellationToken cancellationToken = default) =>
        client.SendAsync(new AutomationIpcRequest
        {
            Type = "paste",
            SessionId = sessionId,
            Text = text,
        }, cancellationToken);

    [McpServerTool, Description("Sends a named key using the terminal's active input mode. Supply keySymbol for layout-sensitive printable keys; it must be one Unicode scalar.")]
    public Task SendKey(
        [Description("The terminal session identifier.")] string sessionId,
        [Description("Key name, for example Enter, Escape, Up, or F1.")] string key,
        [Description("Optional Avalonia key modifiers, for example Control or Shift.")] string? modifiers = null,
        [Description("Optional printable character produced by this key on the active keyboard layout.")] string? keySymbol = null,
        CancellationToken cancellationToken = default) =>
        client.SendAsync(new AutomationIpcRequest
        {
            Type = "send-key",
            SessionId = sessionId,
            Key = key,
            Modifiers = modifiers,
            KeySymbol = keySymbol,
        }, cancellationToken);

    [McpServerTool, Description("Sends a terminal-local mouse click in zero-based cell coordinates.")]
    public Task Click(
        [Description("The terminal session identifier.")] string sessionId,
        [Description("Zero-based terminal column.")] int column,
        [Description("Zero-based terminal row.")] int row,
        [Description("Mouse button: 0 left, 1 middle, or 2 right.")] int button = 0,
        CancellationToken cancellationToken = default) =>
        client.SendAsync(new AutomationIpcRequest
        {
            Type = "click",
            SessionId = sessionId,
            Column = column,
            Row = row,
            Button = button,
        }, cancellationToken);

    [McpServerTool, Description("Resizes a running terminal session in character cells.")]
    public Task Resize(
        [Description("The terminal session identifier.")] string sessionId,
        [Description("New terminal width in cells.")] int columns,
        [Description("New terminal height in cells.")] int rows,
        CancellationToken cancellationToken = default) =>
        client.SendAsync(new AutomationIpcRequest
        {
            Type = "resize",
            SessionId = sessionId,
            Columns = columns,
            Rows = rows,
        }, cancellationToken);

    [McpServerTool, Description("Waits until terminal screen text contains the requested string or timeout expires.")]
    public Task<TerminalAutomationWaitResult> WaitForText(
        [Description("The terminal session identifier.")] string sessionId,
        [Description("Text to find in the visible terminal screen.")] string text,
        [Description("Timeout in milliseconds, from 1 to 300000.")] int timeoutMilliseconds = 10_000,
        CancellationToken cancellationToken = default) =>
        client.WaitAsync(new AutomationIpcRequest
        {
            Type = "wait-text",
            SessionId = sessionId,
            Text = text,
            TimeoutMilliseconds = timeoutMilliseconds,
        }, cancellationToken);

    [McpServerTool, Description("Closes a terminal session and terminates its process.")]
    public Task Close(
        [Description("The terminal session identifier.")] string sessionId,
        CancellationToken cancellationToken = default) =>
        client.SendAsync(new AutomationIpcRequest
        {
            Type = "close",
            SessionId = sessionId,
        }, cancellationToken);
}
