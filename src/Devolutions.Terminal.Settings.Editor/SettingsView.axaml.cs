using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Devolutions.Terminal.Settings.Editor;

public partial class SettingsView : UserControl
{
    public SettingsView()
        : this(new SettingsEditorViewModel())
    {
    }

    public SettingsView(SettingsEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
    }
}
