using Avalonia.Controls;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.Control.Sample;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // TermControl has no parameterless constructor usable from XAML (its
        // constructor takes an optional ITerminalEngine), so it is built here
        // instead of declared in MainWindow.axaml.
        var terminal = new Devolutions.Terminal.TermControl();
        Content = terminal;

        Opened += async (_, _) =>
        {
            // ProfileSettings defaults to Windows PowerShell, which runs through
            // ConPTY. On Linux and macOS, launch the user's shell instead; it runs
            // through the package's dt-pty-host.
            var profile = OperatingSystem.IsWindows()
                ? new ProfileSettings()
                : new ProfileSettings { Commandline = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh" };
            await terminal.StartAsync(profile, columns: 120, rows: 30);
        };
    }
}
