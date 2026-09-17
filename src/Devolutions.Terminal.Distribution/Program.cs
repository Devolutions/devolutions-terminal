using System.Diagnostics;

namespace Devolutions.Terminal.Distribution;

internal static class Program
{
    public static int Main(string[] args)
    {
        var executableName = OperatingSystem.IsWindows() ? "dt.exe" : "dt";
        var executablePath = Path.Combine(AppContext.BaseDirectory, "payload", executableName);
        if (!File.Exists(executablePath))
        {
            Console.Error.WriteLine(
                $"Devolutions Terminal is not available for '{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}'.");
            Console.Error.WriteLine(
                "Supported runtime identifiers: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64.");
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var mode = File.GetUnixFileMode(executablePath);
                File.SetUnixFileMode(
                    executablePath,
                    mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"Unable to make the packaged dt executable runnable: {exception.Message}");
                return 1;
            }
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.Error.WriteLine("Unable to start the packaged dt executable.");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine($"Unable to start the packaged dt executable: {exception.Message}");
            return 1;
        }
    }
}
