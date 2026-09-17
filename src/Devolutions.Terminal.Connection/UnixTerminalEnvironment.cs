namespace Devolutions.Terminal.Connection;

internal static class UnixTerminalEnvironment
{
    public const string TermProgram = "Devolutions.Terminal";

    internal static readonly string[] MacOsExtraPathDirectories =
    [
        "/opt/homebrew/bin",
        "/opt/homebrew/sbin",
        "/usr/local/bin",
        "/opt/local/bin",
    ];

    public static void Apply(IDictionary<string, string?> environment, bool isMacOs)
    {
        ArgumentNullException.ThrowIfNull(environment);
        environment.TryAdd("TERM", "xterm-256color");
        environment.TryAdd("COLORTERM", "truecolor");
        if (!isMacOs)
        {
            return;
        }

        environment.TryAdd("TERM_PROGRAM", TermProgram);
        environment.TryGetValue("PATH", out var path);
        path ??= Environment.GetEnvironmentVariable("PATH");
        environment["PATH"] = AugmentMacOsPath(path);
    }

    public static string AugmentMacOsPath(string? path)
    {
        var parts = (path ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(parts, StringComparer.Ordinal);
        var extra = new List<string>();
        foreach (var directory in MacOsExtraPathDirectories)
        {
            if (!Directory.Exists(directory) || !seen.Add(directory))
            {
                continue;
            }

            extra.Add(directory);
        }

        return extra.Count == 0
            ? string.Join(':', parts)
            : string.Join(':', extra.Concat(parts));
    }
}
