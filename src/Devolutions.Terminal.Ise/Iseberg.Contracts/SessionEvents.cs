

namespace Iseberg.Core;

public enum OutputKind { Output, Error, Warning, Verbose, Debug, Information, Command }
public enum SessionState { Starting, Ready, Running, Debugging, NestedPrompt, Disposed, Failed }

public sealed record OutputStyle(string? Foreground = null, string? Background = null, bool Bold = false, bool Underline = false, bool Inverse = false);
public sealed record OutputEntry(string Text, OutputKind Kind = OutputKind.Output, int CodeStart = 0, OutputStyle? Style = null);
public sealed record DebugLocation(string? ScriptPath, int Line, int Column, string Message);
public sealed record ProgressUpdate(string Activity, string Status, int Percent, bool Completed,
    long SourceId = 0, int ActivityId = 0, int ParentActivityId = -1, int SecondsRemaining = -1, string? CurrentOperation = null);
public sealed record CompletionSet(int Start, int Length, IReadOnlyList<CompletionResult> Matches);
public sealed record CompletionResult(string CompletionText, string ListItemText,
    CompletionResultType ResultType, string ToolTip);
public sealed record CommandDescription(string Name, string Module, string Kind, string Definition);
public sealed record PromptChoice(string Label, string Help);
