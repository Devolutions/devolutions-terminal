using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Devolutions.Terminal.App.Actions;
using Devolutions.Terminal.App.Platform;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class MacOsNativeMenuTests
{
    [Fact]
    public void ApplicationMenuDefinesAboutAndSettingsAndDefersStandardItemsToAvalonia()
    {
        var invoked = new List<string>();
        var menu = MacOsNativeMenu.CreateApplicationMenu(
            about: () => invoked.Add("about"),
            settings: () => invoked.Add("settings"));

        var items = menu.Items
            .OfType<NativeMenuItem>()
            .Where(static item => item is not NativeMenuItemSeparator)
            .ToArray();
        Assert.Equal(
            [
                "About Devolutions Terminal",
                "Settings…",
            ],
            items.Select(static item => item.Header ?? string.Empty).ToArray());
        Assert.DoesNotContain(
            items,
            static item => item.Header?.Contains("Avalonia", StringComparison.OrdinalIgnoreCase) == true);

        Assert.Equal(
            new KeyGesture(Key.OemComma, KeyModifiers.Meta),
            items.Single(static item => item.Header == "Settings…").Gesture);

        ((INativeMenuItemExporterEventsImplBridge)items.Single(static item => item.Header == "About Devolutions Terminal")).RaiseClicked();
        ((INativeMenuItemExporterEventsImplBridge)items.Single(static item => item.Header == "Settings…")).RaiseClicked();

        Assert.Equal(["about", "settings"], invoked);
        Assert.False(string.IsNullOrWhiteSpace(Devolutions.Terminal.App.Views.AboutWindow.ResolveVersionText()));
    }

    [Fact]
    public void DockMenuReflectsOpenWindowsProfilesAndNewTabAvailability()
    {
        var windows = new List<MacOsDockWindowEntry>();
        var profiles = new List<ProfileSettings>
        {
            new() { Name = "zsh", Guid = "{11111111-1111-1111-1111-111111111111}" },
            new() { Name = "pwsh", Guid = "{22222222-2222-2222-2222-222222222222}" },
        };
        var events = new List<string>();

        var dockMenu = MacOsNativeMenu.CreateDockMenu(
            getWindows: () => windows,
            getProfiles: () => profiles,
            newWindow: () => events.Add("new-window"),
            newTab: () => events.Add("new-tab"),
            openProfileInNewWindow: profile => events.Add($"profile:{profile.Name}"),
            out var refreshDock);

        // When no windows are open, New Tab is omitted while New Window and profiles remain available.
        Assert.DoesNotContain(dockMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "New Tab");
        var withProfile = Assert.Single(dockMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "New Window With Profile");
        Assert.Equal(
            ["zsh", "pwsh"],
            withProfile.Menu!.Items.OfType<NativeMenuItem>().Select(static item => item.Header ?? string.Empty).ToArray());

        windows.Add(new MacOsDockWindowEntry("Main Shell", IsActive: true, () => events.Add("activate:Main Shell")));
        refreshDock();

        var activeWindowItem = Assert.Single(dockMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "Main Shell");
        Assert.True(activeWindowItem.IsChecked);
        Assert.Contains(dockMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "New Tab");

        ((INativeMenuItemExporterEventsImplBridge)withProfile.Menu.Items.OfType<NativeMenuItem>().First()).RaiseClicked();
        Assert.Equal(["profile:zsh"], events);
    }

    [Fact]
    public void KeyGestureParsingAndMnemonicStrippingFollowAvaloniaConventions()
    {
        Assert.Equal("Copy", MacOsNativeMenu.StripAccessKey("_Copy"));
        Assert.Equal("Recent Files", MacOsNativeMenu.StripAccessKey("Recent _Files"));
        Assert.Equal("a_b", MacOsNativeMenu.StripAccessKey("a__b"));

        var prevTabGesture = AvaloniaKeyChord.TryToKeyGesture(KeyChord.Parse("cmd+shift+["), out var parsed);
        Assert.True(prevTabGesture);
        Assert.Equal(new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta | KeyModifiers.Shift), parsed);
    }

    [Fact]
    public void WindowMenuUpdatesSplitPanesFullscreenTabsAndHostedWorkbenchMenus()
    {
        var host = new FakeWindowMenuHost();
        var menu = MacOsNativeMenu.CreateWindowMenu(host, out var refresh);

        var fileMenu = menu.Items.OfType<NativeMenuItem>().Single(static item => item.Header == "File").Menu!;
        var viewMenu = menu.Items.OfType<NativeMenuItem>().Single(static item => item.Header == "View").Menu!;
        var windowMenu = menu.Items.OfType<NativeMenuItem>().Single(static item => item.Header == "Window").Menu!;

        Assert.Contains(fileMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "Close Tab");
        Assert.DoesNotContain(fileMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "Close Pane");
        Assert.Contains(viewMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "Enter Full Screen");

        host.ActivePaneCount = 2;
        host.IsFullScreen = true;
        host.Tabs =
        [
            new MacOsTabMenuEntry("zsh", IsActive: false, () => host.Dispatched.Add("tab:0")),
            new MacOsTabMenuEntry("pwsh", IsActive: true, () => host.Dispatched.Add("tab:1")),
        ];
        refresh();

        Assert.Contains(fileMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "Close Pane");
        Assert.Contains(viewMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "Exit Full Screen");
        var activeTab = Assert.Single(windowMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "pwsh");
        Assert.True(activeTab.IsChecked);

        // Hosting a PowerShell ISE workbench reveals Tools, Debug, and Add-ons in the window menu bar.
        var workbench = new Menu();
        for (var i = 0; i < 7; i++)
        {
            var top = new MenuItem { Header = $"Category{i}" };
            top.Items.Add(new MenuItem { Header = $"_Item{i}", Tag = $"Action{i}" });
            workbench.Items.Add(top);
        }

        host.HostedWorkbenchMenu = workbench;
        refresh();

        Assert.True(menu.Items.OfType<NativeMenuItem>().Single(static item => item.Header == "Tools").IsVisible);
        Assert.True(menu.Items.OfType<NativeMenuItem>().Single(static item => item.Header == "Debug").IsVisible);
        Assert.True(menu.Items.OfType<NativeMenuItem>().Single(static item => item.Header == "Add-ons").IsVisible);
        var toolsMenu = menu.Items.OfType<NativeMenuItem>().Single(static item => item.Header == "Tools").Menu!;
        Assert.Contains(toolsMenu.Items.OfType<NativeMenuItem>(), static item => item.Header == "Item3");
    }

    private sealed class FakeWindowMenuHost : IMacOsWindowMenuHost
    {
        public List<string> Dispatched { get; } = [];
        public Window? Window => null;
        public AppSettings Settings { get; } = SettingsService.CreateDefault();
        public bool IsFullScreen { get; set; }
        public bool IsFocusMode { get; set; }
        public bool HasActiveTerminal { get; set; } = true;
        public bool HasTerminalSelection { get; set; }
        public bool CanPasteTerminal { get; set; } = true;
        public bool CanDuplicateTab { get; set; } = true;
        public bool CanRestoreClosedTab { get; set; }
        public bool CanFindMatch { get; set; }
        public List<MacOsTabMenuEntry> Tabs { get; set; } =
            [new MacOsTabMenuEntry("zsh", IsActive: true, static () => { })];
        public int TabCount => Tabs.Count;
        public int ActiveTabIndex => Tabs.FindIndex(static tab => tab.IsActive);
        public int ActivePaneCount { get; set; } = 1;
        public bool IsPaneZoomed { get; set; }
        public bool IsBroadcastInput { get; set; }
        public bool IsPaneReadOnly { get; set; }
        public Menu? HostedWorkbenchMenu { get; set; }

        public IReadOnlyList<MacOsTabMenuEntry> GetTabEntries() => Tabs;
        public Bitmap? ResolveProfileIcon(ProfileSettings profile) => null;
        public Task DispatchAsync(ActionAndArgs action)
        {
            Dispatched.Add(action.Action.ToString());
            return Task.CompletedTask;
        }
        public void LaunchProfile(ProfileSettings profile) => Dispatched.Add($"launch:{profile.Name}");
        public void OpenHelpUrl(string url) => Dispatched.Add($"help:{url}");
    }
}
