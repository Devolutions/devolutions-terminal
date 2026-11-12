using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Devolutions.Terminal.App.Actions;
using Devolutions.Terminal.App.Models;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Platform;

public sealed record MacOsDockWindowEntry(string Title, bool IsActive, Action Activate);

public sealed record MacOsTabMenuEntry(string Title, bool IsActive, Action Activate);

public interface IMacOsWindowMenuHost
{
    Window? Window { get; }
    AppSettings Settings { get; }
    bool IsFullScreen { get; }
    bool IsFocusMode { get; }
    bool HasActiveTerminal { get; }
    bool HasTerminalSelection { get; }
    bool CanPasteTerminal { get; }
    bool CanDuplicateTab { get; }
    bool CanRestoreClosedTab { get; }
    bool CanFindMatch { get; }
    int TabCount { get; }
    int ActiveTabIndex { get; }
    int ActivePaneCount { get; }
    bool IsPaneZoomed { get; }
    bool IsBroadcastInput { get; }
    bool IsPaneReadOnly { get; }
    Menu? HostedWorkbenchMenu { get; }
    IReadOnlyList<MacOsTabMenuEntry> GetTabEntries();
    Bitmap? ResolveProfileIcon(ProfileSettings profile);
    Task DispatchAsync(ActionAndArgs action);
    void LaunchProfile(ProfileSettings profile);
    void OpenHelpUrl(string url);
}

public static class MacOsNativeMenu
{
    private static bool _secondaryWindowHookInstalled;

    public static void InstallSecondaryWindowMenuHook()
    {
        if (!OperatingSystem.IsMacOS() || _secondaryWindowHookInstalled)
        {
            return;
        }

        _secondaryWindowHookInstalled = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (window is IMacOsWindowMenuHost || NativeMenu.GetMenu(window) is not null)
            {
                return;
            }

            NativeMenu.SetMenu(window, CreateSecondaryWindowMenu(window));
        });
    }

    public static NativeMenu CreateApplicationMenu(
        Action about,
        Action settings)
    {
        ArgumentNullException.ThrowIfNull(about);
        ArgumentNullException.ThrowIfNull(settings);

        // Avalonia automatically appends macOS standard items (Services, Hide,
        // Hide Others, Show All, and Quit) after custom application menu items.
        var menu = new NativeMenu();
        menu.Items.Add(CreateItem("About Devolutions Terminal", about));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateItem(
            "Settings…",
            settings,
            new KeyGesture(Key.OemComma, KeyModifiers.Meta)));
        return menu;
    }

    public static NativeMenu CreateDockMenu(
        Func<IReadOnlyList<MacOsDockWindowEntry>> getWindows,
        Func<IReadOnlyList<ProfileSettings>> getProfiles,
        Action newWindow,
        Action? newTab,
        Action<ProfileSettings> openProfileInNewWindow) =>
        CreateDockMenu(getWindows, getProfiles, newWindow, newTab, openProfileInNewWindow, out _);

    public static NativeMenu CreateDockMenu(
        Func<IReadOnlyList<MacOsDockWindowEntry>> getWindows,
        Func<IReadOnlyList<ProfileSettings>> getProfiles,
        Action newWindow,
        Action? newTab,
        Action<ProfileSettings> openProfileInNewWindow,
        out Action refresh)
    {
        ArgumentNullException.ThrowIfNull(getWindows);
        ArgumentNullException.ThrowIfNull(getProfiles);
        ArgumentNullException.ThrowIfNull(newWindow);
        ArgumentNullException.ThrowIfNull(openProfileInNewWindow);

        var menu = new NativeMenu();
        void Refresh()
        {
            menu.Items.Clear();
            var windows = getWindows();
            if (windows.Count > 0)
            {
                foreach (var entry in windows)
                {
                    var windowItem = CreateItem(entry.Title, entry.Activate);
                    windowItem.ToggleType = MenuItemToggleType.CheckBox;
                    windowItem.IsChecked = entry.IsActive;
                    menu.Items.Add(windowItem);
                }

                menu.Items.Add(new NativeMenuItemSeparator());
            }

            menu.Items.Add(CreateItem("New Window", newWindow));
            var profiles = getProfiles();
            if (profiles.Count > 0)
            {
                var profileMenu = new NativeMenu();
                foreach (var profile in profiles)
                {
                    var captured = profile;
                    profileMenu.Items.Add(CreateItem(captured.Name, () => openProfileInNewWindow(captured)));
                }

                menu.Items.Add(new NativeMenuItem("New Window With Profile") { Menu = profileMenu });
            }

            if (windows.Count > 0 && newTab is not null)
            {
                menu.Items.Add(CreateItem("New Tab", newTab));
            }
        }

        menu.NeedsUpdate += (_, _) => Refresh();
        Refresh();
        refresh = Refresh;
        return menu;
    }

    public static NativeMenu CreateSecondaryWindowMenu(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var undo = CreateItem("Undo", () => TryExecuteFocusedEdit(window, EditCommand.Undo), new KeyGesture(Key.Z, KeyModifiers.Meta));
        var redo = CreateItem("Redo", () => TryExecuteFocusedEdit(window, EditCommand.Redo), new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift));
        var cut = CreateItem("Cut", () => TryExecuteFocusedEdit(window, EditCommand.Cut), new KeyGesture(Key.X, KeyModifiers.Meta));
        var copy = CreateItem("Copy", () => TryExecuteFocusedEdit(window, EditCommand.Copy), new KeyGesture(Key.C, KeyModifiers.Meta));
        var paste = CreateItem("Paste", () => TryExecuteFocusedEdit(window, EditCommand.Paste), new KeyGesture(Key.V, KeyModifiers.Meta));
        var selectAll = CreateItem("Select All", () => TryExecuteFocusedEdit(window, EditCommand.SelectAll), new KeyGesture(Key.A, KeyModifiers.Meta));

        var editSubmenu = CreateSubmenu(
            "Edit",
            undo,
            redo,
            new NativeMenuItemSeparator(),
            cut,
            copy,
            paste,
            new NativeMenuItemSeparator(),
            selectAll);

        void RefreshEdit()
        {
            undo.IsEnabled = CanExecuteFocusedEdit(window, EditCommand.Undo);
            redo.IsEnabled = CanExecuteFocusedEdit(window, EditCommand.Redo);
            cut.IsEnabled = CanExecuteFocusedEdit(window, EditCommand.Cut);
            copy.IsEnabled = CanExecuteFocusedEdit(window, EditCommand.Copy);
            paste.IsEnabled = CanExecuteFocusedEdit(window, EditCommand.Paste);
            selectAll.IsEnabled = CanExecuteFocusedEdit(window, EditCommand.SelectAll);
        }

        AttachRefresh(editSubmenu.Menu!, RefreshEdit);

        var minimize = CreateItem("Minimize", () => window.WindowState = WindowState.Minimized, new KeyGesture(Key.M, KeyModifiers.Meta));
        var zoom = CreateItem("Zoom", () => ToggleWindowZoom(window));
        var windowSubmenu = CreateSubmenu(
            "Window",
            minimize,
            zoom,
            new NativeMenuItemSeparator(),
            CreateItem("Bring All to Front", () => BringAllWindowsToFront(window)));
        AttachRefresh(windowSubmenu.Menu!, () =>
        {
            var canResizeState = window.WindowState != WindowState.FullScreen;
            minimize.IsEnabled = canResizeState && window.CanMinimize;
            zoom.IsEnabled = canResizeState && window.CanResize;
        });

        var menu = new NativeMenu();
        menu.Items.Add(CreateSubmenu(
            "File",
            CreateItem("Close Window", window.Close, new KeyGesture(Key.W, KeyModifiers.Meta))));
        menu.Items.Add(editSubmenu);
        menu.Items.Add(windowSubmenu);
        return menu;
    }

    public static NativeMenu CreateWindowMenu(Func<ShortcutAction, Task> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var menu = new NativeMenu();
        menu.Items.Add(CreateSubmenu(
            "File",
            CreateActionItem("New Tab", ShortcutAction.NewTab, dispatch, Key.T),
            CreateActionItem("New Window", ShortcutAction.NewWindow, dispatch, Key.N),
            new NativeMenuItemSeparator(),
            CreateActionItem("Close Tab", ShortcutAction.CloseTab, dispatch, Key.W),
            CreateActionItem(
                "Close Window",
                ShortcutAction.CloseWindow,
                dispatch,
                Key.W,
                KeyModifiers.Meta | KeyModifiers.Shift)));
        menu.Items.Add(CreateSubmenu(
            "Edit",
            CreateActionItem("Copy", ShortcutAction.CopyText, dispatch, Key.C),
            CreateActionItem("Paste", ShortcutAction.PasteText, dispatch, Key.V),
            CreateActionItem("Select All", ShortcutAction.SelectAll, dispatch, Key.A),
            new NativeMenuItemSeparator(),
            CreateActionItem("Find…", ShortcutAction.Find, dispatch, Key.F)));
        menu.Items.Add(CreateSubmenu(
            "View",
            CreateActionItem(
                "Enter Full Screen",
                ShortcutAction.ToggleFullscreen,
                dispatch,
                Key.F,
                KeyModifiers.Meta | KeyModifiers.Control),
            CreateActionItem(
                "Command Palette…",
                ShortcutAction.ToggleCommandPalette,
                dispatch,
                Key.P,
                KeyModifiers.Meta | KeyModifiers.Shift)));
        menu.Items.Add(CreateSubmenu(
            "Window",
            CreateActionItem("Show Previous Tab", ShortcutAction.PrevTab, dispatch, Key.OemOpenBrackets, KeyModifiers.Meta | KeyModifiers.Shift),
            CreateActionItem("Show Next Tab", ShortcutAction.NextTab, dispatch, Key.OemCloseBrackets, KeyModifiers.Meta | KeyModifiers.Shift)));
        return menu;
    }

    public static NativeMenu CreateWindowMenu(IMacOsWindowMenuHost host) =>
        CreateWindowMenu(host, out _);

    public static NativeMenu CreateWindowMenu(IMacOsWindowMenuHost host, out Action refresh)
    {
        var builder = new WindowMenuBuilder(host);
        var menu = builder.Build();
        refresh = builder.RefreshAll;
        return menu;
    }

    private enum EditCommand
    {
        Undo,
        Redo,
        Cut,
        Copy,
        Paste,
        SelectAll,
    }

    private sealed class WindowMenuBuilder(IMacOsWindowMenuHost host)
    {
        private readonly NativeMenu _root = new();
        private readonly NativeMenu _fileMenu = new();
        private readonly NativeMenu _profileMenu = new();
        private readonly NativeMenu _editMenu = new();
        private readonly NativeMenu _viewMenu = new();
        private readonly NativeMenu _toolsMenu = new();
        private readonly NativeMenu _debugMenu = new();
        private readonly NativeMenu _addonsMenu = new();
        private readonly NativeMenu _windowMenu = new();
        private readonly NativeMenu _helpMenu = new();

        private NativeMenuItem _profileSubmenuItem = null!;
        private NativeMenuItem _toolsTop = null!;
        private NativeMenuItem _debugTop = null!;
        private NativeMenuItem _addonsTop = null!;

        public NativeMenu Build()
        {
            ArgumentNullException.ThrowIfNull(host);

            _profileSubmenuItem = new NativeMenuItem("New Tab With Profile") { Menu = _profileMenu };
            var fileTop = new NativeMenuItem("File") { Menu = _fileMenu };
            var editTop = new NativeMenuItem("Edit") { Menu = _editMenu };
            var viewTop = new NativeMenuItem("View") { Menu = _viewMenu };
            _toolsTop = new NativeMenuItem("Tools") { Menu = _toolsMenu, IsVisible = false };
            _debugTop = new NativeMenuItem("Debug") { Menu = _debugMenu, IsVisible = false };
            _addonsTop = new NativeMenuItem("Add-ons") { Menu = _addonsMenu, IsVisible = false };
            var windowTop = new NativeMenuItem("Window") { Menu = _windowMenu };
            var helpTop = new NativeMenuItem("Help") { Menu = _helpMenu };

            _root.Items.Add(fileTop);
            _root.Items.Add(editTop);
            _root.Items.Add(viewTop);
            _root.Items.Add(_toolsTop);
            _root.Items.Add(_debugTop);
            _root.Items.Add(_addonsTop);
            _root.Items.Add(windowTop);
            _root.Items.Add(helpTop);

            AttachRefresh(_root, RefreshTopLevelVisibility);
            AttachRefresh(_fileMenu, RefreshFileMenu);
            AttachRefresh(_profileMenu, RefreshProfileMenu);
            AttachRefresh(_editMenu, RefreshEditMenu);
            AttachRefresh(_viewMenu, RefreshViewMenu);
            AttachRefresh(_toolsMenu, () => RefreshWorkbenchCategory(_toolsMenu, 3));
            AttachRefresh(_debugMenu, () => RefreshWorkbenchCategory(_debugMenu, 4));
            AttachRefresh(_addonsMenu, () => RefreshWorkbenchCategory(_addonsMenu, 5));
            AttachRefresh(_windowMenu, RefreshWindowMenu);
            AttachRefresh(_helpMenu, RefreshHelpMenu);

            RefreshAll();
            return _root;
        }

        public void RefreshAll()
        {
            RefreshTopLevelVisibility();
            RefreshFileMenu();
            RefreshProfileMenu();
            RefreshEditMenu();
            RefreshViewMenu();
            if (host.HostedWorkbenchMenu is not null)
            {
                RefreshWorkbenchCategory(_toolsMenu, 3);
                RefreshWorkbenchCategory(_debugMenu, 4);
                RefreshWorkbenchCategory(_addonsMenu, 5);
            }
            RefreshWindowMenu();
            RefreshHelpMenu();
        }

        private void RefreshTopLevelVisibility()
        {
            var hasWorkbench = host.HostedWorkbenchMenu is not null;
            _toolsTop.IsVisible = hasWorkbench;
            _debugTop.IsVisible = hasWorkbench;
            _addonsTop.IsVisible = hasWorkbench;
        }

        private void RefreshFileMenu()
        {
            RefreshTopLevelVisibility();
            _fileMenu.Items.Clear();

            _fileMenu.Items.Add(CreateItem(
                "New Tab",
                () => Dispatch(ShortcutAction.NewTab),
                ResolveGesture("Terminal.OpenNewTab", Key.T)));
            RefreshProfileMenu();
            _profileSubmenuItem.IsEnabled = _profileMenu.Items.Count > 0;
            _fileMenu.Items.Add(_profileSubmenuItem);
            _fileMenu.Items.Add(CreateItem(
                "New Window",
                () => Dispatch(ShortcutAction.NewWindow),
                ResolveGesture("Terminal.OpenNewWindow", Key.N)));

            var duplicateTab = CreateItem(
                "Duplicate Tab",
                () => Dispatch(ShortcutAction.DuplicateTab),
                ResolveGesture("Terminal.DuplicateTab"));
            duplicateTab.IsEnabled = host.CanDuplicateTab;
            _fileMenu.Items.Add(duplicateTab);

            var reopenClosed = CreateItem(
                "Reopen Closed Tab",
                () => Dispatch(ShortcutAction.RestoreLastClosed),
                ResolveGesture("Terminal.RestoreLastClosed"));
            reopenClosed.IsEnabled = host.CanRestoreClosedTab;
            _fileMenu.Items.Add(reopenClosed);

            if (host.HostedWorkbenchMenu is { } workbenchMenu &&
                workbenchMenu.Items.OfType<MenuItem>().ElementAtOrDefault(0) is { } iseFileMenu)
            {
                var iseItems = BuildMirroredWorkbenchItems(
                    iseFileMenu,
                    excludeTags: ["Exit", "NewSession", "NewRemoteSession", "CloseSession"],
                    remapMacGestures: true);
                if (iseItems.Count > 0)
                {
                    _fileMenu.Items.Add(new NativeMenuItemSeparator());
                    foreach (var iseItem in iseItems)
                    {
                        _fileMenu.Items.Add(iseItem);
                    }
                }
            }

            _fileMenu.Items.Add(new NativeMenuItemSeparator());
            var splitRight = CreateItem(
                "Split Pane Right",
                () => Dispatch(ShortcutAction.SplitPane, new SplitPaneArgs(SplitDirection: SplitDirection.Right)),
                ResolveGesture("Terminal.DuplicatePaneRight") ?? ResolveGesture("Terminal.SplitPaneRight"));
            splitRight.IsEnabled = host.HasActiveTerminal;
            _fileMenu.Items.Add(splitRight);

            var splitDown = CreateItem(
                "Split Pane Down",
                () => Dispatch(ShortcutAction.SplitPane, new SplitPaneArgs(SplitDirection: SplitDirection.Down)),
                ResolveGesture("Terminal.DuplicatePaneDown") ?? ResolveGesture("Terminal.SplitPaneDown"));
            splitDown.IsEnabled = host.HasActiveTerminal;
            _fileMenu.Items.Add(splitDown);

            _fileMenu.Items.Add(new NativeMenuItemSeparator());
            var exportText = CreateItem(
                "Export Text…",
                () => Dispatch(ShortcutAction.ExportBuffer),
                ResolveGesture("Terminal.ExportBuffer"));
            exportText.IsEnabled = host.HasActiveTerminal;
            _fileMenu.Items.Add(exportText);

            _fileMenu.Items.Add(new NativeMenuItemSeparator());
            var closeGesture = ResolveGesture("Terminal.ClosePane", Key.W);
            if (host.ActivePaneCount > 1)
            {
                _fileMenu.Items.Add(CreateItem(
                    "Close Pane",
                    () => Dispatch(ShortcutAction.ClosePane),
                    closeGesture));
                _fileMenu.Items.Add(CreateItem(
                    "Close Tab",
                    () => Dispatch(ShortcutAction.CloseTab)));
            }
            else
            {
                var closeTab = CreateItem(
                    "Close Tab",
                    () => Dispatch(ShortcutAction.CloseTab),
                    closeGesture);
                closeTab.IsEnabled = host.TabCount > 0;
                _fileMenu.Items.Add(closeTab);
            }

            var closeOthers = CreateItem(
                "Close Other Tabs",
                () => Dispatch(ShortcutAction.CloseOtherTabs),
                ResolveGesture("Terminal.CloseOtherTabs"));
            closeOthers.IsEnabled = host.TabCount > 1;
            _fileMenu.Items.Add(closeOthers);

            _fileMenu.Items.Add(CreateItem(
                "Close Window",
                () => Dispatch(ShortcutAction.CloseWindow),
                ResolveGesture("Terminal.CloseWindow") ?? new KeyGesture(Key.W, KeyModifiers.Meta | KeyModifiers.Shift)));
        }

        private void RefreshProfileMenu()
        {
            _profileMenu.Items.Clear();
            var resolved = NewTabMenuResolver.Resolve(host.Settings);
            PopulateResolvedProfileItems(_profileMenu, resolved);
        }

        private void PopulateResolvedProfileItems(
            NativeMenu target,
            IReadOnlyList<ResolvedNewTabMenuItem> items)
        {
            foreach (var entry in items)
            {
                switch (entry.Type)
                {
                    case ResolvedNewTabMenuItemType.Separator:
                        if (target.Items.Count > 0 && target.Items[^1] is not NativeMenuItemSeparator)
                        {
                            target.Items.Add(new NativeMenuItemSeparator());
                        }
                        break;

                    case ResolvedNewTabMenuItemType.Folder:
                        var folderMenu = new NativeMenu();
                        if (entry.Children is { Count: > 0 } children)
                        {
                            PopulateResolvedProfileItems(folderMenu, children);
                        }

                        target.Items.Add(new NativeMenuItem(entry.Name)
                        {
                            Menu = folderMenu,
                            IsEnabled = folderMenu.Items.Count > 0,
                        });
                        break;

                    case ResolvedNewTabMenuItemType.Profile when entry.Profile is { } profile:
                        var capturedProfile = profile;
                        var profileItem = CreateItem(entry.Name, () => host.LaunchProfile(capturedProfile));
                        profileItem.Icon = host.ResolveProfileIcon(capturedProfile);
                        target.Items.Add(profileItem);
                        break;

                    case ResolvedNewTabMenuItemType.Action when entry.ActionId is { } actionId &&
                         host.Settings.ActionMap.AvailableActions.TryGetValue(actionId, out var action):
                        var capturedAction = action;
                        target.Items.Add(CreateItem(entry.Name, () => _ = host.DispatchAsync(capturedAction)));
                        break;
                }
            }

            while (target.Items.Count > 0 && target.Items[^1] is NativeMenuItemSeparator)
            {
                target.Items.RemoveAt(target.Items.Count - 1);
            }
        }

        private void RefreshEditMenu()
        {
            _editMenu.Items.Clear();
            var hasNonTerminalEditor = HasFocusedTextEditor(host.Window);
            var canUndo = CanExecuteFocusedEdit(host.Window, EditCommand.Undo);
            var canRedo = CanExecuteFocusedEdit(host.Window, EditCommand.Redo);
            var canCut = CanExecuteFocusedEdit(host.Window, EditCommand.Cut);

            var undoItem = CreateItem(
                "Undo",
                () => _ = TryExecuteFocusedEdit(host.Window, EditCommand.Undo),
                hasNonTerminalEditor ? new KeyGesture(Key.Z, KeyModifiers.Meta) : null);
            undoItem.IsEnabled = canUndo;
            _editMenu.Items.Add(undoItem);

            var redoItem = CreateItem(
                "Redo",
                () => _ = TryExecuteFocusedEdit(host.Window, EditCommand.Redo),
                hasNonTerminalEditor ? new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift) : null);
            redoItem.IsEnabled = canRedo;
            _editMenu.Items.Add(redoItem);

            _editMenu.Items.Add(new NativeMenuItemSeparator());

            var cutItem = CreateItem(
                "Cut",
                () => _ = TryExecuteFocusedEdit(host.Window, EditCommand.Cut),
                hasNonTerminalEditor ? new KeyGesture(Key.X, KeyModifiers.Meta) : null);
            cutItem.IsEnabled = canCut;
            _editMenu.Items.Add(cutItem);

            var copyItem = CreateItem(
                "Copy",
                () =>
                {
                    if (!TryExecuteFocusedEdit(host.Window, EditCommand.Copy))
                    {
                        Dispatch(ShortcutAction.CopyText);
                    }
                },
                ResolveGesture("Terminal.CopyToClipboard", Key.C));
            copyItem.IsEnabled = hasNonTerminalEditor
                ? CanExecuteFocusedEdit(host.Window, EditCommand.Copy)
                : host.HasTerminalSelection;
            _editMenu.Items.Add(copyItem);

            var pasteItem = CreateItem(
                "Paste",
                () =>
                {
                    if (!TryExecuteFocusedEdit(host.Window, EditCommand.Paste))
                    {
                        Dispatch(ShortcutAction.PasteText);
                    }
                },
                ResolveGesture("Terminal.PasteFromClipboard", Key.V));
            pasteItem.IsEnabled = hasNonTerminalEditor
                ? CanExecuteFocusedEdit(host.Window, EditCommand.Paste)
                : host.CanPasteTerminal;
            _editMenu.Items.Add(pasteItem);

            var selectAllItem = CreateItem(
                "Select All",
                () =>
                {
                    if (!TryExecuteFocusedEdit(host.Window, EditCommand.SelectAll))
                    {
                        Dispatch(ShortcutAction.SelectAll);
                    }
                },
                ResolveGesture("Terminal.SelectAll", Key.A));
            selectAllItem.IsEnabled = hasNonTerminalEditor
                ? CanExecuteFocusedEdit(host.Window, EditCommand.SelectAll)
                : host.HasActiveTerminal;
            _editMenu.Items.Add(selectAllItem);

            _editMenu.Items.Add(new NativeMenuItemSeparator());

            var findItem = CreateItem(
                "Find…",
                () =>
                {
                    if (!TryInvokeWorkbenchAction("Find"))
                    {
                        Dispatch(ShortcutAction.Find);
                    }
                },
                ResolveGesture("Terminal.FindText", Key.F));
            findItem.IsEnabled = host.HasActiveTerminal || host.HostedWorkbenchMenu is not null;
            _editMenu.Items.Add(findItem);

            var findNext = CreateItem(
                "Find Next",
                () => Dispatch(ShortcutAction.FindMatch, new FindMatchArgs(FindMatchDirection.Next)),
                ResolveGesture("Terminal.FindNextMatch") ?? new KeyGesture(Key.G, KeyModifiers.Meta));
            findNext.IsEnabled = host.CanFindMatch;
            _editMenu.Items.Add(findNext);

            var findPrevious = CreateItem(
                "Find Previous",
                () => Dispatch(ShortcutAction.FindMatch, new FindMatchArgs(FindMatchDirection.Previous)),
                ResolveGesture("Terminal.FindPrevMatch") ?? new KeyGesture(Key.G, KeyModifiers.Meta | KeyModifiers.Shift));
            findPrevious.IsEnabled = host.CanFindMatch;
            _editMenu.Items.Add(findPrevious);

            if (host.HostedWorkbenchMenu is { } workbenchMenu &&
                workbenchMenu.Items.OfType<MenuItem>().ElementAtOrDefault(1) is { } iseEditMenu)
            {
                var iseExtra = BuildMirroredWorkbenchItems(
                    iseEditMenu,
                    excludeTags: ["Undo", "Redo", "Cut", "Copy", "Paste", "SelectAll", "Find"],
                    remapMacGestures: true);
                foreach (var item in iseExtra)
                {
                    _editMenu.Items.Add(item);
                }
            }

            _editMenu.Items.Add(new NativeMenuItemSeparator());

            var marksMenu = CreateSubmenu(
                "Marks",
                CreateEnabledActionItem("Add Mark", host.HasActiveTerminal, () => Dispatch(ShortcutAction.AddMark)),
                CreateEnabledActionItem(
                    "Jump to Previous Mark",
                    host.HasActiveTerminal,
                    () => Dispatch(ShortcutAction.ScrollToMark, new ScrollToMarkArgs(ScrollToMarkDirection.Previous))),
                CreateEnabledActionItem(
                    "Jump to Next Mark",
                    host.HasActiveTerminal,
                    () => Dispatch(ShortcutAction.ScrollToMark, new ScrollToMarkArgs(ScrollToMarkDirection.Next))),
                new NativeMenuItemSeparator(),
                CreateEnabledActionItem("Clear Mark", host.HasActiveTerminal, () => Dispatch(ShortcutAction.ClearMark)),
                CreateEnabledActionItem("Clear All Marks", host.HasActiveTerminal, () => Dispatch(ShortcutAction.ClearAllMarks)));
            marksMenu.IsEnabled = host.HasActiveTerminal;
            _editMenu.Items.Add(marksMenu);

            var clearBuffer = CreateItem(
                "Clear Scrollback",
                () => Dispatch(ShortcutAction.ClearBuffer),
                ResolveGesture("Terminal.ClearBuffer", Key.K));
            clearBuffer.IsEnabled = host.HasActiveTerminal;
            _editMenu.Items.Add(clearBuffer);
        }

        private void RefreshViewMenu()
        {
            _viewMenu.Items.Clear();

            _viewMenu.Items.Add(CreateItem(
                "Command Palette…",
                () => Dispatch(ShortcutAction.ToggleCommandPalette),
                ResolveGesture("Terminal.ToggleCommandPalette") ??
                new KeyGesture(Key.P, KeyModifiers.Meta | KeyModifiers.Shift)));

            var focusMode = CreateItem(
                "Focus Mode",
                () => Dispatch(ShortcutAction.ToggleFocusMode),
                ResolveGesture("Terminal.ToggleFocusMode"));
            focusMode.ToggleType = MenuItemToggleType.CheckBox;
            focusMode.IsChecked = host.IsFocusMode;
            _viewMenu.Items.Add(focusMode);

            _viewMenu.Items.Add(CreateItem(
                host.IsFullScreen ? "Exit Full Screen" : "Enter Full Screen",
                () => Dispatch(ShortcutAction.ToggleFullscreen),
                ResolveGesture("Terminal.ToggleFullscreen") ??
                new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control)));

            _viewMenu.Items.Add(new NativeMenuItemSeparator());

            var bigger = CreateItem(
                "Bigger",
                () =>
                {
                    if (!host.HasActiveTerminal && TryInvokeWorkbenchAction("ZoomIn")) return;
                    Dispatch(ShortcutAction.AdjustFontSize, new AdjustFontSizeArgs(1));
                },
                ResolveGesture("Terminal.IncreaseFontSize") ?? new KeyGesture(Key.OemPlus, KeyModifiers.Meta));
            bigger.IsEnabled = host.HasActiveTerminal || host.HostedWorkbenchMenu is not null;
            _viewMenu.Items.Add(bigger);

            var smaller = CreateItem(
                "Smaller",
                () =>
                {
                    if (!host.HasActiveTerminal && TryInvokeWorkbenchAction("ZoomOut")) return;
                    Dispatch(ShortcutAction.AdjustFontSize, new AdjustFontSizeArgs(-1));
                },
                ResolveGesture("Terminal.DecreaseFontSize") ?? new KeyGesture(Key.OemMinus, KeyModifiers.Meta));
            smaller.IsEnabled = host.HasActiveTerminal || host.HostedWorkbenchMenu is not null;
            _viewMenu.Items.Add(smaller);

            var resetSize = CreateItem(
                "Default Size",
                () => Dispatch(ShortcutAction.ResetFontSize),
                ResolveGesture("Terminal.ResetFontSize", Key.D0));
            resetSize.IsEnabled = host.HasActiveTerminal;
            _viewMenu.Items.Add(resetSize);

            _viewMenu.Items.Add(new NativeMenuItemSeparator());

            var zoomPane = CreateItem(
                "Zoom Pane",
                () => Dispatch(ShortcutAction.TogglePaneZoom),
                ResolveGesture("Terminal.TogglePaneZoom"));
            zoomPane.ToggleType = MenuItemToggleType.CheckBox;
            zoomPane.IsChecked = host.IsPaneZoomed;
            zoomPane.IsEnabled = host.ActivePaneCount > 1 || host.IsPaneZoomed;
            _viewMenu.Items.Add(zoomPane);

            var broadcast = CreateItem(
                "Broadcast Input to All Panes",
                () => Dispatch(ShortcutAction.ToggleBroadcastInput),
                ResolveGesture("Terminal.ToggleBroadcastInput"));
            broadcast.ToggleType = MenuItemToggleType.CheckBox;
            broadcast.IsChecked = host.IsBroadcastInput;
            broadcast.IsEnabled = host.ActivePaneCount > 1;
            _viewMenu.Items.Add(broadcast);

            var readOnly = CreateItem(
                "Read-Only Pane",
                () => Dispatch(ShortcutAction.TogglePaneReadOnly),
                ResolveGesture("Terminal.ToggleReadOnlyMode"));
            readOnly.ToggleType = MenuItemToggleType.CheckBox;
            readOnly.IsChecked = host.IsPaneReadOnly;
            readOnly.IsEnabled = host.HasActiveTerminal;
            _viewMenu.Items.Add(readOnly);

            if (host.HostedWorkbenchMenu is { } workbenchMenu &&
                workbenchMenu.Items.OfType<MenuItem>().ElementAtOrDefault(2) is { } iseViewMenu)
            {
                var iseViewItems = BuildMirroredWorkbenchItems(
                    iseViewMenu,
                    excludeTags: ["ZoomIn", "ZoomOut"],
                    remapMacGestures: false);
                if (iseViewItems.Count > 0)
                {
                    _viewMenu.Items.Add(new NativeMenuItemSeparator());
                    foreach (var item in iseViewItems)
                    {
                        _viewMenu.Items.Add(item);
                    }
                }
            }
        }

        private void RefreshWorkbenchCategory(NativeMenu target, int index)
        {
            RefreshTopLevelVisibility();
            target.Items.Clear();
            if (host.HostedWorkbenchMenu is not { } workbenchMenu ||
                workbenchMenu.Items.OfType<MenuItem>().ElementAtOrDefault(index) is not { } category)
            {
                return;
            }

            RaiseSubmenuOpened(category);
            foreach (var item in BuildMirroredWorkbenchItems(category, excludeTags: [], remapMacGestures: false))
            {
                target.Items.Add(item);
            }
        }

        private void RefreshWindowMenu()
        {
            _windowMenu.Items.Clear();
            var canWindowResize = !host.IsFullScreen;

            var minimize = CreateItem(
                "Minimize",
                () =>
                {
                    if (host.Window is { } window) window.WindowState = WindowState.Minimized;
                },
                new KeyGesture(Key.M, KeyModifiers.Meta));
            minimize.IsEnabled = canWindowResize;
            _windowMenu.Items.Add(minimize);

            var zoom = CreateItem(
                "Zoom",
                () => ToggleWindowZoom(host.Window));
            zoom.IsEnabled = canWindowResize;
            _windowMenu.Items.Add(zoom);

            _windowMenu.Items.Add(new NativeMenuItemSeparator());

            var prevTab = CreateItem(
                "Show Previous Tab",
                () => Dispatch(ShortcutAction.PrevTab),
                ResolveGesture("Terminal.PrevTab") ??
                new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta | KeyModifiers.Shift));
            prevTab.IsEnabled = host.TabCount > 1;
            _windowMenu.Items.Add(prevTab);

            var nextTab = CreateItem(
                "Show Next Tab",
                () => Dispatch(ShortcutAction.NextTab),
                ResolveGesture("Terminal.NextTab") ??
                new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Meta | KeyModifiers.Shift));
            nextTab.IsEnabled = host.TabCount > 1;
            _windowMenu.Items.Add(nextTab);

            var moveLeft = CreateItem(
                "Move Tab Left",
                () => Dispatch(ShortcutAction.MoveTab, new MoveTabArgs(string.Empty, MoveTabDirection.Backward)),
                ResolveGesture("Terminal.MoveTabBackward"));
            moveLeft.IsEnabled = host.ActiveTabIndex > 0;
            _windowMenu.Items.Add(moveLeft);

            var moveRight = CreateItem(
                "Move Tab Right",
                () => Dispatch(ShortcutAction.MoveTab, new MoveTabArgs(string.Empty, MoveTabDirection.Forward)),
                ResolveGesture("Terminal.MoveTabForward"));
            moveRight.IsEnabled = host.ActiveTabIndex >= 0 && host.ActiveTabIndex < host.TabCount - 1;
            _windowMenu.Items.Add(moveRight);

            _windowMenu.Items.Add(new NativeMenuItemSeparator());

            var hasPanes = host.ActivePaneCount > 1;
            var selectPane = CreateSubmenu(
                "Select Pane",
                CreateEnabledActionItem(
                    "Select Pane Above",
                    hasPanes,
                    () => Dispatch(ShortcutAction.MoveFocus, new MoveFocusArgs(FocusDirection.Up)),
                    ResolveGesture("Terminal.MoveFocusUp")),
                CreateEnabledActionItem(
                    "Select Pane Below",
                    hasPanes,
                    () => Dispatch(ShortcutAction.MoveFocus, new MoveFocusArgs(FocusDirection.Down)),
                    ResolveGesture("Terminal.MoveFocusDown")),
                CreateEnabledActionItem(
                    "Select Pane Left",
                    hasPanes,
                    () => Dispatch(ShortcutAction.MoveFocus, new MoveFocusArgs(FocusDirection.Left)),
                    ResolveGesture("Terminal.MoveFocusLeft")),
                CreateEnabledActionItem(
                    "Select Pane Right",
                    hasPanes,
                    () => Dispatch(ShortcutAction.MoveFocus, new MoveFocusArgs(FocusDirection.Right)),
                    ResolveGesture("Terminal.MoveFocusRight")),
                new NativeMenuItemSeparator(),
                CreateEnabledActionItem(
                    "Toggle Split Orientation",
                    hasPanes,
                    () => Dispatch(ShortcutAction.ToggleSplitOrientation),
                    ResolveGesture("Terminal.ToggleSplitOrientation")));
            selectPane.IsEnabled = hasPanes;
            _windowMenu.Items.Add(selectPane);

            _windowMenu.Items.Add(new NativeMenuItemSeparator());
            _windowMenu.Items.Add(CreateItem(
                "Rename Window…",
                () => Dispatch(ShortcutAction.OpenWindowRenamer),
                ResolveGesture("Terminal.OpenWindowRenamer")));
            _windowMenu.Items.Add(CreateItem(
                "Workspaces…",
                () => Dispatch(ShortcutAction.Workspaces),
                ResolveGesture("Terminal.Workspaces")));

            _windowMenu.Items.Add(new NativeMenuItemSeparator());
            _windowMenu.Items.Add(CreateItem(
                "Bring All to Front",
                () => BringAllWindowsToFront(host.Window)));

            var tabs = host.GetTabEntries();
            if (tabs.Count > 0)
            {
                _windowMenu.Items.Add(new NativeMenuItemSeparator());
                for (var index = 0; index < tabs.Count; index++)
                {
                    var tab = tabs[index];
                    var gesture = index < 8
                        ? ResolveGesture($"Terminal.SwitchToTab{index}")
                        : index == tabs.Count - 1
                            ? ResolveGesture("Terminal.SwitchToLastTab")
                            : null;
                    var item = CreateItem(tab.Title, tab.Activate, gesture);
                    item.ToggleType = MenuItemToggleType.CheckBox;
                    item.IsChecked = tab.IsActive;
                    _windowMenu.Items.Add(item);
                }
            }
        }

        private void RefreshHelpMenu()
        {
            _helpMenu.Items.Clear();

            if (host.HostedWorkbenchMenu is { } workbenchMenu &&
                workbenchMenu.Items.OfType<MenuItem>().ElementAtOrDefault(6) is { } iseHelpMenu)
            {
                var iseHelpItems = BuildMirroredWorkbenchItems(
                    iseHelpMenu,
                    excludeTags: ["CheckForUpdates"],
                    remapMacGestures: false);
                if (iseHelpItems.Count > 0)
                {
                    foreach (var item in iseHelpItems)
                    {
                        _helpMenu.Items.Add(item);
                    }

                    _helpMenu.Items.Add(new NativeMenuItemSeparator());
                }
            }

            _helpMenu.Items.Add(CreateItem(
                "Devolutions Terminal Documentation",
                () => host.OpenHelpUrl("https://github.com/Devolutions/devolutions-terminal")));
            _helpMenu.Items.Add(CreateItem(
                "Report an Issue…",
                () => host.OpenHelpUrl("https://github.com/Devolutions/devolutions-terminal/issues")));
        }

        private bool TryInvokeWorkbenchAction(string tag)
        {
            if (host.HostedWorkbenchMenu is not { } workbenchMenu)
            {
                return false;
            }

            foreach (var top in workbenchMenu.Items.OfType<MenuItem>())
            {
                RaiseSubmenuOpened(top);
                foreach (var child in top.Items.OfType<MenuItem>())
                {
                    if (Equals(child.Tag, tag) && child.IsEnabled)
                    {
                        child.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<NativeMenuItemBase> BuildMirroredWorkbenchItems(
            MenuItem parent,
            IReadOnlyCollection<string> excludeTags,
            bool remapMacGestures)
        {
            RaiseSubmenuOpened(parent);
            var list = new List<NativeMenuItemBase>();
            foreach (var raw in (IEnumerable?)parent.ItemsSource ?? parent.Items)
            {
                switch (raw)
                {
                    case Separator:
                        if (list.Count > 0 && list[^1] is not NativeMenuItemSeparator)
                        {
                            list.Add(new NativeMenuItemSeparator());
                        }
                        break;

                    case MenuItem child:
                        if (!child.IsVisible)
                        {
                            break;
                        }

                        var tag = child.Tag as string;
                        if (tag is not null && excludeTags.Contains(tag))
                        {
                            break;
                        }

                        var header = StripAccessKey(child.Header?.ToString());
                        if (header == "-")
                        {
                            if (list.Count > 0 && list[^1] is not NativeMenuItemSeparator)
                            {
                                list.Add(new NativeMenuItemSeparator());
                            }
                            break;
                        }

                        var subItems = BuildMirroredWorkbenchItems(child, excludeTags, remapMacGestures);
                        var hasChildren = ((IEnumerable?)child.ItemsSource ?? child.Items).Cast<object>().Any();
                        if (hasChildren)
                        {
                            var sub = new NativeMenu();
                            foreach (var subItem in subItems)
                            {
                                sub.Items.Add(subItem);
                            }

                            list.Add(new NativeMenuItem(header)
                            {
                                Menu = sub,
                                IsEnabled = child.IsEnabled && sub.Items.Count > 0,
                            });
                            break;
                        }

                        var captured = child;
                        var gesture = ResolveWorkbenchGesture(child, tag, remapMacGestures);
                        var nativeItem = CreateItem(
                            header,
                            () => captured.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)),
                            gesture);
                        nativeItem.IsEnabled = child.IsEnabled;
                        if (child.ToggleType != MenuItemToggleType.None)
                        {
                            nativeItem.ToggleType = child.ToggleType;
                            nativeItem.IsChecked = child.IsChecked;
                        }

                        list.Add(nativeItem);
                        break;
                }
            }

            while (list.Count > 0 && list[^1] is NativeMenuItemSeparator)
            {
                list.RemoveAt(list.Count - 1);
            }

            return list;
        }

        private static KeyGesture? ResolveWorkbenchGesture(
            MenuItem item,
            string? tag,
            bool remapMacGestures)
        {
            if (remapMacGestures)
            {
                return tag switch
                {
                    "Open" => new KeyGesture(Key.O, KeyModifiers.Meta),
                    "Save" => new KeyGesture(Key.S, KeyModifiers.Meta),
                    "SaveAs" => new KeyGesture(Key.S, KeyModifiers.Meta | KeyModifiers.Shift),
                    "GoToLine" => new KeyGesture(Key.L, KeyModifiers.Control),
                    // Avoid colliding with DT's Cmd+N / Cmd+T / Cmd+W / Cmd+P / Cmd+F shortcuts.
                    "New" or "Close" or "Print" => null,
                    _ => item.InputGesture is { KeyModifiers: var mods } g && !mods.HasFlag(KeyModifiers.Meta)
                        ? g
                        : null,
                };
            }

            if (item.InputGesture is not { } gesture)
            {
                return null;
            }

            // Function-key and explicit non-colliding shortcuts remain intact.
            if (gesture.Key is >= Key.F1 and <= Key.F24)
            {
                return gesture;
            }

            return null;
        }

        private static void RaiseSubmenuOpened(MenuItem item) =>
            item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));

        private void Dispatch(ShortcutAction action, IActionArgs? args = null) =>
            _ = host.DispatchAsync(new ActionAndArgs(action, args));

        private KeyGesture? ResolveGesture(string actionId, Key? fallbackMetaKey = null) =>
            AvaloniaKeyChord.GetActionGesture(host.Settings.ActionMap, actionId) ??
            (fallbackMetaKey is { } key ? new KeyGesture(key, KeyModifiers.Meta) : null);
    }

    private static bool HasFocusedTextEditor(Window? window)
    {
        if (window?.FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        foreach (var current in focused.GetVisualAncestors().Prepend(focused))
        {
            if (current is TextBox)
            {
                return true;
            }
#if POWERSHELL_ISE
            if (current is AvaloniaEdit.TextEditor)
            {
                return true;
            }
#endif
        }

        return false;
    }

    private static bool CanExecuteFocusedEdit(Window? window, EditCommand command)
    {
        if (window?.FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        foreach (var current in focused.GetVisualAncestors().Prepend(focused))
        {
            if (current is TextBox box)
            {
                var hasSelection = box.SelectionStart != box.SelectionEnd;
                var canWrite = !box.IsReadOnly && box.IsEffectivelyEnabled;
                var canCopySecret = box.PasswordChar == '\0' || box.RevealPassword;
                return command switch
                {
                    EditCommand.Undo => canWrite && box.CanUndo,
                    EditCommand.Redo => canWrite && box.CanRedo,
                    EditCommand.Cut => canWrite && hasSelection && canCopySecret,
                    EditCommand.Copy => hasSelection && canCopySecret,
                    EditCommand.Paste => canWrite,
                    EditCommand.SelectAll => !string.IsNullOrEmpty(box.Text),
                    _ => false,
                };
            }

#if POWERSHELL_ISE
            if (current is AvaloniaEdit.TextEditor { Document: { } doc } editor)
            {
                return command switch
                {
                    EditCommand.Undo => !editor.IsReadOnly && doc.UndoStack.CanUndo,
                    EditCommand.Redo => !editor.IsReadOnly && doc.UndoStack.CanRedo,
                    EditCommand.Cut => !editor.IsReadOnly && editor.SelectionLength > 0,
                    EditCommand.Copy => editor.SelectionLength > 0 || (editor.Options.CutCopyWholeLine && doc.TextLength > 0),
                    EditCommand.Paste => !editor.IsReadOnly && editor.TextArea.ReadOnlySectionProvider.CanInsert(editor.CaretOffset),
                    EditCommand.SelectAll => doc.TextLength > 0,
                    _ => false,
                };
            }
#endif
        }

        return false;
    }

    private static bool TryExecuteFocusedEdit(Window? window, EditCommand command)
    {
        if (window?.FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        foreach (var current in focused.GetVisualAncestors().Prepend(focused))
        {
            if (current is TextBox box)
            {
                switch (command)
                {
                    case EditCommand.Undo when !box.IsReadOnly && box.CanUndo:
                        box.Undo();
                        return true;
                    case EditCommand.Redo when !box.IsReadOnly && box.CanRedo:
                        box.Redo();
                        return true;
                    case EditCommand.Cut when !box.IsReadOnly && box.SelectionStart != box.SelectionEnd:
                        box.Cut();
                        return true;
                    case EditCommand.Copy when box.SelectionStart != box.SelectionEnd:
                        box.Copy();
                        return true;
                    case EditCommand.Paste when !box.IsReadOnly && box.IsEffectivelyEnabled:
                        box.Paste();
                        return true;
                    case EditCommand.SelectAll when !string.IsNullOrEmpty(box.Text):
                        box.SelectAll();
                        return true;
                }

                return true;
            }

#if POWERSHELL_ISE
            if (current is AvaloniaEdit.TextEditor { Document: { } } editor)
            {
                switch (command)
                {
                    case EditCommand.Undo when !editor.IsReadOnly:
                        editor.Undo();
                        return true;
                    case EditCommand.Redo when !editor.IsReadOnly:
                        editor.Redo();
                        return true;
                    case EditCommand.Cut when !editor.IsReadOnly:
                        editor.Cut();
                        return true;
                    case EditCommand.Copy:
                        editor.Copy();
                        return true;
                    case EditCommand.Paste when !editor.IsReadOnly:
                        editor.Paste();
                        return true;
                    case EditCommand.SelectAll:
                        editor.SelectAll();
                        return true;
                }

                return true;
            }
#endif
        }

        return false;
    }

    public static string StripAccessKey(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return string.Empty;
        }

        if (!header.Contains('_', StringComparison.Ordinal))
        {
            return header;
        }

        Span<char> buffer = header.Length <= 128
            ? stackalloc char[header.Length]
            : new char[header.Length];
        var length = 0;
        for (var index = 0; index < header.Length; index++)
        {
            if (header[index] == '_')
            {
                if (index + 1 < header.Length && header[index + 1] == '_')
                {
                    buffer[length++] = '_';
                    index++;
                }

                continue;
            }

            buffer[length++] = header[index];
        }

        return new string(buffer[..length]);
    }

    private static void ToggleWindowZoom(Window? window)
    {
        if (window is null || window.WindowState == WindowState.FullScreen)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static void BringAllWindowsToFront(Window? activeWindow)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            activeWindow?.Activate();
            return;
        }

        var current = activeWindow ?? desktop.Windows.LastOrDefault(static w => w.IsActive);
        foreach (var window in desktop.Windows)
        {
            if (!ReferenceEquals(window, current) &&
                window.IsVisible &&
                window.WindowState != WindowState.Minimized)
            {
                window.Activate();
            }
        }

        current?.Activate();
    }

    private static void AttachRefresh(NativeMenu menu, Action refresh) =>
        menu.NeedsUpdate += (_, _) => refresh();

    private static NativeMenuItem CreateSubmenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        return new NativeMenuItem(header) { Menu = menu };
    }

    private static NativeMenuItem CreateEnabledActionItem(
        string header,
        bool isEnabled,
        Action action,
        KeyGesture? gesture = null)
    {
        var item = CreateItem(header, action, gesture);
        item.IsEnabled = isEnabled;
        return item;
    }

    private static NativeMenuItem CreateActionItem(
        string header,
        ShortcutAction action,
        Func<ShortcutAction, Task> dispatch,
        Key key,
        KeyModifiers modifiers = KeyModifiers.Meta) =>
        CreateItem(header, () => _ = dispatch(action), new KeyGesture(key, modifiers));

    private static NativeMenuItem CreateItem(string header, Action action, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }
}
