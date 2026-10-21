using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iseberg.Core;

[JsonConverter(typeof(ScriptExtentJsonConverter))]
public sealed record ScriptExtent(int StartOffset, int EndOffset, int StartLineNumber,
    int StartColumnNumber, int EndLineNumber, int EndColumnNumber, string Text);
public sealed record VariablePath(string UserPath, bool IsUnqualified, bool IsDriveQualified);
[JsonConverter(typeof(TokenJsonConverter))]
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

/// <summary>
/// Extents and tokens dominate editor analysis payloads, so they use positional arrays instead of named
/// properties: [start, end, startLine, startColumn, endLine, endColumn, text?]. Empty text is omitted.
/// </summary>
public sealed class ScriptExtentJsonConverter : JsonConverter<ScriptExtent>
{
    public override ScriptExtent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Expect(ref reader, JsonTokenType.StartArray);
        var start = NextInt32(ref reader);
        var end = NextInt32(ref reader);
        var startLine = NextInt32(ref reader);
        var startColumn = NextInt32(ref reader);
        var endLine = NextInt32(ref reader);
        var endColumn = NextInt32(ref reader);
        Read(ref reader);
        var text = "";
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            text = reader.GetString() ?? throw new JsonException("Invalid extent text.");
            Read(ref reader);
            Expect(ref reader, JsonTokenType.EndArray);
        }
        return new(start, end, startLine, startColumn, endLine, endColumn, text);
    }

    public override void Write(Utf8JsonWriter writer, ScriptExtent value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.StartOffset);
        writer.WriteNumberValue(value.EndOffset);
        writer.WriteNumberValue(value.StartLineNumber);
        writer.WriteNumberValue(value.StartColumnNumber);
        writer.WriteNumberValue(value.EndLineNumber);
        writer.WriteNumberValue(value.EndColumnNumber);
        if (!string.IsNullOrEmpty(value.Text)) writer.WriteStringValue(value.Text);
        writer.WriteEndArray();
    }

    internal static void Read(ref Utf8JsonReader reader)
    {
        if (!reader.Read()) throw new JsonException("Truncated editor analysis payload.");
    }

    internal static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (reader.TokenType != type) throw new JsonException($"Expected {type} in editor analysis payload.");
    }

    internal static int NextInt32(ref Utf8JsonReader reader)
    {
        Read(ref reader);
        return reader.GetInt32();
    }
}

/// <summary>[kind, flags, extent, text, nestedTokens?, variablePath?] with trailing nulls omitted.</summary>
public sealed class TokenJsonConverter : JsonConverter<Token>
{
    private static readonly ScriptExtentJsonConverter Extents = new();

    public override Token Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ScriptExtentJsonConverter.Expect(ref reader, JsonTokenType.StartArray);
        ScriptExtentJsonConverter.Read(ref reader);
        var kind = (TokenKind)reader.GetInt64();
        ScriptExtentJsonConverter.Read(ref reader);
        var flags = (TokenFlags)reader.GetInt64();
        ScriptExtentJsonConverter.Read(ref reader);
        var extent = Extents.Read(ref reader, typeof(ScriptExtent), options);
        ScriptExtentJsonConverter.Read(ref reader);
        var text = reader.GetString() ?? throw new JsonException("Invalid token text.");
        Token[]? nested = null;
        VariablePath? variable = null;
        ScriptExtentJsonConverter.Read(ref reader);
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var tokens = new List<Token>();
                for (ScriptExtentJsonConverter.Read(ref reader); reader.TokenType != JsonTokenType.EndArray; ScriptExtentJsonConverter.Read(ref reader))
                    tokens.Add(Read(ref reader, typeToConvert, options));
                nested = tokens.ToArray();
            }
            else ScriptExtentJsonConverter.Expect(ref reader, JsonTokenType.Null);
            ScriptExtentJsonConverter.Read(ref reader);
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                ScriptExtentJsonConverter.Expect(ref reader, JsonTokenType.StartArray);
                ScriptExtentJsonConverter.Read(ref reader);
                var path = reader.GetString() ?? throw new JsonException("Invalid variable path.");
                ScriptExtentJsonConverter.Read(ref reader);
                var unqualified = reader.GetBoolean();
                ScriptExtentJsonConverter.Read(ref reader);
                var driveQualified = reader.GetBoolean();
                ScriptExtentJsonConverter.Read(ref reader);
                ScriptExtentJsonConverter.Expect(ref reader, JsonTokenType.EndArray);
                variable = new(path, unqualified, driveQualified);
                ScriptExtentJsonConverter.Read(ref reader);
            }
            ScriptExtentJsonConverter.Expect(ref reader, JsonTokenType.EndArray);
        }
        return new(kind, flags, extent, text) { NestedTokens = nested, VariablePath = variable };
    }

    public override void Write(Utf8JsonWriter writer, Token value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue((long)value.Kind);
        writer.WriteNumberValue((long)value.TokenFlags);
        Extents.Write(writer, value.Extent, options);
        writer.WriteStringValue(value.Text);
        if (value.NestedTokens is not null || value.VariablePath is not null)
        {
            if (value.NestedTokens is { } nested)
            {
                writer.WriteStartArray();
                foreach (var token in nested) Write(writer, token, options);
                writer.WriteEndArray();
            }
            else writer.WriteNullValue();
            if (value.VariablePath is { } variable)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(variable.UserPath);
                writer.WriteBooleanValue(variable.IsUnqualified);
                writer.WriteBooleanValue(variable.IsDriveQualified);
                writer.WriteEndArray();
            }
        }
        writer.WriteEndArray();
    }
}
