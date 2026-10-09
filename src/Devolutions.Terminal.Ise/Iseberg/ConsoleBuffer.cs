using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Avalonia.Threading;
using Iseberg.Core;

namespace Iseberg;

public sealed class ConsoleBuffer : IReadOnlySectionProvider
{
    public sealed record Span(int Start, int End, OutputKind Kind, int CodeStart = 0, ScriptAnalysis? Analysis = null,
        OutputStyle? Style = null, IReadOnlyList<OutputEntry>? PromptStyles = null)
    {
        internal Guid AnalysisId { get; init; } = Guid.NewGuid();
    }
    public TextDocument Document { get; } = new();
    public List<Span> Spans { get; } = [];
    public int TranscriptEnd { get; private set; }
    public int InputStart { get; private set; }
    public bool HasPrompt { get; private set; }
    private string draft = "";
    private string prompt = "";
    private string rawPrompt = "";
    private AnsiOutputParser ansi = new();
    private bool warnedUnsupportedControl;
    public IReadOnlyList<OutputEntry> PromptParts { get; private set; } = [];
    private string? analyzedInput;
    private string observedInput = "";
    private ScriptAnalysis? inputAnalysis;
    public long InputRevision { get; private set; }
    private CancellationTokenSource? inputAnalysisCancellation;
    public Func<string, CancellationToken, Task<ScriptAnalysis>>? Analyze { get; set; }
    public ScriptAnalysis? InputAnalysis => analyzedInput == Input ? inputAnalysis : null;
    public event Action? AnalysisChanged;
    public event Action<Exception>? AnalysisFailed;

    public ConsoleBuffer() => Document.TextChanged += (_, _) => ObserveInputChange();

    private void ObserveInputChange()
    {
        var input = Input;
        if (input == observedInput) return;
        observedInput = input;
        InputRevision++;
        ScheduleInputAnalysis();
    }

    private void ScheduleInputAnalysis()
    {
        if (Analyze is null || analyzedInput == Input) return;
        inputAnalysisCancellation?.Cancel();
        inputAnalysisCancellation?.Dispose();
        inputAnalysisCancellation = new();
        _ = AnalyzeInputAsync(Input, inputAnalysisCancellation.Token);
    }

    private async Task AnalyzeInputAsync(string text, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(150, cancellation);
            var analysis = await Analyze!(text, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (Input != text) return;
            analyzedInput = text;
            inputAnalysis = analysis;
            AnalysisChanged?.Invoke();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
        {
            AnalysisFailed?.Invoke(exception);
        }
    }

    public string Input
    {
        get => HasPrompt ? Document.GetText(InputStart, Document.TextLength - InputStart) : draft;
        set
        {
            if (HasPrompt) Document.Replace(InputStart, Document.TextLength - InputStart, value);
            else { draft = value; ObserveInputChange(); }
        }
    }

    public bool CanInsert(int offset) => HasPrompt && offset >= InputStart && offset <= Document.TextLength;

    public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
    {
        var start = Math.Max(InputStart, segment.Offset);
        var end = Math.Min(Document.TextLength, segment.EndOffset);
        if (HasPrompt && end > start) yield return new SimpleSegment(start, end - start);
    }

    public void ShowPrompt(string value)
    {
        if (HasPrompt && rawPrompt == value) return;
        rawPrompt = value;
        var parser = new AnsiOutputParser();
        PromptParts = parser.Parse(new(value));
        parser.Complete();
        if (parser.UnsupportedControl && !warnedUnsupportedControl) Mutate(ReportUnsupportedControl);
        value = string.Concat(PromptParts.Select(part => part.Text));
        var input = Input;
        Mutate(() =>
        {
            Document.Replace(TranscriptEnd, Document.TextLength - TranscriptEnd, value + input);
            prompt = value;
            InputStart = TranscriptEnd + value.Length;
            HasPrompt = true;
        });
    }

    public void HidePrompt()
    {
        if (!HasPrompt) return;
        draft = Input;
        Mutate(() =>
        {
            Document.Remove(TranscriptEnd, Document.TextLength - TranscriptEnd);
            HasPrompt = false;
            InputStart = TranscriptEnd;
        });
    }

    public bool CompleteOutput()
    {
        ansi.Complete();
        if (ansi.UnsupportedControl && !warnedUnsupportedControl)
        {
            Mutate(ReportUnsupportedControl);
            return true;
        }
        return false;
    }

    private void ReportUnsupportedControl()
    {
        warnedUnsupportedControl = true;
        AppendEntry(new(Environment.NewLine + UiText.Get("UnsupportedTerminalControl") + Environment.NewLine, OutputKind.Warning));
    }

    public void Append(OutputEntry entry) => AppendBatch([entry]);

    public void AppendBatch(IEnumerable<OutputEntry> entries)
    {
        Mutate(() =>
        {
            foreach (var entry in entries)
            {
                if (entry.Kind == OutputKind.Command)
                {
                    ansi.Complete();
                    var parser = new AnsiOutputParser();
                    var parts = parser.Parse(new(entry.Text[..entry.CodeStart]));
                    parser.Complete();
                    var prefix = string.Concat(parts.Select(part => part.Text));
                    AppendEntry(entry with { Text = prefix + entry.Text[entry.CodeStart..], CodeStart = prefix.Length }, parts);
                    if (parser.UnsupportedControl && !warnedUnsupportedControl) ReportUnsupportedControl();
                }
                else foreach (var part in ansi.Parse(entry)) AppendEntry(part);
                if (ansi.UnsupportedControl && !warnedUnsupportedControl)
                    ReportUnsupportedControl();
            }
            Trim();
        });
    }

    private void AppendEntry(OutputEntry entry, IReadOnlyList<OutputEntry>? promptStyles = null)
    {
        var start = TranscriptEnd;
        Document.Insert(start, entry.Text);
        TranscriptEnd += entry.Text.Length;
        InputStart += entry.Text.Length;
        if (entry.Kind != OutputKind.Command && Spans.Count > 0 && Spans[^1].Kind == entry.Kind && Spans[^1].End == start && Spans[^1].Style == entry.Style)
            Spans[^1] = Spans[^1] with { End = TranscriptEnd };
        else
        {
            var span = new Span(start, TranscriptEnd, entry.Kind, start + entry.CodeStart,
                entry.Kind == OutputKind.Command && analyzedInput == entry.Text[entry.CodeStart..] ? inputAnalysis : null, entry.Style, promptStyles);
            Spans.Add(span);
            if (entry.Kind == OutputKind.Command && span.Analysis is null && Analyze is not null)
                _ = AnalyzeCommandAsync(span.AnalysisId, entry.Text[entry.CodeStart..]);
        }
    }

    private async Task AnalyzeCommandAsync(Guid id, string text)
    {
        try
        {
            var result = await Analyze!(text, CancellationToken.None);
            var index = Spans.FindIndex(span => span.AnalysisId == id);
            if (index < 0) return;
            Spans[index] = Spans[index] with { Analysis = result };
            AnalysisChanged?.Invoke();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            AnalysisFailed?.Invoke(exception);
        }
    }

    internal void CancelAnalysis()
    {
        inputAnalysisCancellation?.Cancel();
        inputAnalysisCancellation?.Dispose();
        inputAnalysisCancellation = null;
        Analyze = null;
    }

    private void Trim()
    {
        if (TranscriptEnd <= 500_000) return;
        var line = Document.GetLineByOffset(TranscriptEnd - 400_000);
        var removed = line.Offset > 0 ? line.Offset : TranscriptEnd - 400_000;
        if (TranscriptEnd - removed > 500_000) removed = TranscriptEnd - 400_000;
        Document.Remove(0, removed);
        TranscriptEnd -= removed;
        InputStart -= removed;
        var retained = Spans.Where(s => s.End > removed).Select(s => s with
        {
            Start = Math.Max(0, s.Start - removed), End = s.End - removed, CodeStart = s.CodeStart - removed
        }).ToArray();
        Spans.Clear();
        Spans.AddRange(retained);
    }

    public void Clear()
    {
        var input = Input;
        Mutate(() =>
        {
            Document.Text = HasPrompt ? prompt + input : "";
            TranscriptEnd = 0;
            InputStart = HasPrompt ? prompt.Length : 0;
            Spans.Clear();
            ansi = new();
            warnedUnsupportedControl = false;
        });
    }

    private void Mutate(Action action)
    {
        Document.BeginUpdate();
        try { action(); }
        finally { Document.EndUpdate(); Document.UndoStack.ClearAll(); }
    }
}
