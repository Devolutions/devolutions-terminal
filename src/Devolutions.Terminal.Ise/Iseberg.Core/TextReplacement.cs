using System.Text.RegularExpressions;

namespace Iseberg.Core;

public sealed record ReplacementOptions(bool MatchCase = false, bool WholeWord = false, bool RegularExpression = false,
    bool SearchUp = false, bool WrapAround = true);
public sealed record TextReplacement(int Start, int Length, string Text);

public static class ReplacementSearch
{
    public static IReadOnlyList<TextReplacement> Find(string text, string pattern, string replacement,
        ReplacementOptions options, int start = 0, int? length = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        var count = length ?? text.Length - start;
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + count, text.Length);
        var expression = options.RegularExpression ? CrLfAwareLineEnds(pattern) : Regex.Escape(pattern);
        if (options.WholeWord) expression = @"(?<!\w)(?:" + expression + @")(?!\w)";
        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!options.MatchCase) flags |= RegexOptions.IgnoreCase;
        var regex = new Regex(expression, flags, TimeSpan.FromMilliseconds(500));
        // Scan from the selection start (lookbehinds still see earlier text) so a
        // match straddling the start can't consume text inside the selection.
        var results = new List<TextReplacement>();
        for (var m = regex.Match(text, start); m.Success && m.Index <= start + count; m = m.NextMatch())
        {
            if (m.Index + m.Length <= start + count)
                results.Add(new TextReplacement(m.Index, m.Length, options.RegularExpression ? m.Result(replacement) : replacement));
        }
        return results;
    }

    // Multiline '$' only matches before '\n', so on CRLF text "x$" never matches and
    // "\s+$" eats the '\r'. Rewrite unescaped '$' outside character classes to end
    // before a line break of either style.
    private static string CrLfAwareLineEnds(string pattern)
    {
        const string lineEnd = @"(?=\r\n|(?<!\r)\n|\z)";
        var builder = new System.Text.StringBuilder(pattern.Length + 16);
        var inClass = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var ch = pattern[index];
            if (ch == '\\' && index + 1 < pattern.Length)
            {
                builder.Append(ch).Append(pattern[++index]);
                continue;
            }
            if (inClass)
                inClass = ch != ']';
            else if (ch == '[')
                inClass = true;
            else if (ch == '$')
            {
                builder.Append(lineEnd);
                continue;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    public static TextReplacement? Next(IReadOnlyList<TextReplacement> matches, int offset, ReplacementOptions options)
    {
        if (matches.Count == 0) return null;
        if (options.SearchUp)
            return matches.LastOrDefault(m => m.Start < offset) ?? (options.WrapAround ? matches[^1] : null);
        return matches.FirstOrDefault(m => m.Start >= offset) ?? (options.WrapAround ? matches[0] : null);
    }
}
