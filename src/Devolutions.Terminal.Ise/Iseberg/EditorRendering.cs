using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Iseberg.Core;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;

namespace Iseberg;

public readonly record struct SyntaxColorSpan(int Start, int End, Color Color);

public sealed class PowerShellColorizer : DocumentColorizingTransformer
{
    private static readonly Regex XmlTokens = new("""(?<Comment><!--.*?-->)|</?(?<Tag>[\w:.-]+)|(?<Attribute>[\w:.-]+)\s*=\s*(?<Value>"[^"]*"|'[^']*')""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private ScriptAnalysis? analysis;
    private EditorTheme theme = new();
    private string pane = "Script";
    private IReadOnlyList<SyntaxColorSpan>? colorSpans;
    public ScriptAnalysis? Analysis { get => analysis; set { analysis = value; colorSpans = null; } }
    public EditorTheme Theme { get => theme; set { theme = value; colorSpans = null; } }
    public string Pane { get => pane; set { pane = value; colorSpans = null; } }
    protected override void ColorizeLine(DocumentLine line)
    {
        if (Analysis is null || DesktopTheme.HighContrast) return;
        foreach (var span in colorSpans ??= GetColorSpans(Analysis, Theme, Pane))
            if (span.Start < line.EndOffset && span.End > line.Offset)
                ChangeLinePart(Math.Max(line.Offset, span.Start), Math.Min(line.EndOffset, span.End),
                    element => element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(span.Color)));
    }

    /// <summary>Ordered overlays shared by the editor and rich copy; later spans take precedence.</summary>
    public static IReadOnlyList<SyntaxColorSpan> GetColorSpans(ScriptAnalysis analysis, EditorTheme theme, string pane = "Script")
    {
        var spans = new List<SyntaxColorSpan>();
        if (analysis.IsXml)
        {
            foreach (var token in analysis.XmlTokens)
                spans.Add(new(token.Start, token.End, Color.Parse(theme.Colors["Xml." + token.Kind])));
            return spans;
        }
        Token? previous = null;
        foreach (var token in analysis.Tokens)
        {
            var color = ColorFor(token, previous, theme, pane);
            previous = token;
            if (color is SolidColorBrush brush)
                spans.Add(new(token.Extent.StartOffset, token.Extent.EndOffset, brush.Color));
            if (token is StringExpandableToken { NestedTokens: { } nested })
                foreach (var variable in nested)
                    spans.Add(new(variable.Extent.StartOffset, variable.Extent.EndOffset, Color.Parse(theme.Colors[pane + ".Variable"])));
            if (token is StringToken && token.Text.Contains('<'))
                foreach (Match match in XmlTokens.Matches(token.Text))
                    foreach (var name in new[] { "Comment", "Tag", "Attribute", "Value" })
                    {
                        var group = match.Groups[name];
                        var start = token.Extent.StartOffset + group.Index;
                        var end = start + group.Length;
                        if (group.Success)
                            spans.Add(new(start, end, Color.Parse(theme.Colors["Xml." + name])));
                    }
        }
        return spans;
    }

    public static IBrush? ColorFor(Token token, Token? previous, EditorTheme theme, string pane)
    {
        IBrush Brush(string kind) => new SolidColorBrush(Color.Parse(theme.Colors[pane + "." + kind]));
        if (previous?.Kind is TokenKind.Function or TokenKind.Filter) return Brush("Function");
        if (token.TokenFlags.HasFlag(TokenFlags.AttributeName)) return Brush("Attribute");
        if (token.Kind == TokenKind.Label) return Brush("Label");
        if (token.Kind == TokenKind.Comment) return Brush("Comment");
        if (token.Kind is TokenKind.StringLiteral or TokenKind.StringExpandable or TokenKind.HereStringLiteral or TokenKind.HereStringExpandable) return Brush("String");
        if (token.Kind is TokenKind.Variable or TokenKind.SplattedVariable) return Brush("Variable");
        if (token.Kind == TokenKind.Number) return Brush("Number");
        if (token.Kind == TokenKind.Parameter) return Brush("Parameter");
        if (token.TokenFlags.HasFlag(TokenFlags.Keyword)) return Brush("Keyword");
        if (token.TokenFlags.HasFlag(TokenFlags.CommandName)) return Brush("Command");
        if (token.TokenFlags.HasFlag(TokenFlags.TypeName)) return Brush("Type");
        if (token.TokenFlags.HasFlag(TokenFlags.MemberName)) return Brush("Member");
        if ((token.TokenFlags & (TokenFlags.BinaryOperator | TokenFlags.UnaryOperator | TokenFlags.AssignmentOperator)) != 0) return Brush("Operator");
        if (token.Kind is TokenKind.Identifier or TokenKind.Generic) return Brush("CommandArgument");
        return null;
    }
}

public sealed class ConsoleColorizer(Func<SessionModel?> session, Func<EditorTheme> theme) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (session() is not { } current || DesktopTheme.HighContrast) return;
        foreach (var span in current.OutputSpans)
        {
            if (span.Start >= line.EndOffset) break;
            if (span.End <= line.Offset) continue;
            var key = span.Kind switch
            {
                OutputKind.Error => "Stream.Error",
                OutputKind.Warning => "Stream.Warning",
                OutputKind.Verbose => "Stream.Verbose",
                OutputKind.Debug => "Stream.Debug",
                _ => "Console.Foreground"
            };
            ChangeLinePart(Math.Max(line.Offset, span.Start), Math.Min(line.EndOffset, span.End),
                element => element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.Parse(theme().Colors[key]))));
            ApplyStyle(Math.Max(line.Offset, span.Start), Math.Min(line.EndOffset, span.End), span.Style, theme().Colors[key]);
            if (span.CodeStart > span.Start && span.PromptStyles is { } promptStyles)
                ApplyPromptStyles(line, span.Start, promptStyles);
            if (span.Analysis is null) continue;
            Token? previous = null;
            foreach (var token in span.Analysis.Tokens)
            {
                var start = span.CodeStart + token.Extent.StartOffset;
                var end = span.CodeStart + token.Extent.EndOffset;
                if (start >= line.EndOffset) break;
                var brush = PowerShellColorizer.ColorFor(token, previous, theme(), "Console");
                previous = token;
                if (brush is null || end <= line.Offset) continue;
                ChangeLinePart(Math.Max(line.Offset, start), Math.Min(line.EndOffset, end),
                    element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
        if (!current.Console.HasPrompt) return;
        ApplyPromptStyles(line, current.Console.TranscriptEnd, current.Console.PromptParts);
        if (line.EndOffset <= current.Console.InputStart) return;
        var analysis = current.Console.InputAnalysis;
        Token? prior = null;
        foreach (var token in analysis.Tokens)
        {
            var start = current.Console.InputStart + token.Extent.StartOffset;
            var end = current.Console.InputStart + token.Extent.EndOffset;
            var brush = PowerShellColorizer.ColorFor(token, prior, theme(), "Console");
            prior = token;
            if (brush is not null && start < line.EndOffset && end > line.Offset)
                ChangeLinePart(Math.Max(line.Offset, start), Math.Min(line.EndOffset, end),
                    element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private void ApplyPromptStyles(DocumentLine line, int offset, IReadOnlyList<OutputEntry> parts)
    {
        foreach (var part in parts)
        {
            var end = offset + part.Text.Length;
            if (offset < line.EndOffset && end > line.Offset)
                ApplyStyle(Math.Max(line.Offset, offset), Math.Min(line.EndOffset, end), part.Style);
            offset = end;
        }
    }

    private void ApplyStyle(int start, int end, OutputStyle? style, string? defaultForeground = null)
    {
        if (style is null || start >= end) return;
        ChangeLinePart(start, end, element =>
        {
            var fg = style.Foreground;
            var bg = style.Background;
            if (style.Inverse)
                (fg, bg) = (bg ?? theme().Colors["Console.TextBackground"], fg ?? defaultForeground ?? theme().Colors["Console.Foreground"]);
            if (fg is { } foreground)
                element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.Parse(foreground)));
            if (bg is { } background)
                element.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(Color.Parse(background)));
            if (style.Bold) element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily,
                element.TextRunProperties.Typeface.Style, FontWeight.Bold));
            if (style.Underline) element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
        });
    }
}

public sealed class ScriptAdornments(Func<ScriptTab?> file, Func<DebugLocation?> debug, Func<EditorTheme> theme) : IBackgroundRenderer
{
    private ScriptTab? cachedFile;
    private DebugLocation? cachedLocation;
    private ITextSourceVersion? cachedVersion;
    private (int Start, int End)? pausedSpan;

    public KnownLayer Layer => KnownLayer.Background;
    public void Draw(TextView textView, DrawingContext context)
    {
        if (file() is not { } current || !textView.VisualLinesValid || DesktopTheme.HighContrast) return;
        var location = debug();
        if (cachedFile != current || cachedLocation != location || cachedVersion?.CompareAge(current.Document.Version) != 0)
        {
            cachedFile = current;
            cachedLocation = location;
            cachedVersion = current.Document.Version;
            pausedSpan = PausedStatementSpan(current, location);
        }
        var breakpoints = current.LineBreakpoints.ToDictionary(spec => spec.Line);
        foreach (var line in textView.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var isDebug = pausedSpan is null && BreakpointVisuals.IsPausedLine(current, location, number);
            var isBreakpoint = breakpoints.TryGetValue(number, out var spec) && spec.Enabled;
            if (!isDebug && !isBreakpoint) continue;
            var y = line.VisualTop - textView.ScrollOffset.Y;
            var marker = BreakpointVisuals.MarkerColor(theme(), isDebug);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(12, marker.R, marker.G, marker.B)),
                null, new Rect(0, y, textView.Bounds.Width, line.Height));
        }
        if (pausedSpan is { } span)
        {
            var marker = BreakpointVisuals.MarkerColor(theme(), paused: true);
            foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView,
                new SimpleSegment(span.Start, span.End - span.Start)))
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, marker.R, marker.G, marker.B)), null, rectangle);
        }
    }

    public static (int Start, int End)? PausedStatementSpan(ScriptTab current, DebugLocation? location) =>
        location is not null && !current.File.IsDirty && !EditorAnalysis.IsXmlDocument(current.File.Path) &&
        BreakpointVisuals.IsPausedLine(current, location, location.Line)
            ? EditorAnalysis.StatementAtPosition(current.Document.Text, location.Line, location.Column) : null;
}

public sealed class ScriptDiagnosticRenderer(Func<EditorAnalysisResult> analysis, Func<EditorTheme> theme) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid || analysis() is not { State: EditorAnalysisState.Available } result) return;
        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic.Span.End > textView.Document.TextLength) continue;
            var brush = DesktopTheme.HighContrast ? DesktopTheme.Brush("WindowTextBrush") :
                new SolidColorBrush(Color.Parse(theme().Colors[diagnostic.Severity == EditorDiagnosticSeverity.Error
                    ? "Stream.Error" : "Script.Parameter"]));
            foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView,
                new SimpleSegment(diagnostic.Span.Start, diagnostic.Span.Length)))
            {
                var width = Math.Max(4, rectangle.Width);
                var geometry = new StreamGeometry();
                using (var drawing = geometry.Open())
                {
                    drawing.BeginFigure(new(rectangle.Left, rectangle.Bottom - 1), false);
                    for (var x = 2d; x < width; x += 2)
                        drawing.LineTo(new(rectangle.Left + x, rectangle.Bottom - (x % 4 == 0 ? 1 : 3)));
                    drawing.LineTo(new(rectangle.Left + width, rectangle.Bottom - 1));
                    drawing.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(brush, 1), geometry);
            }
        }
    }
}

public sealed class PowerShellCompletion(CompletionResult result) : ICompletionData
{
    public IImage? Image => null;
    public string Text => result.CompletionText;
    public object Content => result.ListItemText;
    public object Description => result.ToolTip;
    public double Priority => 0;
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        if (textArea.ReadOnlySectionProvider.CanInsert(completionSegment.Offset) &&
            textArea.ReadOnlySectionProvider.GetDeletableSegments(completionSegment).Sum(segment => segment.Length) == completionSegment.Length)
            textArea.Document.Replace(completionSegment, result.CompletionText);
    }
}
