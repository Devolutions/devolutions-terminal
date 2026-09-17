using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Devolutions.Terminal.Settings.Editor.Controls;

public sealed class ColorField : UserControl
{
    private static readonly string[] Presets =
    [
        "#0C0C0C", "#CCCCCC", "#FFFFFF", "#C50F1F", "#13A10E", "#C19C00",
        "#0037DA", "#881798", "#3A96DD", "#767676", "#E74856", "#16C60C",
        "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6",
    ];

    private readonly TextBox _text;
    private readonly Border _swatch;
    private readonly Popup _popup;
    private bool _updating;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ColorField, string?>(
            nameof(Text),
            defaultBindingMode: BindingMode.TwoWay);

    public ColorField()
    {
        _swatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _text = new TextBox
        {
            MinWidth = 92,
            PlaceholderText = "#RRGGBB",
        };
        _text.Classes.Add("field");
        _text.LostFocus += (_, _) => Text = _text.Text;
        _text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Text = _text.Text;
                e.Handled = true;
            }
        };

        var palette = new WrapPanel { MaxWidth = 160, Margin = new Thickness(8) };
        _popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = palette,
            },
        };
        foreach (var preset in Presets)
        {
            var selected = preset;
            var chip = new Border
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.Parse(preset)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            chip.PointerPressed += (_, e) =>
            {
                Text = selected;
                _popup.IsOpen = false;
                e.Handled = true;
            };
            palette.Children.Add(chip);
        }
        _swatch.PointerPressed += (_, e) =>
        {
            _popup.PlacementTarget = _swatch;
            _popup.IsOpen = !_popup.IsOpen;
            e.Handled = true;
        };

        var layout = new DockPanel { LastChildFill = true };
        var swatchHost = new Panel { Margin = new Thickness(0, 0, 6, 0), Children = { _swatch, _popup } };
        DockPanel.SetDock(swatchHost, Dock.Left);
        layout.Children.Add(swatchHost);
        layout.Children.Add(_text);
        Content = layout;
        UpdateFromText();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            UpdateFromText();
        }
    }

    private void UpdateFromText()
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            var value = Text ?? string.Empty;
            if (_text.Text != value)
            {
                _text.Text = value;
            }

            _swatch.Background = Color.TryParse(value, out var color)
                ? new SolidColorBrush(color)
                : Brushes.Transparent;
        }
        finally
        {
            _updating = false;
        }
    }
}
