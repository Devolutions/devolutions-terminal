#if POWERSHELL_ISE
namespace Devolutions.Terminal.App.Connections;

public static class PowerShellIseRuntime
{
    private static readonly object Sync = new();
    private static Task? _initialization;

    public static Task InitializeAsync()
    {
        lock (Sync)
        {
            // A failed discovery can be retried after the user installs PowerShell or changes the override.
            if (_initialization is null || _initialization.IsFaulted)
                _initialization = Task.Run(() => PowerShellInstallation.Find());
            return _initialization;
        }
    }
}
#endif
