using System.Runtime.Versioning;

namespace Devolutions.Terminal.Connection;

[SupportedOSPlatform("windows")]
internal static class WindowsElevation
{
    internal static string WrapCommandLine(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        var executable = FindGsudo(Environment.GetEnvironmentVariable("PATH"), File.Exists)
            ?? throw new FileNotFoundException(
                "Running a profile as Administrator requires gsudo.exe on PATH. Install gsudo and restart Devolutions Terminal.",
                "gsudo.exe");
        return BuildCommandLine(commandLine, executable);
    }

    internal static string BuildCommandLine(string commandLine, string executable) =>
        $"\"{executable}\" --inline --direct run {commandLine}";

    internal static string? FindGsudo(string? path, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        foreach (var entry in (path ?? string.Empty).Split(Path.PathSeparator))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var executable = Path.Combine(directory, "gsudo.exe");
            if (fileExists(executable))
            {
                return executable;
            }
        }

        return null;
    }
}
