using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Devolutions.Terminal.App.Views;
using Devolutions.Terminal.Settings;
using Xunit;
using static Devolutions.Terminal.UI.Tests.PowerShellIseTestHarness;

namespace Devolutions.Terminal.UI.Tests;

public sealed class IsebergVisualTests
{
    [AvaloniaFact]
    public void DefaultAndLegacyIseIconsUseBundledLogoWithoutReplacingCustomOrTerminalIcons()
    {
        var profile = ProfileSettings.CreatePowerShellIse();
        Assert.Equal(ProfileSettings.PowerShellIseIcon, profile.Icon);
        Assert.Equal(ProfileSettings.PowerShellIseIcon, ProfileVisualDefaults.Icon(profile));
        profile.Icon = "\uE943";
        Assert.Equal(ProfileSettings.PowerShellIseIcon, ProfileVisualDefaults.Icon(profile));
        profile.Icon = null;
        Assert.Equal(ProfileSettings.PowerShellIseIcon, ProfileVisualDefaults.Icon(profile));
        profile.Icon = "custom.png";
        Assert.Equal("custom.png", ProfileVisualDefaults.Icon(profile));
        Assert.Equal("ms-appx:///ProfileIcons/pwsh.png", ProfileVisualDefaults.Icon(ProfileSettings.CreatePwsh()));
        var terminal = ProfileSettings.CreatePwsh();
        terminal.Icon = "\uE943";
        Assert.Equal("\uE943", ProfileVisualDefaults.Icon(terminal));

        using var stream = AssetLoader.Open(new Uri(
            "avares://Devolutions.Terminal.App/Assets/ProfileIcons/iseberg.scale-100.png"));
        var header = new byte[24];
        stream.ReadExactly(header);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8]);
        Assert.Equal(128, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
        Assert.Equal(128, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }

    [AvaloniaTheory]
    [InlineData(480, 320)]
    [InlineData(1100, 700)]
    public async Task CompactChromeKeepsCodeZoomIndependentAndToolsWithinViewport(int width, int height)
    {
        await InitializeRuntimeAsync();
        await CompactChromeCoreAsync(width, height);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CompactChromeCoreAsync(int width, int height)
    {
        using var environment = new IseTestEnvironment();
        var profile = environment.Profile();
        profile.IseColorTheme = IsebergThemes.MonochromeGreen;
        profile.FontSize = 11 * 4.0 / 3;
        var tab = new PowerShellIseTab(profile);
        await using var host = new IseTestHost(tab);
        host.Owner.Width = width;
        host.Owner.Height = height;
        await host.InitializeAsync();
        host.Owner.UpdateLayout();

        var workbench = tab.Workbench;
        var files = workbench.FindControl<TabStrip>("FileTabs")!;
        var title = Assert.Single(files.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == workbench.Workbench.SelectedSession!.SelectedFile!.File.Title);
        var chromeFont = title.FontSize;
        Assert.Equal(workbench.FontSize, chromeFont);
        Assert.InRange(files.Bounds.Height, 28, 36);
        var footer = workbench.FindControl<Border>("WorkbenchStatusBar")!;
        Assert.InRange(footer.Bounds.Height, 20, 30);
        var toolbar = workbench.FindControl<Border>("WorkbenchToolbar")!;
        var tools = toolbar.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Equal(16, tools.Length);
        Assert.All(tools, button =>
        {
            var origin = button.TranslatePoint(default, toolbar)!.Value;
            Assert.True(origin.X >= 0 && origin.X + button.Bounds.Width <= toolbar.Bounds.Width);
            Assert.True(origin.Y >= 0 && origin.Y + button.Bounds.Height <= toolbar.Bounds.Height);
        });
        var editor = workbench.ScriptEditorView.TextEditor;
        Assert.Equal(11 * 4.0 / 3, editor.FontSize, precision: 8);
        Assert.Equal(11, tab.Terminal.FontSize, precision: 8);

        var zoom = workbench.FindControl<Slider>("ZoomSlider")!;
        zoom.Value = 200;
        host.Owner.UpdateLayout();
        var track = Assert.Single(zoom.GetVisualDescendants().OfType<Track>());
        Assert.Equal(200, track.Value);
        var thumb = track.Thumb!;
        var thumbOrigin = thumb.TranslatePoint(default, zoom)!.Value;
        Assert.True(thumbOrigin.Y >= 0 && thumbOrigin.Y + thumb.Bounds.Height <= zoom.Bounds.Height,
            $"Zoom={zoom.Bounds}, track={track.Bounds}, margin={track.Margin}, thumb={thumb.Bounds}, origin={thumbOrigin}");
        Assert.Equal(22 * 4.0 / 3, editor.FontSize, precision: 8);
        Assert.Equal(22, tab.Terminal.FontSize, precision: 8);
        Assert.Equal(chromeFont, title.FontSize);
        Assert.InRange(footer.Bounds.Height, 20, 30);
        Iseberg.DesktopTheme.Refresh(highContrast: false);
        tab.Terminal.ResetFontSize();
        Assert.Equal(22, tab.Terminal.FontSize, precision: 8);
        Assert.True(zoom.Focusable);
        Assert.True(zoom.Focus());
        host.Owner.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        host.Owner.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.True(zoom.Value > 200);
        Assert.Equal(editor.FontSize * 3.0 / 4, tab.Terminal.FontSize, precision: 8);
        Assert.Equal(chromeFont, title.FontSize);
        Assert.Empty(host.Errors);
    }
}
