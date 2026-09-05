using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Devolutions.Terminal.UI.Tests.TestApplication))]

namespace Devolutions.Terminal.UI.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .AfterSetup(static builder =>
                builder.Instance?.Styles.Add(new FluentTheme()))
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
