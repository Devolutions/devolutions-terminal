using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Devolutions.Terminal.Automation;

public static class AutomationHostLauncher
{
    private const uint HandleFlagInherit = 1;
    private static readonly object s_processStartLock = new();

    public static async Task EnsureRunningAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var client = new AutomationIpcClient();
        if (await IsAvailableAsync(client, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartHostProcess(startInfo);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAvailableAsync(client, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        if (process.HasExited)
        {
            var detail = standardError.IsCompletedSuccessfully ? standardError.Result.Trim() : string.Empty;
            throw new InvalidOperationException(
                $"Terminal automation host exited with code {process.ExitCode} before becoming ready." +
                (detail.Length > 0 ? $" {detail}" : string.Empty));
        }

        throw new TimeoutException("Timed out waiting for the terminal automation host.");
    }

    internal static Process StartHostProcess(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return OperatingSystem.IsWindows()
            ? StartHostProcessWindows(startInfo)
            : Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the terminal automation host.");
    }

    [SupportedOSPlatform("windows")]
    private static Process StartHostProcessWindows(ProcessStartInfo startInfo)
    {
        lock (s_processStartLock)
        {
            var clearedHandles = new List<nint>(3);
            try
            {
                // Process.Start can otherwise leak the caller's redirected streams
                // into the long-lived host, preventing agent pipelines from reaching EOF.
                foreach (var standardHandle in new[] { -10, -11, -12 })
                {
                    var handle = GetStdHandle(standardHandle);
                    if (handle == nint.Zero || handle == -1 || clearedHandles.Contains(handle))
                    {
                        continue;
                    }

                    if (!GetHandleInformation(handle, out var flags))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if ((flags & HandleFlagInherit) == 0)
                    {
                        continue;
                    }

                    if (!SetHandleInformation(handle, HandleFlagInherit, 0))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    clearedHandles.Add(handle);
                }

                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Failed to start the terminal automation host.");
            }
            finally
            {
                Win32Exception? error = null;
                foreach (var handle in clearedHandles)
                {
                    if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
                    {
                        error ??= new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }

                if (error is not null)
                {
                    throw error;
                }
            }
        }
    }

    public static (string Executable, IReadOnlyList<string> PrefixArguments) GetCurrentProcessCommand()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the current executable path.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Environment.GetCommandLineArgs().FirstOrDefault()
                ?? throw new InvalidOperationException("Could not determine the current .NET assembly.");
            return (executable, [assembly]);
        }

        return (executable, []);
    }

    public static void DetachFromParentSession()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        if (SetSessionId() >= 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (GetSessionId(0) != Environment.ProcessId)
        {
            throw new Win32Exception(error, "Could not detach the automation host from its parent session.");
        }
    }

    private static async Task<bool> IsAvailableAsync(
        AutomationIpcClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await client.ListAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or
                                          InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSessionId();

    [DllImport("libc", EntryPoint = "getsid", SetLastError = true)]
    private static extern int GetSessionId(int processId);
}
