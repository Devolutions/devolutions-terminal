using System.Xml;

namespace Iseberg.Core;

public static class EditorAnalysis
{
    public static string? CommandNameAtCaret(ScriptAnalysis analysis, int caret)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(caret);
        return analysis.Commands.Where(command => command.Start <= caret && command.End >= caret)
            .OrderBy(command => command.End - command.Start).FirstOrDefault()?.Name;
    }

    public static (int Open, int Close)? MatchingBrace(ScriptAnalysis analysis, int caret)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(caret);
        foreach (var offset in new[] { caret, caret - 1 })
            foreach (var pair in analysis.BracePairs)
                if (pair.Open == offset || pair.Close == offset) return (pair.Open, pair.Close);
        return null;
    }

    private static IEnumerable<Token> Flatten(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.NestedTokens is { } nested)
            {
                foreach (var child in Flatten(nested)) yield return child;
            }
            else yield return token;
        }
    }

    public static IReadOnlySet<CompletionResultType>? CompletionFilter(ScriptAnalysis analysis, string text, int caret)
    {
        if (caret < 1 || caret > text.Length) return null;
        var tokens = analysis.Tokens;
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
        if (token?.NestedTokens is { } nested)
            return TokenAtCaret(nested, caret) ?? token;
        return token;
    }

    public static bool IsXmlDocument(string? path) =>
        Path.GetExtension(path)?.Equals(".xml", StringComparison.OrdinalIgnoreCase) == true ||
        Path.GetExtension(path)?.Equals(".ps1xml", StringComparison.OrdinalIgnoreCase) == true;

    public static Token? VariableAtOffset(IEnumerable<Token> tokens, int offset)
    {
        foreach (var token in tokens)
        {
            if (offset < token.Extent.StartOffset || offset >= token.Extent.EndOffset) continue;
            if (token.VariablePath is not null) return token;
            if (token.NestedTokens is { } nested)
                return VariableAtOffset(nested, offset);
        }
        return null;
    }

    public static (int Start, int End)? StatementAtPosition(ScriptAnalysis analysis, int line, int column)
    {
        if (line < 1 || column < 1) return null;
        var statement = analysis.Statements.Where(extent => extent.StartLineNumber == line && extent.StartColumnNumber == column)
            .OrderBy(extent => extent.EndOffset - extent.StartOffset).FirstOrDefault();
        return statement is null ? null : (statement.StartOffset, statement.EndOffset);
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

    public static ScriptAnalysis AnalyzeXml(string text)
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
        return new([], [], folds.OrderBy(fold => fold.Start).Select(fold => new ScriptSpan(fold.Start, fold.End)).ToArray())
        {
            IsXml = true,
            XmlTokens = spans,
            Diagnostics = diagnostics
        };
    }
}
