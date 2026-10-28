using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;
using Devolutions.Terminal.App.Connections;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class TerminalEngineFactoryTests
{
    [Fact]
    public void CreatesTheBuiltInEngine()
    {
        using var engine = TerminalEngineFactory.Create(new AppSettings(), new ProfileSettings());

        Assert.IsType<TerminalEngine>(engine);
    }

    [Fact]
    public void LegacyGhosttySelectionLoadsTheBuiltInEngine()
    {
        const string user = """{ "experimental.terminalEngine": "ghostty" }""";
        var settings = SettingsLoader.Load(SettingsLoader.ReadEmbeddedDefaults(), user);

        using var engine = TerminalEngineFactory.Create(settings, new ProfileSettings());

        Assert.IsType<TerminalEngine>(engine);
    }
}
