namespace Devolutions.Terminal.Connection;

/// <summary>
/// Adjusts <c>wsl.exe</c> command lines before they are handed to ConPTY.
/// </summary>
/// <remarks>
/// <para>
/// <c>wsl.exe</c> re-reads <c>GetCommandLineW</c> when it is the console process.
/// A quoted distribution name is then looked up including the quote characters,
/// which fails with <c>WSL_E_DISTRO_NOT_FOUND</c>. A single-token name must be
/// passed without quotes. Names that contain whitespace stay quoted so
/// CreateProcess does not split them.
/// </para>
/// </remarks>
internal static class WslCommandLine
{
    public static string Normalize(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (!IsWslInvocation(commandLine))
        {
            return commandLine;
        }

        return UnquoteFlag(UnquoteFlag(commandLine, "--distribution"), "-d");
    }

    private static bool IsWslInvocation(string commandLine)
    {
        var span = commandLine.AsSpan().TrimStart();
        if (span.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<char> token;
        if (span[0] == '"')
        {
            var end = span[1..].IndexOf('"');
            if (end < 0)
            {
                return false;
            }

            token = span.Slice(1, end);
        }
        else
        {
            var end = span.IndexOf(' ');
            token = end < 0 ? span : span[..end];
        }

        return token.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("wsl", StringComparison.OrdinalIgnoreCase);
    }

    private static string UnquoteFlag(string commandLine, string flag)
    {
        var search = 0;
        while (search < commandLine.Length)
        {
            var index = commandLine.IndexOf(flag, search, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return commandLine;
            }

            var before = index == 0 ? ' ' : commandLine[index - 1];
            var afterIndex = index + flag.Length;
            if (!char.IsWhiteSpace(before) ||
                afterIndex >= commandLine.Length ||
                !char.IsWhiteSpace(commandLine[afterIndex]))
            {
                search = Math.Min(commandLine.Length, index + flag.Length);
                continue;
            }

            var valueStart = afterIndex;
            while (valueStart < commandLine.Length && char.IsWhiteSpace(commandLine[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= commandLine.Length || commandLine[valueStart] != '"')
            {
                search = Math.Min(commandLine.Length, valueStart + 1);
                continue;
            }

            var valueEnd = commandLine.IndexOf('"', valueStart + 1);
            if (valueEnd < 0)
            {
                return commandLine;
            }

            var value = commandLine.AsSpan(valueStart + 1, valueEnd - valueStart - 1);
            if (value.IsEmpty || value.Contains(' ') || value.Contains('\t'))
            {
                search = valueEnd + 1;
                continue;
            }

            commandLine = string.Concat(
                commandLine.AsSpan(0, valueStart),
                value,
                commandLine.AsSpan(valueEnd + 1));
            search = valueStart + value.Length;
        }

        return commandLine;
    }
}
