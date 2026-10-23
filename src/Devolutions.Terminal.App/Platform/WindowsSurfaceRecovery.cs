using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Devolutions.Terminal.App.Platform;

// Avalonia renders each window into a Windows.UI.Composition drawing surface.
// Windows can drop that surface's contents while the display is off, the session
// is locked or the GPU is reset, and Avalonia neither listens for
// RenderingDeviceReplaced nor presents frames while nothing is damaged. An idle
// terminal then shows only the window backdrop until something happens to repaint
// it. When the display comes back, a WM_PAINT makes Avalonia redraw the whole
// scene, which also recreates the render target if the device was lost.
public static partial class WindowsSurfaceRecovery
{
    internal const uint WmDestroy = 0x0002;
    internal const uint WmDisplayChange = 0x007E;
    internal const uint WmPowerBroadcast = 0x0218;
    internal const uint WmWtsSessionChange = 0x02B1;
    internal const nint PbtApmResumeSuspend = 0x0007;
    internal const nint PbtApmResumeAutomatic = 0x0012;
    internal const nint PbtPowerSettingChange = 0x8013;
    internal const nint WtsConsoleConnect = 0x1;
    internal const nint WtsRemoteConnect = 0x3;
    internal const nint WtsSessionUnlock = 0x8;
    internal const uint DisplayStateOff = 0;

    // GUID_CONSOLE_DISPLAY_STATE: 0 = off, 1 = on, 2 = dimmed.
    internal static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    // The display-on notification can arrive before the display stack has finished
    // reconfiguring, so repaint once more after it settles.
    private static readonly TimeSpan SettledRepaintDelay = TimeSpan.FromSeconds(1);
    private const uint DeviceNotifyWindowHandle = 0;
    private const uint NotifyForThisSession = 0;
    private static bool _installed;

    public static void Install()
    {
        if (!OperatingSystem.IsWindows() || _installed)
        {
            return;
        }

        _installed = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>(OnWindowOpened);
    }

    internal static unsafe bool ShouldRepaint(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmDisplayChange:
                return true;
            case WmWtsSessionChange:
                return wParam is WtsSessionUnlock or WtsConsoleConnect or WtsRemoteConnect;
            case WmPowerBroadcast when wParam is PbtApmResumeSuspend or PbtApmResumeAutomatic:
                return true;
            case WmPowerBroadcast when wParam == PbtPowerSettingChange && lParam != 0:
                var setting = (PowerBroadcastSetting*)lParam;
                return setting->PowerSetting == ConsoleDisplayState &&
                       setting->DataLength >= sizeof(uint) &&
                       setting->Data != DisplayStateOff;
            default:
                return false;
        }
    }

    private static void OnWindowOpened(Window window, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() ||
            window.TryGetPlatformHandle()?.Handle is not { } hwnd ||
            hwnd == 0)
        {
            return;
        }

        new Registration(window, hwnd).Attach();
    }

    [SupportedOSPlatform("windows")]
    private sealed class Registration
    {
        private readonly Window _window;
        private readonly nint _hwnd;
        private readonly Win32Properties.CustomWndProcHookCallback _hook;
        private nint _powerNotification;
        private bool _sessionNotification;
        private bool _destroyed;

        public Registration(Window window, nint hwnd)
        {
            _window = window;
            _hwnd = hwnd;
            _hook = OnMessage;
        }

        public void Attach()
        {
            var displayState = ConsoleDisplayState;
            _powerNotification = RegisterPowerSettingNotification(
                _hwnd,
                in displayState,
                DeviceNotifyWindowHandle);
            _sessionNotification = WTSRegisterSessionNotification(_hwnd, NotifyForThisSession);
            Win32Properties.AddWndProcHookCallback(_window, _hook);
            _window.Closed += (_, _) => Win32Properties.RemoveWndProcHookCallback(_window, _hook);
        }

        private nint OnMessage(nint handle, uint message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == WmDestroy)
            {
                Unregister();
            }
            else if (ShouldRepaint(message, wParam, lParam))
            {
                Repaint();
                DispatcherTimer.RunOnce(Repaint, SettledRepaintDelay);
            }

            return 0;
        }

        private void Repaint()
        {
            if (!_destroyed)
            {
                // Avalonia answers WM_PAINT with a full redraw of the composition target.
                InvalidateRect(_hwnd, 0, false);
            }
        }

        private void Unregister()
        {
            _destroyed = true;
            if (_powerNotification != 0)
            {
                UnregisterPowerSettingNotification(_powerNotification);
                _powerNotification = 0;
            }

            if (_sessionNotification)
            {
                WTSUnRegisterSessionNotification(_hwnd);
                _sessionNotification = false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterPowerSettingNotification(
        nint hRecipient,
        in Guid powerSettingGuid,
        uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint handle);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint hWnd, uint flags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InvalidateRect(
        nint hWnd,
        nint lpRect,
        [MarshalAs(UnmanagedType.Bool)] bool bErase);
}
