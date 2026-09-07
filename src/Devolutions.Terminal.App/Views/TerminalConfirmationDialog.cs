using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Devolutions.Terminal.App.Views;

internal sealed class TerminalConfirmationDialog
{
    private bool _isOpen;

    public async Task<bool> ShowAsync(Window owner, string title, string message, string acceptLabel)
    {
        // A second action must not silently reuse consent for a different operation.
        if (_isOpen)
        {
            return false;
        }

        _isOpen = true;
        try
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            var accept = new Button { Content = acceptLabel };
            var dialog = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(24),
                    Spacing = 20,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 12,
                            Children = { cancel, accept },
                        },
                    },
                },
            };
            cancel.Click += (_, _) => dialog.Close(false);
            accept.Click += (_, _) => dialog.Close(true);
            dialog.Opened += (_, _) => cancel.Focus();
            return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        }
        finally
        {
            _isOpen = false;
        }
    }
}
