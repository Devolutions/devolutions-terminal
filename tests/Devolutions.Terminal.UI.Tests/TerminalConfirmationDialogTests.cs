using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Devolutions.Terminal.App.Views;
using Xunit;

namespace Devolutions.Terminal.UI.Tests;

public sealed class TerminalConfirmationDialogTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DialogRequiresExplicitAcceptance(bool accept)
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var service = new TerminalConfirmationDialog();
            var result = service.ShowAsync(owner, "Confirm paste", "Commands may execute.", "Paste");
            Assert.False(result.IsCompleted);
            var dialog = Assert.Single(owner.OwnedWindows);
            if (accept)
            {
                var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(),
                    button => Equals(button.Content, "Paste"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else
            {
                dialog.Close();
            }

            Assert.Equal(accept, await result);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task OverlappingRequestDoesNotReuseConsent()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var service = new TerminalConfirmationDialog();
            var first = service.ShowAsync(owner, "Paste", "First request", "Paste");
            Assert.False(await service.ShowAsync(owner, "Close", "Second request", "Close"));
            Assert.Single(owner.OwnedWindows).Close(true);
            Assert.True(await first);
        }
        finally
        {
            owner.Close();
        }
    }
}
