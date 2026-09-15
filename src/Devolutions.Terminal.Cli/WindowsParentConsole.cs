using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Devolutions.Terminal.Cli;

public static class WindowsParentConsole
{
    private const int AttachParentProcess = -1;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private const uint FileTypeDisk = 0x0001;
    private const uint FileTypePipe = 0x0003;

    private static TextWriter? s_standardOut;
    private static TextWriter? s_standardError;
    private static TextReader? s_standardIn;

    public static void Attach()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AttachWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void AttachWindows()
    {
        var outputRedirected = IsRedirected(StandardOutputHandle);
        var errorRedirected = IsRedirected(StandardErrorHandle);
        if (!outputRedirected && !errorRedirected && !AttachConsole(AttachParentProcess))
        {
            return;
        }

        BindStandardStreams();
    }

    [SupportedOSPlatform("windows")]
    private static void BindStandardStreams()
    {
        s_standardOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        s_standardError = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        s_standardIn = new StreamReader(Console.OpenStandardInput());
        Console.SetOut(s_standardOut);
        Console.SetError(s_standardError);
        Console.SetIn(s_standardIn);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRedirected(int standardHandle)
    {
        var handle = GetStdHandle(standardHandle);
        if (handle == nint.Zero || handle == -1)
        {
            return false;
        }

        var fileType = GetFileType(handle);
        return fileType is FileTypeDisk or FileTypePipe;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int nStdHandle);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint hFile);
}
