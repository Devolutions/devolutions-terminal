using Avalonia.Input;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

// Win32 input mode (?9001h, as ConPTY asks): keys that type text are sent when their
// text arrives, so dead keys compose and non-ASCII text goes as key records.
public sealed class TermControlWin32TextInputTests
{
    private static readonly TerminalInputMode Win32 = new(
        true,
        false,
        false,
        KittyKeyboardFlags.None,
        0,
        true);

    [Fact]
    public void DeadKeyComposesWithTheNextKey()
    {
        var control = new TermControl();

        // Portuguese layout: "~" is a dead key. Windows raises its KeyDown and KeyUp
        // with the accent as the symbol, and no text.
        Assert.Null(control.ProcessKeyDownInput(Key.OemQuestion, KeyModifiers.None, PhysicalKey.Slash, "~", Win32));
        Assert.Null(control.ProcessKeyUpInput(Key.OemQuestion, KeyModifiers.None, PhysicalKey.Slash, "~", Win32));

        // The next key arrives as DeadCharProcessed, followed by the composed text.
        Assert.Null(control.ProcessKeyDownInput(Key.DeadCharProcessed, KeyModifiers.None, PhysicalKey.A, null, Win32));
        Assert.Equal("\u001b[65;0;227;1;0;1_", control.ProcessTextInput("ã", Win32));
        Assert.Equal("\u001b[65;0;97;0;0;1_", control.ProcessKeyUpInput(Key.A, KeyModifiers.None, PhysicalKey.A, "a", Win32));
    }

    [Fact]
    public void DeadKeyThenSpaceTypesTheAccent()
    {
        var control = new TermControl();

        Assert.Null(control.ProcessKeyDownInput(Key.OemQuestion, KeyModifiers.None, PhysicalKey.Slash, "~", Win32));
        Assert.Null(control.ProcessKeyDownInput(Key.Space, KeyModifiers.None, PhysicalKey.Space, " ", Win32));
        Assert.Equal("\u001b[32;0;126;1;0;1_", control.ProcessTextInput("~", Win32));
    }

    [Fact]
    public void SpaceIsSentWithItsText()
    {
        var control = new TermControl();

        Assert.Null(control.ProcessKeyDownInput(Key.Space, KeyModifiers.None, PhysicalKey.Space, " ", Win32));
        Assert.Equal("\u001b[32;0;32;1;0;1_", control.ProcessTextInput(" ", Win32));
    }

    [Fact]
    public void PrintableKeyIsSentWithItsText()
    {
        var control = new TermControl();

        Assert.Null(control.ProcessKeyDownInput(Key.A, KeyModifiers.Shift, PhysicalKey.A, "A", Win32));
        Assert.Equal("\u001b[65;0;65;1;16;1_", control.ProcessTextInput("A", Win32));
        Assert.Equal("\u001b[65;0;65;0;16;1_", control.ProcessKeyUpInput(Key.A, KeyModifiers.Shift, PhysicalKey.A, "A", Win32));
    }

    [Fact]
    public void HeldKeyRepeatsWithItsText()
    {
        var control = new TermControl();

        Assert.Null(control.ProcessKeyDownInput(Key.A, KeyModifiers.None, PhysicalKey.A, "a", Win32));
        Assert.Equal("\u001b[65;0;97;1;0;1_", control.ProcessTextInput("a", Win32));
        Assert.Null(control.ProcessKeyDownInput(Key.A, KeyModifiers.None, PhysicalKey.A, "a", Win32));
        Assert.Equal("\u001b[65;0;97;1;0;1_", control.ProcessTextInput("a", Win32));
    }

    [Fact]
    public void TextWithoutAKeyGoesAsKeyRecords()
    {
        var control = new TermControl();

        // An IME commit: no key is waiting for it.
        Assert.Equal(
            "\u001b[0;0;26085;1;0;1_\u001b[0;0;26085;0;0;1_\u001b[0;0;26412;1;0;1_\u001b[0;0;26412;0;0;1_",
            control.ProcessTextInput("日本", Win32));
    }

    [Fact]
    public void ModifiedAndNonTextKeysAreStillSentAtOnce()
    {
        var control = new TermControl();

        // AltGr (Ctrl+Alt) carries its character on the key itself.
        Assert.Equal("\u001b[69;0;8364;1;10;1_", control.ProcessKeyDownInput(Key.E, KeyModifiers.Control | KeyModifiers.Alt, PhysicalKey.E, "€", Win32));
        Assert.Equal("\u001b[67;0;3;1;8;1_", control.ProcessKeyDownInput(Key.C, KeyModifiers.Control, PhysicalKey.C, "c", Win32));
        Assert.Equal("\u001b[13;0;13;1;0;1_", control.ProcessKeyDownInput(Key.Enter, KeyModifiers.None, PhysicalKey.Enter, "\r", Win32));
    }

    [Fact]
    public void AKeyWhoseTextNeverCameIsNotSentByALaterText()
    {
        var control = new TermControl();

        // A dead key followed by a key that sends at once: the accent is dropped,
        // and a later IME commit is not attributed to the dead key.
        Assert.Null(control.ProcessKeyDownInput(Key.OemQuestion, KeyModifiers.None, PhysicalKey.Slash, "~", Win32));
        Assert.NotNull(control.ProcessKeyDownInput(Key.Left, KeyModifiers.None, PhysicalKey.ArrowLeft, null, Win32));
        Assert.Equal("\u001b[0;0;233;1;0;1_\u001b[0;0;233;0;0;1_", control.ProcessTextInput("é", Win32));
    }
}
