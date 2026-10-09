using System.Management.Automation.Language;
using Iseberg.Core;
using Token = System.Management.Automation.Language.Token;
using TokenKind = System.Management.Automation.Language.TokenKind;
namespace Iseberg.PowerShellHost;
internal static partial class PowerShellEditorAnalysis
{
    public static ScriptAnalysis Analyze(string text, string? documentPath)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (EditorAnalysis.IsXmlDocument(documentPath)) throw new ArgumentException("XML analysis belongs to the parent.");
        var ast = Parser.ParseInput(text, out var tokens, out var errors);
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
        return ConvertAnalysis(ast, tokens, errors, folds);
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

}
