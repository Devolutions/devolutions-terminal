using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Devolutions.Terminal.Settings.Editor.Controls;

public sealed class FilePathField : UserControl
{
    private readonly TextBox _text;
    private bool _updating;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<FilePathField, string?>(
            nameof(Text),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<FilePathField, string>(nameof(Title), "Select file");

    public static readonly StyledProperty<bool> ImageFilesProperty =
        AvaloniaProperty.Register<FilePathField, bool>(nameof(ImageFiles));

    public static readonly StyledProperty<bool> FoldersProperty =
        AvaloniaProperty.Register<FilePathField, bool>(nameof(Folders));

    public FilePathField()
    {
        _text = new TextBox { MinWidth = 120 };
        _text.Classes.Add("field");
        _text.LostFocus += (_, _) => Text = _text.Text;
        var browse = new Button
        {
            Content = "…",
            MinWidth = 32,
            Padding = new Thickness(8, 4),
            Margin = new Thickness(6, 0, 0, 0),
        };
        browse.Click += async (_, _) => await BrowseAsync().ConfigureAwait(true);
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        layout.Children.Add(browse);
        layout.Children.Add(_text);
        Content = layout;
        UpdateFromText();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool ImageFiles
    {
        get => GetValue(ImageFilesProperty);
        set => SetValue(ImageFilesProperty, value);
    }

    public bool Folders
    {
        get => GetValue(FoldersProperty);
        set => SetValue(FoldersProperty, value);
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
        }
        finally
        {
            _updating = false;
        }
    }

    private async Task BrowseAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var storage = topLevel?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        string? path;
        if (Folders)
        {
            if (!storage.CanPickFolder)
            {
                return;
            }

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Title,
                AllowMultiple = false,
            }).ConfigureAwait(true);
            path = folders is { Count: > 0 } ? folders[0].TryGetLocalPath() : null;
        }
        else
        {
            if (!storage.CanOpen)
            {
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Title,
                AllowMultiple = false,
                FileTypeFilter = ImageFiles
                    ?
                    [
                        new FilePickerFileType("Images")
                        {
                            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.ico", "*.svg"],
                        },
                        FilePickerFileTypes.All,
                    ]
                    : [FilePickerFileTypes.All],
            }).ConfigureAwait(true);
            path = files is { Count: > 0 } ? files[0].TryGetLocalPath() : null;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            Text = path;
        }
    }
}
