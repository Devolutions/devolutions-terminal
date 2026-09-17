using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace Devolutions.Terminal.Settings.Editor.Controls;

public sealed class FontFaceField : UserControl
{
    private readonly ComboBox _combo;
    private readonly List<string> _fonts;
    private bool _updating;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<FontFaceField, string?>(
            nameof(Text),
            defaultBindingMode: BindingMode.TwoWay);

    public FontFaceField()
    {
        _fonts = FontManager.Current.SystemFonts
            .Select(static font => font.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _combo = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = _fonts,
        };
        _combo.Classes.Add("field");
        _combo.SelectionChanged += (_, _) =>
        {
            if (!_updating && _combo.SelectedItem is string name)
            {
                Text = name;
            }
        };
        Content = _combo;
        UpdateFromText();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IReadOnlyList<string> Fonts => _fonts;

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
            var value = Text;
            if (!string.IsNullOrWhiteSpace(value) &&
                !_fonts.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                _fonts.Insert(0, value);
                _combo.ItemsSource = null;
                _combo.ItemsSource = _fonts;
            }

            _combo.SelectedItem = _fonts.FirstOrDefault(font =>
                string.Equals(font, value, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _updating = false;
        }
    }
}
