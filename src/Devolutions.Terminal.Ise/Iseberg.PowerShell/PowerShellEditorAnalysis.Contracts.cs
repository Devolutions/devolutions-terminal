using System.Management.Automation.Language;
using Iseberg.Core;
using Token = System.Management.Automation.Language.Token;
using TokenKind = System.Management.Automation.Language.TokenKind;
using ParseError = System.Management.Automation.Language.ParseError;
using ScriptExtent = Iseberg.Core.ScriptExtent;

namespace Iseberg.PowerShellHost;

internal static partial class PowerShellEditorAnalysis
{
    private static ScriptExtent Extent(IScriptExtent extent) => new(extent.StartOffset, extent.EndOffset,
        extent.StartLineNumber, extent.StartColumnNumber, extent.EndLineNumber, extent.EndColumnNumber, extent.Text);

    private static Iseberg.Core.Token ConvertToken(Token token) =>
        new((Iseberg.Core.TokenKind)(long)token.Kind, (Iseberg.Core.TokenFlags)(long)token.TokenFlags, Extent(token.Extent), token.Text)
        {
            NestedTokens = token is StringExpandableToken { NestedTokens: { } nested }
                ? nested.Select(ConvertToken).ToArray() : null,
            VariablePath = token is VariableToken variable
                ? new(variable.VariablePath.UserPath, variable.VariablePath.IsUnqualified, variable.VariablePath.IsDriveQualified) : null
        };

    private static ScriptAnalysis ConvertAnalysis(ScriptBlockAst ast, Token[] tokens, ParseError[] errors,
        IReadOnlyList<(int Start, int End)> folds)
    {
        var stack = new Stack<(TokenKind Kind, int Offset)>();
        var pairs = new List<BracePair>();
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
                    pairs.Add(new(open.Offset, token.Extent.StartOffset));
                }
                else stack.Clear();
            }
        }
        return new(tokens.Select(ConvertToken).ToArray(),
            errors.Select(error => new Iseberg.Core.ParseError(error.ErrorId, error.Message,
                Extent(error.Extent), error.IncompleteInput)).ToArray(),
            folds.Distinct().OrderBy(fold => fold.Start).ThenByDescending(fold => fold.End)
                .Select(fold => new ScriptSpan(fold.Start, fold.End)).ToArray())
        {
            BracePairs = pairs,
            Statements = ast.FindAll(node => node is StatementAst && node is not StatementBlockAst, true)
                .Select(node => Extent(node.Extent)).ToArray(),
            Commands = ast.FindAll(node => node is CommandAst, true).OfType<CommandAst>()
                .Select(command => new CommandSpan(command.Extent.StartOffset, command.Extent.EndOffset,
                    command.GetCommandName())).ToArray()
        };
    }
}
