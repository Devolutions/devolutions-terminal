using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.Browser;

public sealed class BrowserTerminalView : UserControl
{
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#1B1B1B"));
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#0C0C0C"));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#2D2D2D"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#F2F2F2"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#A0A0A0"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#0078D4"));

    private readonly TextBlock _title = new()
    {
        Text = "Devolutions Terminal",
        Foreground = new SolidColorBrush(Color.Parse("#F2F2F2")),
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 13,
        Margin = new Avalonia.Thickness(12, 0, 8, 0),
    };
    private readonly StackPanel _tabStrip = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly WrapPanel _profileMenu = new();
    private readonly Border _profilePanel;
    private readonly Grid _terminals = new();
    private readonly Button _addButton;
    private readonly List<HostTab> _tabs = [];
    private HostTab? _active;
    private HostControlClient? _control;
    private Uri? _httpBase;
    private bool _started;

    public BrowserTerminalView()
    {
        _addButton = new Button
        {
            Content = "+",
            FontSize = 16,
            Width = 32,
            Height = 28,
            Margin = new Avalonia.Thickness(8, 0),
            Padding = new Avalonia.Thickness(0),
            Background = AccentBrush,
            Foreground = Brushes.White,
            BorderThickness = new Avalonia.Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        _profilePanel = new Border
        {
            Background = PanelBrush,
            Padding = new Avalonia.Thickness(8, 6),
            IsVisible = false,
            Child = new ScrollViewer
            {
                MaxHeight = 168,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _profileMenu,
            },
        };
        _addButton.Click += (_, _) => _profilePanel.IsVisible = !_profilePanel.IsVisible;
        Content = BuildLayout();
        AttachedToVisualTree += (_, _) => _ = StartSessionAsync();
    }

    public bool IsSessionReady => _active?.Ready == true;

    public int TabCount => _tabs.Count;

    public string Title => _title.Text ?? "";

    public string GetScreenText()
    {
        var term = _active?.Term;
        return term is null
            ? string.Empty
            : TerminalBufferExport.ToPlainText(term.Engine.CreateSnapshot(includeHistory: true).Buffer);
    }

    public void SendInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && _active?.Connection is { IsRunning: true } connection)
        {
            connection.Write(text);
        }
    }

    private Control BuildLayout()
    {
        var tabs = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tabStrip,
        };
        DockPanel.SetDock(_title, Dock.Left);
        DockPanel.SetDock(_addButton, Dock.Right);
        var header = new DockPanel
        {
            Background = HeaderBrush,
            Height = 40,
            LastChildFill = true,
            Children = { _title, _addButton, tabs },
        };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_profilePanel, Dock.Top);
        return new DockPanel
        {
            Background = ActiveBrush,
            Children = { header, _profilePanel, _terminals },
        };
    }

    private async Task StartSessionAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            var httpBase = BrowserLaunchContext.TryGetHttpBase();
            if (httpBase is not null && await HostControlClient.IsAvailableAsync(httpBase).ConfigureAwait(true))
            {
                await StartControlAsync(httpBase).ConfigureAwait(true);
                return;
            }

            var useHost = httpBase is not null &&
                await HostPtyWebSocketConnection.IsBridgeAvailableAsync(httpBase).ConfigureAwait(true);
            await StartFallbackAsync(useHost, httpBase).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task StartControlAsync(Uri httpBase)
    {
        _httpBase = httpBase;
        _control = await HostControlClient.ConnectAsync(httpBase).ConfigureAwait(true);
        var catalog = await _control.ListProfilesAsync().ConfigureAwait(true);
        var profiles = catalog.Profiles ?? [];
        await RunOnUiAsync(() =>
        {
            BuildProfileMenu(profiles);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
        var initial = profiles.FirstOrDefault(profile =>
                profile.Launchable &&
                string.Equals(profile.Id, catalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(profile => profile.Launchable);
        if (initial is null)
        {
            await RunOnUiAsync(() =>
            {
                _title.Text = "No launchable profiles";
                _addButton.IsVisible = true;
                _profilePanel.IsVisible = true;
                return Task.CompletedTask;
            }).ConfigureAwait(true);
            return;
        }

        await LaunchProfileAsync(initial).ConfigureAwait(true);
    }

    private void BuildProfileMenu(IReadOnlyList<HostProfileInfo> profiles)
    {
        _addButton.IsVisible = true;
        _profileMenu.Children.Clear();
        var duplicated = profiles
            .GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            var label = duplicated.Contains(profile.Name) && !string.IsNullOrWhiteSpace(profile.Source)
                ? $"{profile.Name} ({profile.Source})"
                : profile.Name;
            var button = new Button
            {
                Content = label,
                Margin = new Avalonia.Thickness(0, 0, 6, 6),
                Padding = new Avalonia.Thickness(10, 4),
                Background = profile.Launchable ? IdleBrush : HeaderBrush,
                Foreground = profile.Launchable ? TextBrush : MutedBrush,
                BorderThickness = new Avalonia.Thickness(0),
                IsEnabled = profile.Launchable,
            };
            var tip = profile.Launchable ? null : profile.Reason ?? "Not launchable";
            if (!string.IsNullOrWhiteSpace(tip))
            {
                ToolTip.SetTip(button, tip);
            }

            var captured = profile;
            button.Click += (_, _) => _ = LaunchProfileAsync(captured);
            _profileMenu.Children.Add(button);
        }
    }

    private async Task LaunchProfileAsync(HostProfileInfo profile)
    {
        if (_control is null || _httpBase is null)
        {
            return;
        }

        try
        {
            var launched = await _control.LaunchAsync(profile.Id, 80, 24).ConfigureAwait(true);
            await RunOnUiAsync(async () =>
            {
                _profilePanel.IsVisible = false;
                await OpenTabAsync(profile, launched).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _title.Text = ex.Message);
        }
    }

    private async Task OpenTabAsync(HostProfileInfo profile, HostControlMessage launched)
    {
        var connection = new HostPtyWebSocketConnection(_httpBase!, launched.Transport);
        var term = new TermControl
        {
            ConnectionFactory = _ => connection,
            AccessibleName = profile.Name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var tab = CreateTab(profile, launched.SessionId ?? "", connection, term, showClose: true);
        _tabs.Add(tab);
        _terminals.Children.Add(term);
        _tabStrip.Children.Add(tab.Chrome);
        await ActivateAsync(tab).ConfigureAwait(true);
    }

    private async Task ActivateAsync(HostTab tab)
    {
        _active = tab;
        foreach (var other in _tabs)
        {
            var active = other == tab;
            other.Term.IsVisible = active;
            other.Header.Background = active ? ActiveBrush : IdleBrush;
            other.Header.BorderBrush = active ? AccentBrush : Brushes.Transparent;
        }

        _title.Text = tab.Profile.Name;
        if (!tab.Started)
        {
            tab.Started = true;
            await tab.Term.StartAsync(new ProfileSettings
            {
                Guid = tab.Profile.Id,
                Name = tab.Profile.Name,
                Commandline = "host-pty",
                ColorScheme = string.IsNullOrWhiteSpace(tab.Profile.ColorScheme) ? "Campbell" : tab.Profile.ColorScheme,
            }, 80, 24).ConfigureAwait(true);
            tab.Ready = true;
        }

        tab.Term.Focus();
    }

    private async Task CloseTabAsync(HostTab tab)
    {
        _tabs.Remove(tab);
        _terminals.Children.Remove(tab.Term);
        _tabStrip.Children.Remove(tab.Chrome);
        try
        {
            await tab.Connection.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
        }

        if (_control is not null && tab.SessionId.Length > 0)
        {
            try
            {
                await _control.CloseSessionAsync(tab.SessionId).ConfigureAwait(true);
            }
            catch (HostControlException)
            {
            }
        }

        if (_active != tab)
        {
            return;
        }

        var next = _tabs.LastOrDefault();
        if (next is null)
        {
            _active = null;
            _title.Text = "Devolutions Terminal";
            return;
        }

        await ActivateAsync(next).ConfigureAwait(true);
    }

    private async Task StartFallbackAsync(bool useHost, Uri? httpBase)
    {
        IRestartableTerminalConnection connection;
        string title;
        if (useHost && httpBase is not null)
        {
            connection = new HostPtyWebSocketConnection(httpBase);
            title = "host shell";
        }
        else
        {
            connection = new BrowserShellConnection();
            title = "dt-wasm";
        }

        var term = new TermControl
        {
            ConnectionFactory = _ => connection,
            AccessibleName = "Devolutions Terminal",
        };
        var tab = CreateTab(
            new HostProfileInfo { Id = "fallback", Name = title, Launchable = true },
            "",
            connection,
            term,
            showClose: false);
        _tabs.Add(tab);
        _terminals.Children.Add(term);
        _tabStrip.Children.Add(tab.Chrome);
        _active = tab;
        _title.Text = title;
        await term.StartAsync(ProfileSettings.CreateBrowserShell(), 80, 24).ConfigureAwait(true);
        tab.Started = true;
        tab.Ready = true;
        term.Focus();
    }

    private HostTab CreateTab(
        HostProfileInfo profile,
        string sessionId,
        IRestartableTerminalConnection connection,
        TermControl term,
        bool showClose)
    {
        var header = new Button
        {
            Content = new TextBlock
            {
                Text = profile.Name,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            Background = IdleBrush,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 2),
            Padding = new Avalonia.Thickness(10, 4),
        };
        var close = new Button
        {
            Content = "x",
            Width = 22,
            Height = 22,
            Margin = new Avalonia.Thickness(2, 0, 0, 0),
            Padding = new Avalonia.Thickness(0),
            Background = Brushes.Transparent,
            Foreground = MutedBrush,
            BorderThickness = new Avalonia.Thickness(0),
            IsVisible = showClose,
        };
        var chrome = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { header, close },
        };
        var tab = new HostTab(profile, sessionId, connection, term, chrome, header, close);
        header.Click += (_, _) => _ = ActivateAsync(tab);
        close.Click += (_, _) => _ = CloseTabAsync(tab);
        return tab;
    }

    private static Task RunOnUiAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        return Dispatcher.UIThread.InvokeAsync(action);
    }

    private void ShowError(Exception ex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Content = new TextBlock
            {
                Text = ex.ToString(),
                Foreground = Brushes.OrangeRed,
                Margin = new Avalonia.Thickness(16),
                TextWrapping = TextWrapping.Wrap,
            };
        });
    }

    private sealed class HostTab
    {
        public HostTab(
            HostProfileInfo profile,
            string sessionId,
            IRestartableTerminalConnection connection,
            TermControl term,
            Control chrome,
            Button header,
            Button close)
        {
            Profile = profile;
            SessionId = sessionId;
            Connection = connection;
            Term = term;
            Chrome = chrome;
            Header = header;
            Close = close;
        }

        public HostProfileInfo Profile { get; }
        public string SessionId { get; }
        public IRestartableTerminalConnection Connection { get; }
        public TermControl Term { get; }
        public Control Chrome { get; }
        public Button Header { get; }
        public Button Close { get; }
        public bool Started { get; set; }
        public bool Ready { get; set; }
    }
}
