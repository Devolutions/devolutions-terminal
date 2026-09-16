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
            // A bare ProfileSettings() launches the platform default shell
            // (Windows PowerShell on Windows, /bin/sh-family elsewhere via the
            // package's own PTY connection).
            await terminal.StartAsync(new ProfileSettings(), columns: 120, rows: 30);
        };
    }
}
