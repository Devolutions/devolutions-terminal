using Avalonia.Controls;
using Avalonia.Input;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Platform;

public static class MacOsNativeMenu
{
    public static NativeMenu CreateApplicationMenu(
        Action about,
        Action settings,
        Action hide,
        Action quit)
    {
        ArgumentNullException.ThrowIfNull(about);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hide);
        ArgumentNullException.ThrowIfNull(quit);

        var menu = new NativeMenu();
        menu.Items.Add(CreateItem("About Devolutions Terminal", about));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateItem(
            "Settings…",
            settings,
            new KeyGesture(Key.OemComma, KeyModifiers.Meta)));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateItem(
            "Hide Devolutions Terminal",
            hide,
            new KeyGesture(Key.H, KeyModifiers.Meta)));
        menu.Items.Add(CreateItem(
            "Quit Devolutions Terminal",
            quit,
            new KeyGesture(Key.Q, KeyModifiers.Meta)));
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
            CreateActionItem("Find", ShortcutAction.Find, dispatch, Key.F)));
        menu.Items.Add(CreateSubmenu(
            "View",
            CreateActionItem(
                "Toggle Full Screen",
                ShortcutAction.ToggleFullscreen,
                dispatch,
                Key.F,
                KeyModifiers.Meta | KeyModifiers.Control),
            CreateActionItem(
                "Command Palette",
                ShortcutAction.ToggleCommandPalette,
                dispatch,
                Key.P,
                KeyModifiers.Meta | KeyModifiers.Shift)));
        menu.Items.Add(CreateSubmenu(
            "Window",
            CreateActionItem("Next Tab", ShortcutAction.NextTab, dispatch, Key.Right, KeyModifiers.Meta | KeyModifiers.Shift),
            CreateActionItem("Previous Tab", ShortcutAction.PrevTab, dispatch, Key.Left, KeyModifiers.Meta | KeyModifiers.Shift)));
        return menu;
    }

    private static NativeMenuItem CreateSubmenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        return new NativeMenuItem(header) { Menu = menu };
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
