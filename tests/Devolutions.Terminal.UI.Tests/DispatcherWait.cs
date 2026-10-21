using System.Diagnostics;
using Avalonia.Threading;

namespace Devolutions.Terminal.UI.Tests;

/// <summary>Polls a UI-thread condition, yielding to dispatcher work between checks.</summary>
internal static class DispatcherWait
{
    public static async Task UntilAsync(Func<bool> condition, Func<string> diagnostic)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException(diagnostic());
            // Yield to dispatcher work, not wall-clock timing or an arbitrary sleep.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
