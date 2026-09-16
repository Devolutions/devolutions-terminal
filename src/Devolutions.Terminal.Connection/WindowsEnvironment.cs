using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Devolutions.Terminal.Connection;

[SupportedOSPlatform("windows")]
internal static class WindowsEnvironment
{
    private static readonly string[] PreservedVariables =
    [
        "SystemRoot",
        "SystemDrive",
        "ALLUSERSPROFILE",
        "PUBLIC",
        "ProgramData",
        "COMPUTERNAME",
        "USERNAME",
        "USERDOMAIN",
        "USERDNSDOMAIN",
        "HOMEDRIVE",
        "HOMESHARE",
        "HOMEPATH",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432",
    ];

    public static SortedDictionary<string, string> Create(TerminalLaunchOptions options)
    {
        if (options.ReloadEnvironmentVariables)
        {
            return Regenerate();
        }

        var variables = NewEnvironment();
        if (!options.InheritEnvironment)
        {
            return variables;
        }

        foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
        {
            if (pair.Key is string key && pair.Value is string value)
            {
                variables[key] = value;
            }
        }

        return variables;
    }

    private static SortedDictionary<string, string> Regenerate()
    {
        var variables = NewEnvironment();
        foreach (var name in PreservedVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                variables[name] = value;
            }
        }

        ApplyRegistryVariables(
            variables,
            ReadRegistryVariables(
                Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"));
        ApplyRegistryVariables(
            variables,
            ReadRegistryVariables(Registry.CurrentUser, "Environment"));
        ApplyRegistryVariables(
            variables,
            ReadRegistryVariables(Registry.CurrentUser, "Volatile Environment"));
        using var process = Process.GetCurrentProcess();
        ApplyRegistryVariables(
            variables,
            ReadRegistryVariables(
                Registry.CurrentUser,
                $@"Volatile Environment\{process.SessionId}"));
        return variables;
    }

    internal static void ApplyRegistryVariables(
        IDictionary<string, string> variables,
        IEnumerable<RegistryEnvironmentVariable> registryVariables)
    {
        var entries = registryVariables as IReadOnlyCollection<RegistryEnvironmentVariable> ??
            registryVariables.ToArray();
        foreach (var expand in new[] { false, true })
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || string.IsNullOrEmpty(entry.Value))
                {
                    continue;
                }

                var isPath = IsPathVariable(entry.Name);
                if ((entry.Kind == RegistryValueKind.ExpandString || isPath) != expand)
                {
                    continue;
                }

                var value = expand
                    ? ExpandEnvironmentVariables(entry.Value, variables)
                    : entry.Value;
                if (isPath && variables.TryGetValue(entry.Name, out var existing))
                {
                    variables[entry.Name] = existing.EndsWith(';')
                        ? existing + value
                        : existing + ';' + value;
                }
                else
                {
                    variables[entry.Name] = value;
                }
            }
        }
    }

    private static IReadOnlyList<RegistryEnvironmentVariable> ReadRegistryVariables(
        RegistryKey root,
        string path)
    {
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            return [];
        }

        var variables = new List<RegistryEnvironmentVariable>();
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(
                name,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (value is not null)
            {
                variables.Add(new RegistryEnvironmentVariable(name, value, key.GetValueKind(name)));
            }
        }

        return variables;
    }

    private static string ExpandEnvironmentVariables(
        string value,
        IDictionary<string, string> variables)
    {
        var result = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '%')
            {
                result.Append(value[index++]);
                continue;
            }

            var end = value.IndexOf('%', index + 1);
            if (end < 0)
            {
                result.Append(value.AsSpan(index));
                break;
            }

            var name = value[(index + 1)..end];
            if (variables.TryGetValue(name, out var replacement))
            {
                result.Append(replacement);
            }
            else
            {
                result.Append(value.AsSpan(index, end - index + 1));
            }

            index = end + 1;
        }

        return result.ToString();
    }

    private static bool IsPathVariable(string name) =>
        name.Equals("Path", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LibPath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Os2LibPath", StringComparison.OrdinalIgnoreCase);

    private static SortedDictionary<string, string> NewEnvironment() =>
        new(StringComparer.OrdinalIgnoreCase);

    internal readonly record struct RegistryEnvironmentVariable(
        string Name,
        string Value,
        RegistryValueKind Kind);
}
