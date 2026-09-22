using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Devolutions.Terminal.Browser;

public partial class BrowserTerminalApp : Application
{
    public static BrowserTerminalView? CurrentView { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var view = new BrowserTerminalView();
        CurrentView = view;
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = view;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
