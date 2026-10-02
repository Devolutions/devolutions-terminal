using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Actions;

public static class CloseConfirmationPolicy
{
    public static bool RequiresConfirmation(
        ConfirmOnClose policy,
        int runningSessions,
        int unsavedRecordings = 0,
        bool automaticExit = false) =>
        unsavedRecordings > 0 ||
        (!automaticExit && policy switch
        {
            ConfirmOnClose.Always => runningSessions > 0,
            ConfirmOnClose.Automatic => runningSessions > 1,
            _ => false,
        });
}
