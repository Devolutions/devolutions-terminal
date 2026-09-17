namespace Devolutions.Terminal.Settings;

public static class UnixShellCommandline
{
    public static readonly string[] MacOsExtraPathDirectories =
    [
        "/opt/homebrew/bin",
        "/opt/homebrew/sbin",
        "/usr/local/bin",
        "/opt/local/bin",
    ];

    public static string WithLogin(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        return executable.Contains(' ', StringComparison.Ordinal)
            ? executable
            : executable + " -l";
    }

    public static string DefaultNewProfileCommandline()
    {
        if (OperatingSystem.IsWindows())
        {
            return @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe";
        }

        return OperatingSystem.IsMacOS() ? "/bin/zsh -l" : "/bin/bash";
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
