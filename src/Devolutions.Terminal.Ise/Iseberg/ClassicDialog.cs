using Avalonia;
using Avalonia.Controls;

namespace Iseberg;

internal static class ClassicDialog
{
    public static void Apply(Window window)
    {
        DesktopTheme.ApplyWindow(window);
        window.Classes.Add("options");
        window.Styles.Add(new CommandDialogStyles());
        window.Icon = AppIcon.Create();
        window.FontSize = 12 * DesktopTheme.TextScale;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.UseLayoutRounding = true;
    }
}
