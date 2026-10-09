namespace Iseberg.Core;

public sealed record ScriptExtent(int StartOffset, int EndOffset, int StartLineNumber,
    int StartColumnNumber, int EndLineNumber, int EndColumnNumber, string Text);
public sealed record VariablePath(string UserPath, bool IsUnqualified, bool IsDriveQualified);
public sealed record Token(TokenKind Kind, TokenFlags TokenFlags, ScriptExtent Extent, string Text)
{
    public Token[]? NestedTokens { get; init; }
    public VariablePath? VariablePath { get; init; }
}
public sealed record ParseError(string ErrorId, string Message, ScriptExtent Extent, bool IncompleteInput);
public sealed record ScriptSpan(int Start, int End);
public sealed record BracePair(int Open, int Close);
public sealed record CommandSpan(int Start, int End, string? Name);
public sealed record ScriptDiagnostic(string Code, string Message, int Start, int End);
public sealed record XmlTokenSpan(int Start, int End, string Kind);

public sealed record ScriptAnalysis(Token[] Tokens, ParseError[] Errors, IReadOnlyList<ScriptSpan> Folds)
{
    public bool IsXml { get; init; }
    public IReadOnlyList<XmlTokenSpan> XmlTokens { get; init; } = [];
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = Errors.Select(error =>
        new ScriptDiagnostic(error.ErrorId, error.Message, error.Extent.StartOffset, error.Extent.EndOffset)).ToArray();
    public IReadOnlyList<BracePair> BracePairs { get; init; } = [];
    public IReadOnlyList<ScriptExtent> Statements { get; init; } = [];
    public IReadOnlyList<CommandSpan> Commands { get; init; } = [];
}
