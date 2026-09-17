using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace Devolutions.Terminal.App.Platform;

internal static partial class DesktopCursor
{
    public static bool TryGetPosition(out PixelPoint point)
    {
        if (OperatingSystem.IsWindows() && TryGetWindowsPosition(out point))
        {
            return true;
        }

        if (OperatingSystem.IsMacOS() && TryGetMacOsPosition(out point))
        {
            return true;
        }

        point = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowsCursorPos(out NativePoint point);

    [SupportedOSPlatform("windows")]
    private static bool TryGetWindowsPosition(out PixelPoint point)
    {
        if (GetWindowsCursorPos(out var native))
        {
            point = new PixelPoint(native.X, native.Y);
            return true;
        }

        point = default;
        return false;
    }

    [SupportedOSPlatform("macos")]
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial nint CGEventCreate(nint source);

    [SupportedOSPlatform("macos")]
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial CGPoint CGEventGetLocation(nint eventRef);

    [SupportedOSPlatform("macos")]
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial uint CGMainDisplayID();

    [SupportedOSPlatform("macos")]
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial nint CGDisplayPixelsHigh(uint display);

    [SupportedOSPlatform("macos")]
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint cf);

    [SupportedOSPlatform("macos")]
    private static bool TryGetMacOsPosition(out PixelPoint point)
    {
        point = default;
        var cgEvent = CGEventCreate(0);
        if (cgEvent == 0)
        {
            return false;
        }

        try
        {
            var location = CGEventGetLocation(cgEvent);
            var height = (int)CGDisplayPixelsHigh(CGMainDisplayID());
            point = new PixelPoint(
                (int)Math.Round(location.X),
                (int)Math.Round(height - location.Y));
            return true;
        }
        finally
        {
            CFRelease(cgEvent);
        }
    }
}
