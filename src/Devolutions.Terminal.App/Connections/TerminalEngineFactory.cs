using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Connections;

public static class TerminalEngineFactory
{
    public static ITerminalEngine Create(AppSettings settings, ProfileSettings profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        return new TerminalEngine(historySize: profile.HistorySize);
    }
}
