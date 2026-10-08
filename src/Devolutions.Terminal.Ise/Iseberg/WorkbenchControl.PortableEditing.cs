using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Utils;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private PortableAnalysisProvider? portableAnalysis;
    private ScriptDiagnosticRenderer? portableDiagnosticRenderer;
    private Func<bool>? portableHoverIsCurrent;
    private long portableHoverGeneration;

    /// <summary>Call once after the editor/folding setup. Does not alter host console behavior.</summary>
    internal void InitializePortableEditing()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (portableAnalysis is not null) return;
        portableAnalysis = new(() => displayedFile?.File.Path ?? displayedFile?.File.Name);
        ScriptEditorControl.AnalysisProvider = portableAnalysis;
        ScriptEditorControl.CompletionProvider = new PortableCompletionProvider(this);
        portableDiagnosticRenderer = new(() => ScriptEditorControl.Analysis, () => settings.Theme);
        ScriptEditor.TextArea.TextView.BackgroundRenderers.Add(portableDiagnosticRenderer);
        // AvaloniaEdit 12 emits TextCopied, not the legacy data-object copying event.
        ScriptEditor.TextArea.TextCopied += OnPortableTextCopied;
        ScriptEditor.PointerHover += OnPortableHover;
        ScriptEditor.PointerHoverStopped += OnPortableHoverStopped;
        ScriptEditorControl.DetachedFromVisualTree += OnPortableEditorDetached;
    }

    /// <summary>First line of ApplyScriptAnalysis: if this returns true, skip the old provider path.</summary>
    internal bool ApplyPortableScriptAnalysis()
    {
        if (portableAnalysis is null) return false;
        ResetPortableHover();
        if (windowClosed) return true;
        var result = ScriptEditorControl.Analysis;
        colorizer.Analysis = result.State == EditorAnalysisState.Available && portableAnalysis.Version == result.Version
            ? portableAnalysis.Parsed : null;
        ScriptEditor.TextArea.TextView.Redraw();
        breakpointMargin.InvalidateVisual();
        folding?.UpdateFoldings(settings.ShowOutlining && colorizer.Analysis is { } parsed
            ? parsed.Folds.Select(fold => new NewFolding(fold.Start, fold.End)) : [], -1);
        Diagnostics.IsVisible = !result.Diagnostics.IsEmpty;
        Diagnostics.Text = string.Join("  |  ", result.Diagnostics.Take(3).Select(diagnostic =>
            $"Line {ScriptEditorControl.Document.GetLineByOffset(diagnostic.Span.Start).LineNumber}: {diagnostic.Message}"));
        return true;
    }

    /// <summary>Call from RefreshState and RenderDebugger to dismiss values invalidated by resume/frame/evaluation.</summary>
    internal void RefreshPortableEditing()
    {
        if (portableHoverIsCurrent is not null && !portableHoverIsCurrent()) ResetPortableHover();
        if (!windowClosed && portableAnalysis?.Parsed is { } parsed &&
            parsed.IsXml != EditorAnalysis.IsXmlDocument(displayedFile?.File.Path ?? displayedFile?.File.Name))
            AnalyzeScript();
    }

    /// <summary>Call before editor disposal. Pending hover results cannot reopen a detached tooltip.</summary>
    internal void DisposePortableEditing()
    {
        ResetPortableHover();
        ScriptEditor.PointerHover -= OnPortableHover;
        ScriptEditor.PointerHoverStopped -= OnPortableHoverStopped;
        ScriptEditorControl.DetachedFromVisualTree -= OnPortableEditorDetached;
        ScriptEditor.TextArea.TextCopied -= OnPortableTextCopied;
        if (portableDiagnosticRenderer is not null)
            ScriptEditor.TextArea.TextView.BackgroundRenderers.Remove(portableDiagnosticRenderer);
        portableDiagnosticRenderer = null;
    }

    private sealed class PortableAnalysisProvider(Func<string?> path) : IEditorAnalysisProvider
    {
        public long Version { get; private set; }
        public ScriptAnalysis? Parsed { get; private set; }

        public Task<EditorAnalysisResult> AnalyzeAsync(EditorAnalysisRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = EditorAnalysis.Analyze(request.Text, path());
            cancellationToken.ThrowIfCancellationRequested();
            Parsed = parsed;
            Version = request.Version;
            return Task.FromResult(new EditorAnalysisResult(request.Version, EditorAnalysisState.Available,
                parsed.Diagnostics.Select(diagnostic => new EditorDiagnostic(diagnostic.Code, diagnostic.Message,
                    EditorDiagnosticSeverity.Error, new(diagnostic.Start, diagnostic.End - diagnostic.Start))).ToImmutableArray()));
        }

    }

    private sealed class PortableCompletionProvider(WorkbenchControl owner) : IEditorCompletionProvider
    {
        private readonly WorkbenchCompletionProvider powerShell = new(owner);

        public Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EditorAnalysis.IsXmlDocument(owner.displayedFile?.File.Path ?? owner.displayedFile?.File.Name)
                ? Task.FromResult(new EditorCompletionList(request.Version, new(request.CaretOffset, 0), []))
                : powerShell.CompleteAsync(request, cancellationToken);
        }
    }

    private async void OnPortableTextCopied(object? sender, TextEventArgs args)
    {
        if (windowClosed) return;
        try
        {
            var data = new DataTransfer();
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, args.Text);
            data.Add(item);
            AddPortableCopyHtml(data);
            var clipboard = TopLevel.GetTopLevel(ScriptEditor)?.Clipboard ??
                throw new InvalidOperationException("The script editor has no clipboard.");
            await clipboard.SetDataAsync(data);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or
            IOException or UnauthorizedAccessException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            await ReportErrorAsync("Rich editor copy", exception);
        }
    }

    internal void AddPortableCopyHtml(DataTransfer data)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(windowClosed, this);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Items.Count == 0) return;
        var selection = ScriptEditor.TextArea.Selection;
        var text = ScriptEditor.Document.Text;
        var analysis = EditorAnalysis.Analyze(text, displayedFile?.File.Path ?? displayedFile?.File.Name);
        var spans = DesktopTheme.HighContrast ? [] : PowerShellColorizer.GetColorSpans(analysis, settings.Theme);
        string fragment;
        if (selection.IsEmpty)
        {
            if (!ScriptEditor.Options.CutCopyWholeLine) return;
            var line = ScriptEditor.Document.GetLineByOffset(ScriptEditor.CaretOffset);
            fragment = ColoredHtml(text, new(line.Offset, line.TotalLength), spans, ScriptEditor.Options.IndentationSize);
        }
        else
        {
            // Enrich the native payload without replacing rectangular/whole-line clipboard metadata.
            if (selection is AvaloniaEdit.Editing.RectangleSelection)
            {
                var rows = selection.GetText().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var segments = selection.Segments.ToArray();
                var html = new StringBuilder();
                for (var index = 0; index < rows.Length; index++)
                {
                    if (index > 0) html.Append('\n');
                    var source = index < segments.Length
                        ? ScriptEditor.Document.GetText(segments[index].StartOffset, segments[index].Length) : "";
                    if (index < segments.Length && rows[index].StartsWith(source, StringComparison.Ordinal))
                    {
                        html.Append(ColoredHtml(text, new(segments[index].StartOffset, segments[index].Length),
                            spans, ScriptEditor.Options.IndentationSize));
                        html.Append(WebUtility.HtmlEncode(rows[index][source.Length..]));
                    }
                    else html.Append(WebUtility.HtmlEncode(rows[index]));
                }
                fragment = html.ToString();
            }
            else fragment = ColoredHtml(text, new(selection.SurroundingSegment.Offset, selection.SurroundingSegment.Length),
                spans, ScriptEditor.Options.IndentationSize);
        }
        var wrapped = WrapCopyHtml(fragment, settings.Theme, DesktopTheme.HighContrast, ScriptEditor.Options.IndentationSize);
        var item = data.Items[0];
        if (OperatingSystem.IsWindows())
            item.Set(DataFormat.CreateBytesPlatformFormat("HTML Format"), CreateWindowsHtmlClipboard(wrapped));
        else if (OperatingSystem.IsMacOS())
            item.Set(DataFormat.CreateBytesPlatformFormat("public.html"), Encoding.UTF8.GetBytes(wrapped));
        else item.Set(DataFormat.CreateStringPlatformFormat("text/html"), wrapped);
    }

    /// <summary>Safe HTML from a UTF-16 document slice, using the same token palette/overlays as rendering.</summary>
    public static string CreateRichCopyHtml(string text, ScriptAnalysis analysis, EditorTextSpan span, EditorTheme theme, int tabSize = 4) =>
        WrapCopyHtml(ColoredHtml(text, span, PowerShellColorizer.GetColorSpans(analysis, theme), tabSize), theme, highContrast: false, tabSize);

    private static string WrapCopyHtml(string fragment, EditorTheme theme, bool highContrast, int tabSize)
    {
        Color ReadColor(string key, string system) => highContrast
            ? DesktopTheme.Brush(system) is ISolidColorBrush brush ? brush.Color :
                throw new NotSupportedException("Rich copy requires solid high-contrast foreground/background colors.")
            : Color.Parse(theme.Colors[key]);
        static string Css(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var foreground = Css(ReadColor("Script.Foreground", "WindowTextBrush"));
        var background = Css(ReadColor("Script.Background", "WindowBrush"));
        return $"<pre style=\"white-space:pre;tab-size:{tabSize};font-family:monospace;color:{foreground};" +
            $"background-color:{background}\">{fragment}</pre>";
    }

    private static string ColoredHtml(string text, EditorTextSpan span, IReadOnlyList<SyntaxColorSpan> spans, int tabSize)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(span.End, text.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(tabSize, 1);
        var selectedText = text[span.Start..span.End];
        var model = new RichTextModel();
        foreach (var color in spans)
        {
            var start = Math.Max(span.Start, color.Start);
            var end = Math.Min(span.End, color.End);
            if (start < end) model.SetForeground(start - span.Start, end - start, new SimpleHighlightingBrush(color.Color));
        }
        var output = new StringBuilder();
        foreach (var section in model.GetHighlightedSections(0, selectedText.Length))
        {
            var styled = section.Color.Foreground is not null;
            if (styled)
                output.Append("<span style=\"").Append(WebUtility.HtmlEncode(
                    section.Color.ToCss().Replace(" ", "", StringComparison.Ordinal))).Append("\">");
            // AvaloniaEdit's HTML writer encodes individual UTF-16 chars, losing surrogate pairs.
            output.Append(WebUtility.HtmlEncode(selectedText.Substring(section.Offset, section.Length)));
            if (styled) output.Append("</span>");
        }
        return output.ToString();
    }

    /// <summary>CF_HTML offsets are UTF-8 byte offsets, including for non-ASCII selections.</summary>
    public static byte[] CreateWindowsHtmlClipboard(string fragment)
    {
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        const string headerFormat = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, headerFormat, 0, 0, 0, 0));
        var startFragment = headerLength + Encoding.UTF8.GetByteCount(prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);
        var header = string.Format(CultureInfo.InvariantCulture, headerFormat, headerLength, endHtml, startFragment, endFragment);
        return Encoding.UTF8.GetBytes(header + prefix + fragment + suffix);
    }

    private void OnPortableHoverStopped(object? sender, PointerEventArgs args) => ResetPortableHover();
    private void OnPortableEditorDetached(object? sender, VisualTreeAttachmentEventArgs args) => ResetPortableHover();

    private void ResetPortableHover()
    {
        portableHoverGeneration++;
        portableHoverIsCurrent = null;
        ToolTip.SetIsOpen(ScriptEditor, false);
        ToolTip.SetTip(ScriptEditor, null);
    }

    private void ShowPortableHover(string text)
    {
        ToolTip.SetPlacement(ScriptEditor, PlacementMode.Pointer);
        ToolTip.SetTip(ScriptEditor, new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 520 });
        ToolTip.SetIsOpen(ScriptEditor, true);
    }

    private async void OnPortableHover(object? sender, PointerEventArgs args)
    {
        ResetPortableHover();
        if (windowClosed || !ScriptEditorControl.TryGetOffsetFromPoint(args.GetPosition(ScriptEditor), out var offset)) return;
        var generation = portableHoverGeneration;
        var version = ScriptEditorControl.DocumentVersion;
        var lifetime = ScriptEditorControl.EditorLifetime;
        var file = displayedFile;
        bool CurrentDocument() => !windowClosed && generation == portableHoverGeneration && displayedFile == file &&
            ScriptEditorControl.IsSnapshotCurrent(version, lifetime);
        var diagnostics = ScriptEditorControl.Analysis;
        var diagnostic = diagnostics.State == EditorAnalysisState.Available
            ? diagnostics.Diagnostics.FirstOrDefault(value => value.Span.Start <= offset &&
                offset < value.Span.Start + Math.Max(1, value.Span.Length)) : null;
        if (diagnostic is not null)
        {
            portableHoverIsCurrent = CurrentDocument;
            ShowPortableHover(diagnostic.Message);
            args.Handled = true;
            return;
        }
        if (file is null || displayedSession is not { } session || !session.Engine.IsDebuggerPaused ||
            session.Evaluating || !FileInCurrentRunspace(session, file) || colorizer.Analysis is not { IsXml: false } parsed ||
            EditorAnalysis.VariableAtOffset(parsed.Tokens, offset) is not { } variable) return;
        var revision = session.DebugRevisionCounter;
        var runspace = session.Engine.RunspaceId;
        var frame = session.SelectedDebugFrame;
        bool CurrentPause() => CurrentDocument() && displayedSession == session && Workbench.Sessions.Contains(session) &&
            session.Engine.IsDebuggerPaused && !session.Evaluating && revision == session.DebugRevisionCounter &&
            runspace == session.Engine.RunspaceId && frame == session.SelectedDebugFrame;
        portableHoverIsCurrent = CurrentPause;
        args.Handled = true;
        try
        {
            if (session.DebugSnapshot is null) await RefreshDebuggerAsync(session);
            if (!CurrentPause()) return;
            var snapshot = session.DebugSnapshot ?? throw new InvalidOperationException("The debugger variable snapshot is unavailable.");
            portableHoverIsCurrent = () => CurrentPause() && session.DebugSnapshot == snapshot;
            var value = variable.VariablePath.IsUnqualified
                ? snapshot.Variables.FirstOrDefault(value => value.Name.Equals("$" + variable.VariablePath.UserPath, StringComparison.OrdinalIgnoreCase))
                : null;
            if (!variable.VariablePath.IsUnqualified)
                ShowPortableHover(variable.Text + ": Scoped/provider variables cannot be resolved from the selected frame's variable snapshot.");
            else if (value is null)
                ShowPortableHover(variable.Text + ": Variable is not available in the selected debugger scope.");
            else ShowPortableHover(value.Error is null ? value.ToString() : value.Name + ": " + value.Error);
        }
        catch (InvalidOperationException) when (!CurrentPause()) { }
        catch (Exception exception) when (exception is InvalidOperationException or System.Management.Automation.RuntimeException)
        {
            if (!CurrentPause()) return;
            ShowPortableHover(variable.Text + ": " + exception.Message);
            await ReportErrorAsync("Debugger variable hover", exception);
        }
    }
}
