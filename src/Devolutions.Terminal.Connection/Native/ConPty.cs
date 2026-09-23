using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Devolutions.Terminal.Connection.Native;

[SupportedOSPlatform("windows")]
internal static partial class ConPty
{
    [LibraryImport("conpty.dll", EntryPoint = "ConptyCreatePseudoConsole")]
    internal static partial int CreatePseudoConsole(Kernel32.Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out nint phPC);

    [LibraryImport("conpty.dll", EntryPoint = "ConptyResizePseudoConsole")]
    internal static partial int ResizePseudoConsole(SafePseudoConsoleHandle hPC, Kernel32.Coord size);

    [LibraryImport("conpty.dll", EntryPoint = "ConptyClosePseudoConsole")]
    internal static partial void ClosePseudoConsole(nint hPC);
}
