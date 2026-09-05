using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace Devolutions.Terminal.Settings.Editor.Controls;

/// <summary>
/// A Windows Terminal style toggle: the current state is written to the left of the switch.
/// </summary>
public sealed class SettingsToggle : UserControl
{
    private readonly TextBlock _state;
    private readonly ToggleSwitch _toggle;

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<SettingsToggle, bool>(
            nameof(IsChecked),
            defaultBindingMode: BindingMode.TwoWay);

    public SettingsToggle()
    {
        _state = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.72,
        };
        _toggle = new ToggleSwitch
        {
            OnContent = string.Empty,
            OffContent = string.Empty,
        };
        _toggle.IsCheckedChanged += (_, _) => IsChecked = _toggle.IsChecked ?? false;
        var layout = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _state, _toggle },
        };
        base.Content = layout;
        UpdateState();
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty)
        {
            UpdateState();
        }
        else if (change.Property == AutomationProperties.NameProperty)
        {
            AutomationProperties.SetName(_toggle, AutomationProperties.GetName(this) ?? string.Empty);
        }
    }

    private void UpdateState()
    {
        _state.Text = IsChecked ? "On" : "Off";
        if (_toggle.IsChecked != IsChecked)
        {
            _toggle.IsChecked = IsChecked;
        }
    }
}
