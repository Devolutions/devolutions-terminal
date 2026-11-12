using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Devolutions.Terminal.App.Platform;

namespace Devolutions.Terminal.App.Views;

public sealed class AboutWindow : Window
{
    private static AboutWindow? _current;

    public AboutWindow(Action<string>? openUri = null)
    {
        var open = openUri ?? DefaultOpenUri;
        Title = "About Devolutions Terminal";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMaximize = false;
        CanMinimize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#FF262628"));

        var stack = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(28, 24, 28, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var iconBitmap = TryLoadAppIcon();
        if (iconBitmap is not null)
        {
            Icon = new WindowIcon(iconBitmap);
            stack.Children.Add(new Image
            {
                Source = iconBitmap,
                Width = 72,
                Height = 72,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = "Devolutions Terminal",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"Version {ResolveVersionText()}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#AEAEB2")),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Cross-platform terminal built with .NET NativeAOT, Avalonia, Skia, and HarfBuzz.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#D1D1D6")),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 6),
        });

        var links = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            ItemSpacing = 12,
            LineSpacing = 4,
        };
        links.Children.Add(CreateLink("Source Code", "https://github.com/Devolutions/devolutions-terminal", open));
        links.Children.Add(CreateLink("Documentation", "https://github.com/Devolutions/devolutions-terminal/tree/master/docs", open));
        links.Children.Add(CreateLink("Release Notes", "https://github.com/Devolutions/devolutions-terminal/releases", open));
        links.Children.Add(CreateLink("Third-Party Notices", "https://github.com/Devolutions/devolutions-terminal/blob/master/NOTICE.md", open));
        links.Children.Add(CreateLink("Report an Issue", "https://github.com/Devolutions/devolutions-terminal/issues", open));
        stack.Children.Add(links);

        stack.Children.Add(new TextBlock
        {
            Text = "© Devolutions Inc. All rights reserved.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8E8E93")),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });

        Content = stack;
        AutomationProperties.SetName(this, "About Devolutions Terminal");
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            iconBitmap?.Dispose();
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
    }

    public static AboutWindow ShowOrActivate(Window? owner = null, Action<string>? openUri = null)
    {
        if (_current is { } existing)
        {
            existing.Show();
            existing.Activate();
            return existing;
        }

        var window = new AboutWindow(openUri);
        _current = window;
        if (owner is { IsVisible: true })
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        window.Activate();
        return window;
    }

    internal static string ResolveVersionText()
    {
        var assembly = typeof(AboutWindow).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plusIndex = info.IndexOf('+', StringComparison.Ordinal);
            if (plusIndex > 0)
            {
                var commit = info[(plusIndex + 1)..];
                var shortCommit = commit.Length > 7 ? commit[..7] : commit;
                return $"{info[..plusIndex]} ({shortCommit})";
            }

            return info;
        }

        return assembly.GetName().Version?.ToString(3) ?? "Development build";
    }

    private static Bitmap? TryLoadAppIcon()
    {
        var uri = new Uri("avares://Devolutions.Terminal.App/Assets/ProfileIcons/terminal.png");
        if (!AssetLoader.Exists(uri))
        {
            return null;
        }

        using var stream = AssetLoader.Open(uri);
        return new Bitmap(stream);
    }

    private static Button CreateLink(string label, string url, Action<string> open)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(2, 1),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#64B5F6")),
            FontSize = 12,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => open(url);
        return button;
    }

    private static void DefaultOpenUri(string url)
    {
        try
        {
            new PlatformLauncher().Open(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Unable to open '{url}': {ex.Message}");
        }
    }
}
