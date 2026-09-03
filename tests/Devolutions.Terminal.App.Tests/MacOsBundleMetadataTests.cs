using System.Xml.Linq;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class MacOsBundleMetadataTests
{
    private const string AppId = "com.devolutions.Terminal";
    private static readonly string MacOsAssets =
        Path.Combine(AppContext.BaseDirectory, "macos");

    [Fact]
    public void InfoPlistDeclaresBundleIdentityUrlSchemeAndHighDpi()
    {
        var document = XDocument.Load(Path.Combine(MacOsAssets, "Info.plist"));
        var dict = Assert.IsType<XElement>(document.Root).Element("dict");
        Assert.NotNull(dict);
        var values = ReadPlist(dict);

        Assert.Equal(AppId, values["CFBundleIdentifier"]);
        Assert.Equal("Devolutions.Terminal", values["CFBundleExecutable"]);
        Assert.Equal("APPL", values["CFBundlePackageType"]);
        Assert.Equal("DevolutionsTerminal", values["CFBundleIconFile"]);
        Assert.Equal("13.0", values["LSMinimumSystemVersion"]);
        Assert.Equal("public.app-category.developer-tools", values["LSApplicationCategoryType"]);
        Assert.Equal("true", values["NSHighResolutionCapable"]);
        Assert.Equal("true", values["NSSupportsAutomaticGraphicsSwitching"]);
        Assert.Contains("dterm", File.ReadAllText(Path.Combine(MacOsAssets, "Info.plist")));
    }

    [Fact]
    public void PackageMetadataMatchesCanonicalIdentity()
    {
        var values = File.ReadAllLines(Path.Combine(MacOsAssets, "package.env"))
            .Where(static line => line.Contains('=', StringComparison.Ordinal) && !line.StartsWith('#'))
            .Select(static line => line.Split('=', 2))
            .ToDictionary(static parts => parts[0], static parts => parts[1].Trim('"'), StringComparer.Ordinal);

        Assert.Equal("devolutions-terminal", values["PACKAGE_NAME"]);
        Assert.Equal(AppId, values["APP_ID"]);
        Assert.Equal("Devolutions Terminal.app", values["BUNDLE_NAME"]);
        Assert.Equal("Devolutions.Terminal", values["EXECUTABLE_NAME"]);
        Assert.Equal("dt", values["CLI_NAME"]);
        Assert.Equal("dt-pty-host", values["PTY_HOST_NAME"]);
        Assert.Equal("libghostty-vt.dylib", values["GHOSTTY_LIBRARY"]);
        Assert.Equal("dterm", values["URL_SCHEME"]);
        Assert.Equal("13.0", values["MACOS_DEPLOYMENT_TARGET"]);
        Assert.DoesNotContain(values.Keys, static key => key == "VERSION" || key.EndsWith("_VERSION", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> ReadPlist(XElement dict)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var children = dict.Elements().ToArray();
        for (var index = 0; index < children.Length - 1; index++)
        {
            if (children[index].Name.LocalName != "key")
            {
                continue;
            }

            var key = children[index].Value;
            var value = children[index + 1];
            values[key] = value.Name.LocalName switch
            {
                "string" => value.Value,
                "true" => "true",
                "false" => "false",
                _ => value.Name.LocalName,
            };
        }

        return values;
    }
}
