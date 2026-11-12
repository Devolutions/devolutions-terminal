using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Devolutions.Terminal.Cli;
using Devolutions.Terminal.Settings;
using Devolutions.Terminal.App.Platform;
using Devolutions.Terminal.App.Views;

namespace Devolutions.Terminal;

public partial class TerminalApp : Application
{
    private readonly HashSet<Window> _trayWindows = [];
    private readonly Dictionary<Window, WindowState> _windowStates = [];
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TerminalWindowRouter? _router;
    private bool _alwaysShowNotificationIcon;

    internal static CliInvocation? InitialInvocation { get; set; }
    internal static DeferredBrokerHandler? BrokerHandler { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsMacOS())
        {
            // Attach before AppBuilder.AfterSetup runs SetupApplicationMenuExporter;
            // otherwise Avalonia falls back to its built-in "About Avalonia" menu.
            NativeMenu.SetMenu(
                this,
                MacOsNativeMenu.CreateApplicationMenu(
                    about: () =>
                    {
                        (_desktop as IActivatableLifetime)?.TryLeaveBackground();
                        var active = _desktop?.Windows.OfType<MainWindow>().LastOrDefault(static w => w.IsActive)
                                     ?? _desktop?.Windows.OfType<MainWindow>().LastOrDefault();
                        AboutWindow.ShowOrActivate(active);
                    },
                    settings: () => DispatchToActiveWindow(new ActionAndArgs(
                        ShortcutAction.OpenSettings,
                        new OpenSettingsArgs(SettingsTarget.SettingsUI)))));
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            WindowsSurfaceRecovery.Install();
            _router = new TerminalWindowRouter(
                desktop,
                ConfigureWindow,
                isolated: InitialInvocation?.Isolated ?? false);
            BrokerHandler?.SetHandler(_router);
            if (OperatingSystem.IsMacOS())
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += (_, e) =>
                {
                    if (desktop.Windows.OfType<MainWindow>().Any())
                    {
                        e.Cancel = true;
                        _ = MainWindow.RequestApplicationCloseAsync(desktop);
                    }
                };
                MacOsNativeMenu.InstallSecondaryWindowMenuHook();
                NativeDock.SetMenu(
                    this,
                    MacOsNativeMenu.CreateDockMenu(
                        getWindows: () => desktop.Windows
                            .OfType<MainWindow>()
                            .Select((window, index) => new MacOsDockWindowEntry(
                                !string.IsNullOrWhiteSpace(window.Title)
                                    ? window.Title
                                    : !string.IsNullOrWhiteSpace(window.WindowName)
                                        ? window.WindowName
                                        : $"Terminal {index + 1}",
                                window.IsActive,
                                () => ActivateWindowFromMenu(window)))
                            .ToArray(),
                        getProfiles: () =>
                        {
                            var activeSettings = (desktop.Windows.OfType<MainWindow>().FirstOrDefault() as IMacOsWindowMenuHost)?.Settings
                                ?? SettingsService.LoadWithDynamicProfiles(DynamicProfileManager.CreateDefault());
                            return activeSettings.Profiles
                                .Where(static profile => !profile.Hidden && !profile.Orphaned)
                                .ToArray();
                        },
                        newWindow: () => OpenNewWindowFromDock(profile: null),
                        newTab: () => DispatchToActiveWindow(new ActionAndArgs(ShortcutAction.NewTab)),
                        openProfileInNewWindow: OpenNewWindowFromDock));
            }

            desktop.MainWindow = _router.CreateInitial(
                InitialInvocation ?? new CliParser().Parse([]).Invocation!);
            if (desktop is IActivatableLifetime activatable)
            {
                activatable.Activated += OnActivated;
            }

            desktop.Exit += (_, _) =>
            {
                if (desktop is IActivatableLifetime lifetime)
                {
                    lifetime.Activated -= OnActivated;
                }

                _router?.Dispose();
                _router = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureWindow(Window window)
    {
        if (window is not MainWindow terminalWindow)
        {
            return;
        }

        _alwaysShowNotificationIcon |= terminalWindow.AlwaysShowNotificationIcon;
        _windowStates[window] = window.WindowState;
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property != Window.WindowStateProperty ||
                !terminalWindow.MinimizeToNotificationArea)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.ShowInTaskbar = false;
                _trayWindows.Add(window);
            }
            else
            {
                window.ShowInTaskbar = true;
                _trayWindows.Remove(window);
                _windowStates[window] = window.WindowState;
            }

            RefreshNotificationIcon();
        };
        window.Closed += (_, _) =>
        {
            _trayWindows.Remove(window);
            _windowStates.Remove(window);
            RefreshNotificationIcon();
        };
        RefreshNotificationIcon();
    }

    private void RefreshNotificationIcon()
    {
        var notificationIcon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        if (notificationIcon is not null)
        {
            notificationIcon.IsVisible = _alwaysShowNotificationIcon || _trayWindows.Count > 0;
        }
    }

    private void ShowWindows_OnClick(object? sender, EventArgs e)
    {
        if (_desktop is null)
        {
            return;
        }

        foreach (var window in _trayWindows.OfType<MainWindow>().ToArray())
        {
            window.ShowInTaskbar = true;
            window.WindowState = _windowStates.GetValueOrDefault(window, WindowState.Normal);
            window.Show();
            window.Activate();
            _trayWindows.Remove(window);
        }

        RefreshNotificationIcon();
    }

    private async void Exit_OnClick(object? sender, EventArgs e)
    {
        if (_desktop is not null) await MainWindow.RequestApplicationCloseAsync(_desktop);
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is ProtocolActivatedEventArgs protocol)
        {
            HandleProtocol(protocol.Uri);
            return;
        }

        if (e.Kind == ActivationKind.Reopen)
        {
            EnsureWindow();
        }
    }

    private void HandleProtocol(Uri uri)
    {
        if (!LinuxDesktopIntegration.TryNormalizeProtocolActivation(
                [uri.OriginalString],
                out _,
                out var error) ||
            error is not null)
        {
            return;
        }

        var action = string.IsNullOrWhiteSpace(uri.Host)
            ? uri.AbsolutePath.Trim('/')
            : uri.Host;
        if (action.Equals("new-tab", StringComparison.OrdinalIgnoreCase) ||
            _desktop?.Windows.Count == 0)
        {
            EnsureWindow();
            return;
        }

        var window = _desktop?.Windows.OfType<Window>().LastOrDefault();
        window?.Show();
        window?.Activate();
        (_desktop as IActivatableLifetime)?.TryLeaveBackground();
    }

    private void DispatchToActiveWindow(ActionAndArgs action)
    {
        var existing = _desktop?.Windows.OfType<MainWindow>().LastOrDefault(static item => item.IsActive)
            ?? _desktop?.Windows.OfType<MainWindow>().LastOrDefault();
        if (existing is not null)
        {
            ActivateWindowFromMenu(existing);
            existing.DispatchMenuAction(action);
            return;
        }

        var created = EnsureWindow();
        if (created is not null)
        {
            _ = DispatchAfterInitialActivationAsync(created, action);
        }
    }

    private static async Task DispatchAfterInitialActivationAsync(MainWindow window, ActionAndArgs action)
    {
        try
        {
            await window.InitialActivation.ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        window.DispatchMenuAction(action);
    }

    private void ActivateWindowFromMenu(MainWindow window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = _windowStates.GetValueOrDefault(window, WindowState.Normal);
        }

        window.ShowInTaskbar = true;
        window.Show();
        window.Activate();
        (_desktop as IActivatableLifetime)?.TryLeaveBackground();
    }

    private void OpenNewWindowFromDock(ProfileSettings? profile)
    {
        if (_router is null || _desktop is null)
        {
            return;
        }

        var window = _router.CreateNewWindow(profile);
        _desktop.MainWindow ??= window;
        window.Show();
        window.Activate();
        (_desktop as IActivatableLifetime)?.TryLeaveBackground();
    }

    private MainWindow? EnsureWindow()
    {
        if (_router is null || _desktop is null)
        {
            return null;
        }

        var existing = _desktop.Windows.OfType<MainWindow>().LastOrDefault();
        if (existing is not null)
        {
            existing.Show();
            existing.Activate();
            (_desktop as IActivatableLifetime)?.TryLeaveBackground();
            return existing;
        }

        var window = _router.CreateInitial(
            InitialInvocation ?? new CliParser().Parse([]).Invocation!);
        _desktop.MainWindow = window;
        window.Show();
        return window;
    }
}
