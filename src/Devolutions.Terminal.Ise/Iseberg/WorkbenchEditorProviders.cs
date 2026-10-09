using System.Collections.Immutable;
using Iseberg.Core;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg;

internal sealed class WorkbenchAnalysisProvider : IEditorAnalysisProvider
{
    public Func<string, CancellationToken, Task<ScriptAnalysis>>? Analyze { get; set; }
    public long Version { get; private set; }
    public ScriptAnalysis? Parsed { get; private set; }

    public async Task<EditorAnalysisResult> AnalyzeAsync(EditorAnalysisRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = await (Analyze ?? throw new InvalidOperationException("PowerShell analysis is unavailable."))(request.Text, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Parsed = parsed;
        Version = request.Version;
        return new EditorAnalysisResult(request.Version, EditorAnalysisState.Available,
            Parsed.Errors.Select(error => new EditorDiagnostic(error.ErrorId, error.Message, EditorDiagnosticSeverity.Error,
                new(error.Extent.StartOffset, error.Extent.EndOffset - error.Extent.StartOffset))).ToImmutableArray());
    }
}

public sealed partial class WorkbenchControl
{
    private IReadOnlySet<CompletionResultType>? scriptCompletionFilter;

    private sealed class WorkbenchCompletionProvider(WorkbenchControl owner) : IEditorCompletionProvider
    {
        public async Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken cancellationToken)
        {
            var session = owner.displayedSession;
            if (session is null || session.Evaluating || session.Engine.State != SessionState.Ready &&
                !session.Engine.IsDebuggerPaused && !session.Engine.IsNestedPromptActive)
                return new(request.Version, new(request.CaretOffset, 0), []);
            var revision = session.DebugRevisionCounter;
            var filter = owner.scriptCompletionFilter;
            var results = await owner.GetCompletionAsync(session, request.Text, request.CaretOffset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session != owner.displayedSession || revision != session.DebugRevisionCounter)
                throw new OperationCanceledException("The workbench session changed.");
            if (results is null) return new(request.Version, new(request.CaretOffset, 0), []);
            return new(request.Version, new(results.Start, results.Length),
                results.Matches.Where(match => filter is null || filter.Contains(match.ResultType))
                    .Select(match => new EditorCompletionItem(match.CompletionText, match.ListItemText, match.ToolTip)).ToImmutableArray());
        }
    }
}
