using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Devolutions.Terminal.Connection.Native;
using Microsoft.Win32;

namespace Devolutions.Terminal.Connection;

/// <summary>
/// Builds the Windows environment block for a child process, mirroring the
/// Windows Terminal <c>til::env</c> behavior.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TerminalLaunchOptions.ReloadEnvironmentVariables"/> takes
/// precedence over <see cref="TerminalLaunchOptions.InheritEnvironment"/>:
/// when it is set, the environment is regenerated from the registry and the
/// host process environment is not inherited, because inheriting it would
/// reintroduce the stale values the reload exists to discard.
/// </para>
/// <para>
/// Profile overrides follow Windows Terminal's
/// <c>set_user_environment_var</c>: values are expanded against the
/// already-constructed environment (so <c>PATH=%PATH%;C:\tools</c> appends),
/// and <c>TEMP</c>/<c>TMP</c> are shortened with <c>GetShortPathNameW</c>.
/// An empty value is a no-op, matching Windows Terminal, which never stores
/// empty values; this repository additionally treats a <see langword="null"/>
/// value as a deletion.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsEnvironment
{
    private const string ProgramFilesKeyPath = @"Software\Microsoft\Windows\CurrentVersion";

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
    ];

    public static SortedDictionary<string, string> Create(TerminalLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var variables = options.ReloadEnvironmentVariables
            ? Regenerate()
            : NewEnvironment();

        if (!options.ReloadEnvironmentVariables && options.InheritEnvironment)
        {
            foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                if (pair.Key is string key && pair.Value is string value)
                {
                    variables[key] = value;
                }
            }
        }

        ApplyOverrides(variables, options.EnvironmentVariables);
        return variables;
    }

    /// <summary>
    /// Applies profile overrides on top of an already-constructed environment.
    /// </summary>
    internal static void ApplyOverrides(
        IDictionary<string, string> variables,
        IReadOnlyDictionary<string, string?> overrides)
    {
        foreach (var pair in overrides)
        {
            if (string.IsNullOrEmpty(pair.Key))
            {
                continue;
            }

            if (pair.Value is null)
            {
                variables.Remove(pair.Key);
                continue;
            }

            SetUserEnvironmentVariable(variables, pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// Expands <paramref name="value"/> against <paramref name="variables"/>
    /// and stores it, matching <c>til::env::set_user_environment_var</c>.
    /// </summary>
    internal static void SetUserEnvironmentVariable(
        IDictionary<string, string> variables,
        string name,
        string value)
    {
        var expanded = ExpandEnvironmentVariables(value, variables);
        if (expanded.Length == 0)
        {
            return;
        }

        variables[name] = IsTempVariable(name) ? GetShortPath(expanded) : expanded;
    }

    /// <summary>
    /// Returns the registry value to environment variable mapping used to
    /// source the Program Files family, matching Windows Terminal's
    /// architecture-conditional table.
    /// </summary>
    internal static IReadOnlyList<ProgramFilesMapping> GetProgramFilesMappings(
        bool is64BitProcess,
        Architecture processArchitecture)
    {
        var mappings = new List<ProgramFilesMapping>
        {
            new("ProgramFilesDir", "ProgramFiles"),
            new("CommonFilesDir", "CommonProgramFiles"),
        };

        if (!is64BitProcess)
        {
            return mappings;
        }

        if (processArchitecture == Architecture.Arm64)
        {
            mappings.Add(new("ProgramFilesDir (Arm)", "ProgramFiles(Arm)"));
            mappings.Add(new("CommonFilesDir (Arm)", "CommonProgramFiles(Arm)"));
        }

        mappings.Add(new("ProgramFilesDir (x86)", "ProgramFiles(x86)"));
        mappings.Add(new("CommonFilesDir (x86)", "CommonProgramFiles(x86)"));
        mappings.Add(new("ProgramW6432Dir", "ProgramW6432"));
        mappings.Add(new("CommonW6432Dir", "CommonProgramW6432"));
        return mappings;
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

                if (!isPath)
                {
                    var plain = ExpandEnvironmentVariables(entry.Value, variables);
                    if (plain.Length != 0)
                    {
                        variables[entry.Name] = IsTempVariable(entry.Name)
                            ? GetShortPath(plain)
                            : plain;
                    }

                    continue;
                }

                var value = expand
                    ? ExpandEnvironmentVariables(entry.Value, variables)
                    : entry.Value;
                if (variables.TryGetValue(entry.Name, out var existing) && existing.Length != 0)
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

        ApplyProgramFilesVariables(variables);
        ApplyRegistryVariables(
            variables,
            ReadRegistryVariables(
                RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"));
        ApplyRegistryVariables(
            variables,
            ReadRegistryVariables(RegistryHive.CurrentUser, "Environment"));
        ApplyRegistryVariables(
            variables,
            ReadRegistryVariables(RegistryHive.CurrentUser, "Volatile Environment"));
        using var process = Process.GetCurrentProcess();
        ApplyRegistryVariables(
            variables,
            ReadRegistryVariables(
                RegistryHive.CurrentUser,
                $@"Volatile Environment\{process.SessionId}"));
        return variables;
    }

    private static void ApplyProgramFilesVariables(IDictionary<string, string> variables)
    {
        using var root = OpenBaseKey(RegistryHive.LocalMachine);
        using var key = OpenSubKey(root, ProgramFilesKeyPath);
        if (key is null)
        {
            return;
        }

        var mappings = GetProgramFilesMappings(
            Environment.Is64BitProcess,
            RuntimeInformation.ProcessArchitecture);
        foreach (var mapping in mappings)
        {
            var value = ReadRegistryValue(key, mapping.ValueName)?.Value;
            if (!string.IsNullOrEmpty(value))
            {
                SetUserEnvironmentVariable(variables, mapping.VariableName, value);
            }
        }
    }

    private static IReadOnlyList<RegistryEnvironmentVariable> ReadRegistryVariables(
        RegistryHive hive,
        string path)
    {
        using var root = OpenBaseKey(hive);
        using var key = OpenSubKey(root, path);
        if (key is null)
        {
            return [];
        }

        string[] names;
        try
        {
            names = key.GetValueNames();
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return [];
        }

        var variables = new List<RegistryEnvironmentVariable>(names.Length);
        foreach (var name in names)
        {
            var entry = ReadRegistryValue(key, name);
            if (entry is { } value)
            {
                variables.Add(value);
            }
        }

        return variables;
    }

    private static RegistryEnvironmentVariable? ReadRegistryValue(RegistryKey key, string name)
    {
        try
        {
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                is not string value)
            {
                return null;
            }

            return new RegistryEnvironmentVariable(name, value, key.GetValueKind(name));
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return null;
        }
    }

    private static RegistryKey? OpenBaseKey(RegistryHive hive)
    {
        // Windows Terminal relies on the process-bitness registry view; the
        // explicit view keeps a 32-bit host redirected to Wow6432Node and a
        // 64-bit or ARM64 host on the native view.
        var view = Environment.Is64BitProcess ? RegistryView.Registry64 : RegistryView.Registry32;
        try
        {
            return RegistryKey.OpenBaseKey(hive, view);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return null;
        }
    }

    private static RegistryKey? OpenSubKey(RegistryKey? root, string path)
    {
        if (root is null)
        {
            return null;
        }

        try
        {
            return root.OpenSubKey(path);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return null;
        }
    }

    private static bool IsRegistryFailure(Exception exception) =>
        exception is SecurityException or UnauthorizedAccessException or IOException;

    private static string GetShortPath(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        try
        {
            var buffer = new char[260];
            var length = Kernel32.GetShortPathNameW(path, ref buffer[0], (uint)buffer.Length);
            if (length > buffer.Length)
            {
                buffer = new char[length];
                length = Kernel32.GetShortPathNameW(path, ref buffer[0], (uint)buffer.Length);
            }

            return length == 0 || length > buffer.Length
                ? path
                : new string(buffer, 0, (int)length);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return path;
        }
    }

    private static string ExpandEnvironmentVariables(
        string value,
        IDictionary<string, string> variables)
    {
        var result = new StringBuilder(value.Length);
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

    private static bool IsTempVariable(string name) =>
        name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TMP", StringComparison.OrdinalIgnoreCase);

    private static SortedDictionary<string, string> NewEnvironment() =>
        new(StringComparer.OrdinalIgnoreCase);

    internal readonly record struct ProgramFilesMapping(string ValueName, string VariableName);

    internal readonly record struct RegistryEnvironmentVariable(
        string Name,
        string Value,
        RegistryValueKind Kind);
}
