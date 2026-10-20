using System.Globalization;
using System.Text;

namespace Devolutions.Terminal.Core;

public static class TerminalEngineExtensions
{
    // Mirrors Windows Terminal's clear buffer (microsoft/terminal#18732): erase the
    // scrollback and screen but keep the cursor's row, moved to the top. ConPTY's
    // keep-cursor-row clear does the same to its own buffer, so the shell's next
    // absolute cursor move still lands on the prompt instead of mid-screen.
    public static void ClearBufferKeepingCursorRow(this ITerminalEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var cursorX = engine.CursorX;
        var cursorY = engine.CursorY;
        var sequence = new StringBuilder("\u001b[3J");
        if (engine.Rows - cursorY > 1)
        {
            sequence.Append(CultureInfo.InvariantCulture, $"\u001b[{cursorY + 2};1H\u001b[J");
        }

        if (cursorY > 0)
        {
            sequence.Append(CultureInfo.InvariantCulture, $"\u001b[H\u001b[{cursorY}M");
        }

        sequence.Append(CultureInfo.InvariantCulture, $"\u001b[1;{cursorX + 1}H");
        engine.Feed(sequence.ToString());
    }
}
