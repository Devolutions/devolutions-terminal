using Devolutions.Terminal.Core;

namespace Devolutions.Terminal.Automation;

public sealed record TerminalAutomationStartRequest(
    string CommandLine,
    string? WorkingDirectory = null,
    int Columns = 120,
    int Rows = 30,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null);

public sealed record TerminalAutomationSessionInfo(
    string Id,
    string CommandLine,
    bool IsRunning,
    int Columns,
    int Rows,
    int? ExitCode,
    string? Error = null);

public sealed record TerminalAutomationStyleRun(
    int StartColumn,
    int Length,
    CellFlags Style,
    TermColor Foreground,
    TermColor Background);

public sealed record TerminalAutomationCell(
    int Column,
    string Text,
    int Width);

public sealed record TerminalAutomationRow(
    string Text,
    IReadOnlyList<TerminalAutomationCell> Cells,
    IReadOnlyList<TerminalAutomationStyleRun> Styles);

public sealed record TerminalAutomationSnapshot(
    string Id,
    long Revision,
    string Text,
    IReadOnlyList<TerminalAutomationRow> Screen,
    string Title,
    string? WorkingDirectory,
    bool IsRunning,
    int? ExitCode,
    int Columns,
    int Rows,
    int CursorX,
    int CursorY,
    bool CursorVisible,
    bool AlternateBufferActive,
    bool ApplicationCursorKeys,
    bool BracketedPaste,
    bool MouseTracking,
    bool SgrMouse)
{
    public TerminalMouseTrackingMode MouseTrackingMode { get; init; }
    public TerminalInputMode InputMode { get; init; }
    public IReadOnlyList<TerminalAutomationImage> Images { get; init; } = [];
    public IReadOnlyList<TerminalEngineDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record TerminalAutomationImage(
    long Id, string Protocol, int Column, int Row, int? PixelWidth, int? PixelHeight);

public sealed record TerminalAutomationRegion(int Column, int Row, int Columns, int Rows);

public sealed record TerminalAutomationFrame(
    long Revision,
    string Hash,
    int PixelWidth,
    int PixelHeight,
    double CellWidth,
    double CellHeight,
    TerminalAutomationRegion Region,
    double RenderMilliseconds)
{
    public bool IncludesCursor { get; init; }
}

public sealed record TerminalAutomationCapture(TerminalAutomationFrame Frame, string PngBase64);

public sealed record TerminalAutomationVisualWaitResult(
    bool Matched, double ElapsedMilliseconds, TerminalAutomationFrame Frame);

public sealed record TerminalAutomationInput
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? Key { get; init; }
    public string? KeySymbol { get; init; }
    public string? Modifiers { get; init; }
    public string? Event { get; init; }
    public int Column { get; init; }
    public int Row { get; init; }
    public int Button { get; init; }
}

public sealed record TerminalAutomationFrameSample(double ElapsedMilliseconds, TerminalAutomationFrame Frame);

public sealed record TerminalAutomationTiming(double Minimum, double Median, double P95, double Maximum);

public sealed record TerminalAutomationPerformance(
    double DurationMilliseconds,
    int IntervalMilliseconds,
    int ChangedFrames,
    double ObservedChangesPerSecond,
    double LongestUnchangedMilliseconds,
    long OutputBytes,
    double ParseMilliseconds,
    TerminalAutomationTiming RenderMilliseconds,
    TerminalAutomationTiming? ChangeIntervalMilliseconds,
    IReadOnlyList<TerminalAutomationFrameSample> Samples);

public sealed record TerminalAutomationWaitResult(
    bool Matched,
    TerminalAutomationSnapshot Snapshot);

public sealed record TerminalAutomationCommandResult(bool Completed);
