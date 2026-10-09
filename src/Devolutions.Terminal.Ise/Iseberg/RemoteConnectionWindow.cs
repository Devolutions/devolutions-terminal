using Iseberg.Core;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Iseberg;

public sealed class RemoteConnectionWindow : Window
{
    public RemoteConnectionWindow()
    {
        Title = UiText.Get("NewRemoteSession").Replace("_", "");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var transport = new ComboBox { Name = "RemoteTransport", ItemsSource = new[] { "SSH", "WSMan" }, SelectedIndex = 0 };
        var address = new TextBox { Name = "RemoteAddress" };
        var user = new TextBox { Name = "RemoteUser" };
        var password = new TextBox { Name = "RemotePassword", PasswordChar = '*' };
        var key = new TextBox { Name = "RemoteKey" };
        var port = new TextBox { Name = "RemotePort", Text = "22" };
        var subsystem = new TextBox { Name = "RemoteSubsystem", Text = "powershell" };
        var error = new TextBlock { Name = "RemoteConnectionError", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var body = new StackPanel { Margin = new(16), Spacing = 8 };
        void Field(string resource, Control input)
        {
            body.Children.Add(new TextBlock { Text = UiText.Get(resource) });
            Avalonia.Automation.AutomationProperties.SetName(input, UiText.Get(resource));
            body.Children.Add(input);
        }
        Field("RemoteTransport", transport);
        Field("RemoteAddress", address);
        Field("RemoteUser", user);
        Field("RemotePort", port);
        Field("RemoteKey", key);
        Field("RemoteSubsystem", subsystem);
        Field("RemotePassword", password);
        password.IsEnabled = false;
        transport.SelectionChanged += (_, _) =>
        {
            var ssh = transport.SelectedIndex == 0;
            port.IsEnabled = key.IsEnabled = subsystem.IsEnabled = ssh;
            password.IsEnabled = !ssh;
        };
        body.Children.Add(new TextBlock { Text = UiText.Get("RemoteConnectionHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(error);
        var connect = new Button { Content = UiText.Get("RemoteConnect"), IsDefault = true };
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true };
        connect.Click += (_, _) =>
        {
            try
            {
                var target = address.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException(UiText.Get("RemoteAddressRequired"));
                RemoteConnectionInfo connection;
                if (transport.SelectedIndex == 0)
                {
                    if (!int.TryParse(port.Text, out var number) || number is < 1 or > 65535)
                        throw new ArgumentException(UiText.Get("RemotePortInvalid"));
                    if (string.IsNullOrWhiteSpace(subsystem.Text)) throw new ArgumentException(UiText.Get("RemoteSubsystemRequired"));
                    connection = new(RemoteConnectionKind.Ssh, target, user.Text ?? "", number,
                        string.IsNullOrWhiteSpace(key.Text) ? null : key.Text, subsystem.Text!);
                }
                else
                {
                    if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        throw new ArgumentException(UiText.Get("RemoteUriInvalid"));
                    connection = new(RemoteConnectionKind.WSMan, target, user.Text,
                        ConnectionUri: uri.AbsoluteUri, ConfigurationName: "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                        Password: string.IsNullOrWhiteSpace(user.Text) ? null : password.Text);
                }
                password.Text = "";
                Close(connection);
            }
            catch (ArgumentException exception) { error.Text = exception.Message; }
        };
        cancel.Click += (_, _) => Close(null);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { connect, cancel }
        });
        Content = body;
        Dialogs.RegisterNames(this);
    }
}
