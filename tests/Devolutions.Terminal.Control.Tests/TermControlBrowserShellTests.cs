using Avalonia.Headless.XUnit;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class TermControlBrowserShellTests
{
    [AvaloniaFact]
    public async Task BrowserShellFeedsEngineAndEchoesCommands()
    {
        var connection = new BrowserShellConnection();
        var control = new TermControl();
        control.ConnectionFactory = _ => connection;

        try
        {
            await control.StartAsync(ProfileSettings.CreateBrowserShell(), 80, 24);
            await WaitForTextAsync(control, "dt-wasm");

            connection.Write("echo e2e-ok\r");
            await WaitForTextAsync(control, "e2e-ok");

            connection.Write("help\r");
            await WaitForTextAsync(control, "about");
            Assert.True(control.IsRunning);
        }
        finally
        {
            await control.CloseAsync();
        }
    }

    private static async Task WaitForTextAsync(TermControl control, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string text;
        do
        {
            text = TerminalBufferExport.ToPlainText(
                control.Engine.CreateSnapshot(includeHistory: true).Buffer);
            if (text.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }
}
