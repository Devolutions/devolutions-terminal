using System.Runtime.InteropServices.JavaScript;

namespace Devolutions.Terminal.Browser;

public static partial class BrowserTerminalProbe
{
    [JSExport]
    public static bool IsReady() => BrowserTerminalApp.CurrentView?.IsSessionReady == true;

    [JSExport]
    public static string GetScreenText() =>
        BrowserTerminalApp.CurrentView?.GetScreenText() ?? string.Empty;

    [JSExport]
    public static void SendInput(string text) =>
        BrowserTerminalApp.CurrentView?.SendInput(text);

    [JSExport]
    public static int GetTabCount() => BrowserTerminalApp.CurrentView?.TabCount ?? 0;

    [JSExport]
    public static string GetTitle() => BrowserTerminalApp.CurrentView?.Title ?? string.Empty;
}
