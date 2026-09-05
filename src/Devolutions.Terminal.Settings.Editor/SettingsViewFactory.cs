using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.Settings.Editor;

public static class SettingsViewFactory
{
    public static SettingsWindow CreateWindow() => new(new SettingsEditorViewModel());

    public static SettingsWindow CreateWindow(
        Func<AppSettings> load,
        Action<AppSettings> save,
        Func<AppSettings> createDefault,
        Func<string?>? getRevision = null) =>
        new(CreateViewModel(load, save, createDefault, getRevision));

    public static SettingsView CreateView(
        Func<AppSettings> load,
        Action<AppSettings> save,
        Func<AppSettings> createDefault,
        Func<string?>? getRevision = null) =>
        new(CreateViewModel(load, save, createDefault, getRevision));

    private static SettingsEditorViewModel CreateViewModel(
        Func<AppSettings> load,
        Action<AppSettings> save,
        Func<AppSettings> createDefault,
        Func<string?>? getRevision) =>
        new(load, save, createDefault, getRevision);
}
