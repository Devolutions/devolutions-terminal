using System.Management.Automation.Language;
using System.Management.Automation;
using System.Xml;

namespace Iseberg.Core;

public sealed record ScriptDiagnostic(string Code, string Message, int Start, int End);
public sealed record XmlTokenSpan(int Start, int End, string Kind);

public sealed record ScriptAnalysis(Token[] Tokens, ParseError[] Errors, IReadOnlyList<(int Start, int End)> Folds)
{
    public bool IsXml { get; init; }
    public IReadOnlyList<XmlTokenSpan> XmlTokens { get; init; } = [];
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = Errors.Select(error =>
        new ScriptDiagnostic(error.ErrorId, error.Message, error.Extent.StartOffset, error.Extent.EndOffset)).ToArray();
}

public static class EditorAnalysis
{
    public static string? CommandNameAtCaret(string text, int caret)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(caret);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(caret, text.Length);
        var ast = Parser.ParseInput(text, out _, out _);
        return ast.FindAll(node => node is CommandAst && node.Extent.StartOffset <= caret &&
                node.Extent.EndOffset >= caret, searchNestedScriptBlocks: true)
            .OfType<CommandAst>().OrderBy(command => command.Extent.EndOffset - command.Extent.StartOffset)
            .FirstOrDefault()?.GetCommandName();
    }

    public static (int Open, int Close)? MatchingBrace(string text, int caret)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(caret);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(caret, text.Length);
        var stack = new Stack<(TokenKind Kind, int Offset)>();
        var pairs = new List<(int Open, int Close)>();
        Parser.ParseInput(text, out var tokens, out _);
        foreach (var token in Flatten(tokens))
        {
            var kind = token.Kind switch
            {
                TokenKind.AtCurly => TokenKind.LCurly,
                TokenKind.AtParen or TokenKind.DollarParen => TokenKind.LParen,
                _ => token.Kind
            };
            if (kind is TokenKind.LCurly or TokenKind.LParen or TokenKind.LBracket)
                stack.Push((kind, token.Extent.EndOffset - 1));
            else if (kind is TokenKind.RCurly or TokenKind.RParen or TokenKind.RBracket)
            {
                var expected = kind switch
                {
                    TokenKind.RCurly => TokenKind.LCurly,
                    TokenKind.RParen => TokenKind.LParen,
                    _ => TokenKind.LBracket
                };
                if (stack.TryPeek(out var open) && open.Kind == expected)
                {
                    stack.Pop();
                    pairs.Add((open.Offset, token.Extent.StartOffset));
                }
                else stack.Clear();
            }
        }
        foreach (var offset in new[] { caret, caret - 1 })
            foreach (var pair in pairs)
                if (pair.Open == offset || pair.Close == offset) return pair;
        return null;
    }

    private static IEnumerable<Token> Flatten(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            if (token is StringExpandableToken { NestedTokens: { } nested })
            {
                foreach (var child in Flatten(nested)) yield return child;
            }
            else yield return token;
        }
    }

    public static IReadOnlySet<CompletionResultType>? CompletionFilter(string text, int caret)
    {
        if (caret < 1 || caret > text.Length) return null;
        Parser.ParseInput(text, out var tokens, out _);
        var current = TokenAtCaret(tokens, caret);
        var inString = current?.Kind is TokenKind.StringLiteral or TokenKind.StringExpandable or TokenKind.HereStringLiteral or TokenKind.HereStringExpandable;
        if (current?.Kind == TokenKind.Comment) return null;
        var character = text[caret - 1];
        if (character is '\\' or '/' && (inString || current?.Kind is TokenKind.Generic or TokenKind.Identifier))
            return new HashSet<CompletionResultType> { CompletionResultType.ProviderItem, CompletionResultType.ProviderContainer };
        if (character == '$' && current?.Kind is TokenKind.StringExpandable or TokenKind.HereStringExpandable)
            return new HashSet<CompletionResultType> { CompletionResultType.Variable };
        if (inString) return null;
        return character switch
        {
            '$' => new HashSet<CompletionResultType> { CompletionResultType.Variable },
            '-' when current?.Kind is TokenKind.Parameter or TokenKind.Generic ||
                     current?.TokenFlags.HasFlag(TokenFlags.CommandName) == true =>
                new HashSet<CompletionResultType> { CompletionResultType.Command, CompletionResultType.ParameterName },
            ':' when caret >= 2 && text[caret - 2] == ':' =>
                new HashSet<CompletionResultType> { CompletionResultType.Method, CompletionResultType.Property },
            '.' when caret >= 2 && !char.IsWhiteSpace(text[caret - 2]) && current?.Kind != TokenKind.Number &&
                         tokens.LastOrDefault(t => t.Extent.EndOffset <= caret - 1)?.Kind != TokenKind.Number &&
                         text[caret - 2] is not ('*' or '?') =>
                new HashSet<CompletionResultType> { CompletionResultType.Method, CompletionResultType.Property, CompletionResultType.Type, CompletionResultType.Namespace },
            '[' => new HashSet<CompletionResultType> { CompletionResultType.Type, CompletionResultType.Namespace },
            ' ' when tokens.LastOrDefault(t => t.Extent.EndOffset <= caret - 1 && t.Kind != TokenKind.EndOfInput)?.Kind == TokenKind.Parameter =>
                new HashSet<CompletionResultType> { CompletionResultType.ParameterValue },
            _ => null
        };
    }

    private static Token? TokenAtCaret(IEnumerable<Token> tokens, int caret)
    {
        var token = tokens.FirstOrDefault(t => t.Extent.StartOffset < caret && t.Extent.EndOffset >= caret);
        if (token is StringExpandableToken { NestedTokens: { } nested })
            return TokenAtCaret(nested, caret) ?? token;
        return token;
    }

    public static bool IsXmlDocument(string? path) =>
        Path.GetExtension(path)?.Equals(".xml", StringComparison.OrdinalIgnoreCase) == true ||
        Path.GetExtension(path)?.Equals(".ps1xml", StringComparison.OrdinalIgnoreCase) == true;

    public static VariableToken? VariableAtOffset(IEnumerable<Token> tokens, int offset)
    {
        foreach (var token in tokens)
        {
            if (offset < token.Extent.StartOffset || offset >= token.Extent.EndOffset) continue;
            if (token is VariableToken variable) return variable;
            if (token is StringExpandableToken { NestedTokens: { } nested })
                return VariableAtOffset(nested, offset);
        }
        return null;
    }

    public static (int Start, int End)? StatementAtPosition(string text, int line, int column)
    {
        if (line < 1 || column < 1) return null;
        var offset = OffsetAtPosition(text, line, column, out var valid);
        if (!valid || offset >= text.Length) return null;
        var ast = Parser.ParseInput(text, out _, out _);
        // A pipeline is the executable statement, rather than its individual command or variable.
        var statement = ast.FindAll(node => node is StatementAst && node is not StatementBlockAst &&
                node.Extent.StartOffset == offset, searchNestedScriptBlocks: true)
            .OrderBy(node => node.Extent.EndOffset - node.Extent.StartOffset).FirstOrDefault();
        return statement is null ? null : (statement.Extent.StartOffset, statement.Extent.EndOffset);
    }

    public static ScriptAnalysis Analyze(string text) => Analyze(text, null);

    public static ScriptAnalysis Analyze(string text, string? documentPath)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsXmlDocument(documentPath)) return AnalyzeXml(text);
        Parser.ParseInput(text, out var tokens, out var errors);
        var stack = new Stack<Token>();
        var regions = new Stack<Token>();
        var regionFolds = new List<(int Start, int End)>();
        var folds = new List<(int Start, int End)>();
        foreach (var token in Flatten(tokens))
        {
            if (token.Kind is TokenKind.LCurly or TokenKind.AtCurly)
                stack.Push(token);
            else if (token.Kind == TokenKind.RCurly && stack.TryPop(out var open) &&
                     token.Extent.EndLineNumber > open.Extent.StartLineNumber)
                folds.Add((open.Extent.StartOffset, token.Extent.EndOffset));
        }
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Comment && !token.Text.StartsWith("<#", StringComparison.Ordinal) &&
                IsLineDirective(text, token))
            {
                if (IsDirective(token.Text, "#region")) regions.Push(token);
                else if (IsDirective(token.Text, "#endregion") && regions.TryPop(out var region) &&
                    token.Extent.StartLineNumber > region.Extent.StartLineNumber)
                    regionFolds.Add((region.Extent.StartOffset, token.Extent.EndOffset));
            }
            if (token.Extent.EndLineNumber <= token.Extent.StartLineNumber) continue;
            var closed = token.Kind switch
            {
                TokenKind.Comment => token.Text.StartsWith("<#", StringComparison.Ordinal) &&
                    token.Text.EndsWith("#>", StringComparison.Ordinal),
                TokenKind.HereStringLiteral => token.Text.EndsWith("'@", StringComparison.Ordinal),
                TokenKind.HereStringExpandable => token.Text.EndsWith("\"@", StringComparison.Ordinal),
                TokenKind.StringLiteral => token.Text.EndsWith("'", StringComparison.Ordinal),
                TokenKind.StringExpandable => token.Text.EndsWith("\"", StringComparison.Ordinal),
                _ => false
            };
            if (closed && !errors.Any(error => error.IncompleteInput &&
                error.Extent.StartOffset >= token.Extent.StartOffset && error.Extent.StartOffset < token.Extent.EndOffset))
                folds.Add((token.Extent.StartOffset, token.Extent.EndOffset));
        }
        // Unclosed outer regions must not hide even otherwise matched nested directives.
        var unmatchedRegion = regions.Count == 0 ? int.MaxValue : regions.Min(region => region.Extent.StartOffset);
        folds.AddRange(regionFolds.Where(fold => fold.Start < unmatchedRegion));
        return new(tokens, errors, folds.Distinct().OrderBy(f => f.Start).ThenByDescending(f => f.End).ToArray());
    }

    private static bool IsDirective(string text, string directive) =>
        text.StartsWith(directive, StringComparison.OrdinalIgnoreCase) &&
        (text.Length == directive.Length || char.IsWhiteSpace(text[directive.Length]));

    private static bool IsLineDirective(string text, Token token)
    {
        for (var index = token.Extent.StartOffset - 1; index >= 0 && text[index] is not ('\r' or '\n'); index--)
            if (!char.IsWhiteSpace(text[index])) return false;
        return true;
    }

    private static int OffsetAtPosition(string text, int line, int column, out bool valid)
    {
        var currentLine = 1;
        var index = 0;
        while (index < text.Length && currentLine < line)
        {
            if (text[index++] == '\r')
            {
                if (index < text.Length && text[index] == '\n') index++;
                currentLine++;
            }
            else if (text[index - 1] == '\n') currentLine++;
        }
        var start = index;
        while (index < text.Length && text[index] is not ('\r' or '\n')) index++;
        valid = currentLine == line && column > 0 && column - 1 <= index - start;
        return valid ? start + column - 1 : text.Length;
    }

    private static ScriptAnalysis AnalyzeXml(string text)
    {
        var diagnostics = new List<ScriptDiagnostic>();
        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            while (reader.Read()) { }
        }
        catch (XmlException exception)
        {
            var start = OffsetAtPosition(text, Math.Max(1, exception.LineNumber), Math.Max(1, exception.LinePosition), out _);
            diagnostics.Add(new("InvalidXml", exception.Message, start, Math.Min(text.Length, start + 1)));
        }

        var spans = new List<XmlTokenSpan>();
        var folds = new List<(int Start, int End)>();
        var elements = new Stack<(string Name, int Start)>();
        void Fold(int start, int end)
        {
            if (text.AsSpan(start, end - start).IndexOfAny('\r', '\n') >= 0) folds.Add((start, end));
        }
        var offset = 0;
        while (offset < text.Length)
        {
            var start = text.IndexOf('<', offset);
            if (start < 0) break;
            var remaining = text.AsSpan(start);
            var terminator = remaining.StartsWith("<!--", StringComparison.Ordinal) ? "-->" :
                remaining.StartsWith("<![CDATA[", StringComparison.Ordinal) ? "]]>" :
                remaining.StartsWith("<?", StringComparison.Ordinal) ? "?>" : null;
            if (terminator is not null)
            {
                var end = text.IndexOf(terminator, start + 2, StringComparison.Ordinal);
                var closed = end >= 0;
                end = closed ? end + terminator.Length : text.Length;
                spans.Add(new(start, end, terminator == "-->" ? "Comment" : terminator == "]]>" ? "Value" : "Tag"));
                if (closed) Fold(start, end);
                offset = end;
                continue;
            }
            if (remaining.StartsWith("<!", StringComparison.Ordinal)) break;
            var closing = remaining.StartsWith("</", StringComparison.Ordinal);
            var cursor = start + (closing ? 2 : 1);
            var nameStart = cursor;
            while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] is not ('/' or '>' or '<')) cursor++;
            if (cursor == nameStart) { offset = start + 1; continue; }
            var name = text[nameStart..cursor];
            spans.Add(new(nameStart, cursor, "Tag"));
            var complete = false;
            var empty = false;
            while (cursor < text.Length)
            {
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                if (cursor >= text.Length || text[cursor] == '<') break;
                if (text[cursor] == '>' || text[cursor] == '/' && cursor + 1 < text.Length && text[cursor + 1] == '>')
                {
                    empty = text[cursor] == '/';
                    cursor += empty ? 2 : 1;
                    complete = true;
                    break;
                }
                var attributeStart = cursor;
                while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] is not ('=' or '<' or '>' or '/')) cursor++;
                if (cursor == attributeStart) break;
                spans.Add(new(attributeStart, cursor, "Attribute"));
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                if (cursor >= text.Length || text[cursor++] != '=') break;
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                if (cursor >= text.Length || text[cursor] is not ('\'' or '"')) break;
                var valueStart = cursor;
                var quote = text[cursor++];
                while (cursor < text.Length && text[cursor] != quote) cursor++;
                if (cursor >= text.Length) break;
                spans.Add(new(valueStart, ++cursor, "Value"));
            }
            if (complete)
            {
                if (closing)
                {
                    if (elements.TryPeek(out var element) && element.Name == name)
                    {
                        elements.Pop();
                        Fold(element.Start, cursor);
                    }
                    else elements.Clear();
                }
                else if (!empty) elements.Push((name, start));
            }
            offset = Math.Max(start + 1, cursor);
        }
        return new([], [], folds.OrderBy(fold => fold.Start).ToArray())
        {
            IsXml = true,
            XmlTokens = spans,
            Diagnostics = diagnostics
        };
    }
}
