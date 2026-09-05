using System.IO.Compression;
using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class KittyGraphicsTests
{
    private const string Esc = "\u001b";

    private static string Apc(string control, string? base64Payload = null) =>
        base64Payload is null
            ? $"{Esc}_G{control}{Esc}\\"
            : $"{Esc}_G{control};{base64Payload}{Esc}\\";

    private static string B64(byte[] data) => Convert.ToBase64String(data);

    private static (TerminalEngine Engine, List<string> Responses) CreateEngine(int columns = 80, int rows = 24)
    {
        var engine = new TerminalEngine(columns, rows);
        var responses = new List<string>();
        engine.ResponseReady += (_, bytes) => responses.Add(Encoding.UTF8.GetString(bytes));
        return (engine, responses);
    }

    // --- decoder ---

    [Fact]
    public void ParsesAllControlKeys()
    {
        var ok = KittyGraphicsDecoder.TryParse(
            "a=t,t=d,f=32,o=z,s=4,v=2,i=7,I=9,p=3,c=10,r=5,x=1,y=2,X=1,Y=1,w=2,h=2,z=-3,q=1,C=1,m=1,d=a",
            out var command,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(command);
        Assert.Equal(KittyGraphicsAction.Transmit, command.Action);
        Assert.Equal('d', command.Medium);
        Assert.Equal(32, command.Format);
        Assert.True(command.Compressed);
        Assert.Equal(4, command.SourceWidth);
        Assert.Equal(2, command.SourceHeight);
        Assert.Equal(7u, command.ImageId);
        Assert.Equal(3u, command.PlacementId);
        Assert.Equal(10, command.Columns);
        Assert.Equal(5, command.Rows);
        Assert.Equal(1, command.PixelOffsetX);
        Assert.Equal(2, command.PixelOffsetY);
        Assert.Equal(1, command.CropX);
        Assert.Equal(1, command.CropY);
        Assert.Equal(2, command.CropWidth);
        Assert.Equal(2, command.CropHeight);
        Assert.Equal(-3, command.ZIndex);
        Assert.Equal(1, command.Quiet);
        Assert.True(command.NoCursorMove);
        Assert.True(command.MoreChunks);
    }

    [Fact]
    public void DefaultsMatchKittySpec()
    {
        Assert.True(KittyGraphicsDecoder.TryParse("", out var command, out _));
        Assert.Equal(KittyGraphicsAction.TransmitAndDisplay, command!.Action);
        Assert.Equal('d', command.Medium);
        Assert.Equal(32, command.Format);
        Assert.Equal(0u, command.ImageId);
        Assert.False(command.MoreChunks);
    }

    [Fact]
    public void UnknownKeysAreIgnored()
    {
        Assert.True(KittyGraphicsDecoder.TryParse("n=42,a=q", out var command, out _));
        Assert.Equal(KittyGraphicsAction.Query, command!.Action);
    }

    [Theory]
    [InlineData("aa=1")]
    [InlineData("=1")]
    [InlineData("i=abc")]
    [InlineData("s=1.5")]
    public void MalformedControlKeysAreRejected(string control)
    {
        Assert.False(KittyGraphicsDecoder.TryParse(control, out _, out var error));
        Assert.StartsWith("EINVAL", error, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidBase64PayloadIsRejected()
    {
        Assert.False(KittyGraphicsDecoder.TryParse("a=t;!!!not-base64!!!", out _, out var error));
        Assert.Contains("base64", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RgbFormatExpandsToRgba()
    {
        Assert.True(KittyGraphicsDecoder.TryParse("a=t,f=24,s=2,v=1", out var command, out _));
        var rgb = new byte[] { 255, 0, 0, 0, 255, 0 };
        Assert.True(KittyGraphicsDecoder.TryDecodeImageData(command!, rgb, out var data, out var error));

        Assert.Equal(2, data!.Width);
        Assert.Equal(1, data.Height);
        Assert.Equal(
            new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 },
            data.Rgba32Pixels);
    }

    [Fact]
    public void RgbaFormatPassesThrough()
    {
        Assert.True(KittyGraphicsDecoder.TryParse("a=t,f=32,s=1,v=1", out var command, out _));
        var rgba = new byte[] { 1, 2, 3, 4 };
        Assert.True(KittyGraphicsDecoder.TryDecodeImageData(command!, rgba, out var data, out _));
        Assert.Equal(rgba, data!.Rgba32Pixels);
    }

    [Fact]
    public void ZlibPayloadDecompresses()
    {
        var rgba = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                zlib.Write(rgba);
            }

            compressed = output.ToArray();
        }

        Assert.True(KittyGraphicsDecoder.TryParse("a=t,f=32,o=z,s=2,v=1", out var command, out _));
        Assert.True(command!.Compressed);
        Assert.True(KittyGraphicsDecoder.TryDecodeImageData(command, compressed, out var data, out var error));
        Assert.Equal(rgba, data!.Rgba32Pixels);
        Assert.Null(error);
    }

    [Fact]
    public void EncodedFormatRetainsCodecBytes()
    {
        Assert.True(KittyGraphicsDecoder.TryParse("a=t,f=100", out var command, out _));
        var pngMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.True(KittyGraphicsDecoder.TryDecodeImageData(command!, pngMagic, out var data, out _));
        Assert.Null(data!.Rgba32Pixels);
        Assert.Equal(pngMagic, data.EncodedData);
    }

    [Theory]
    // raw formats require dimensions
    [InlineData("a=t,f=32", "EINVAL")]
    // payload length must match s*v*format
    [InlineData("a=t,f=32,s=2,v=2", "EINVAL")]
    // dimensions beyond pixel limits
    [InlineData("a=t,f=32,s=99999,v=99999", "ETOOMANY")]
    // only direct transmission
    [InlineData("a=t,t=f,f=100", "ENOTSUP")]
    [InlineData("a=t,t=s,f=100", "ENOTSUP")]
    // unsupported pixel formats
    [InlineData("a=t,f=1,s=1,v=1", "EINVAL")]
    public void InvalidTransmissionsAreRejected(string control, string expectedCode)
    {
        Assert.True(KittyGraphicsDecoder.TryParse(control, out var command, out _));
        // 15 bytes: wrong for any 2x2 RGBA payload, irrelevant for earlier failures.
        var payload = control.Contains("f=100", StringComparison.Ordinal) ? new byte[] { 1, 2, 3 } : new byte[15];
        Assert.False(KittyGraphicsDecoder.TryDecodeImageData(command!, payload, out _, out var error));
        Assert.StartsWith(expectedCode, error, StringComparison.Ordinal);
    }

    // --- engine ---

    [Fact]
    public void TransmitAndDisplayCreatesOverlayAtCursorAndResponds()
    {
        var (engine, responses) = CreateEngine();
        var payload = B64(new byte[] { 255, 0, 0, 255 });

        engine.Feed($"{Apc($"a=T,f=32,s=1,v=1,i=7,r=1", payload)}");

        var overlay = Assert.Single(engine.Images);
        Assert.Equal(TerminalImageProtocol.KittyGraphics, overlay.Protocol);
        Assert.NotNull(overlay.Kitty);
        Assert.Equal(7u, overlay.Kitty.ImageId);
        Assert.Equal(1, overlay.Kitty.Rows);
        Assert.Equal(0, overlay.AnchorColumn);
        Assert.Equal(0, overlay.AnchorRow);
        Assert.Equal([$"{Esc}_Gi=7;OK{Esc}\\"], responses);
        // Cursor moved below the image (r=1, no C=1).
        Assert.Equal(1, engine.CursorY);
    }

    [Fact]
    public void NoCursorMoveKeepsCursorInPlace()
    {
        var (engine, _) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=7,r=3,C=1", B64(new byte[] { 1, 2, 3, 4 })));
        Assert.Equal(0, engine.CursorY);
    }

    [Fact]
    public void NaturalHeightMovesCursorByPixelRows()
    {
        var (engine, _) = CreateEngine();
        engine.Resize(80, 24, 10, 20);
        // 60px tall / 20px cell = 3 rows, no explicit r.
        var pixels = new byte[10 * 60 * 4];
        engine.Feed(Apc("a=T,f=32,s=10,v=60,i=1", B64(pixels)));
        Assert.Equal(3, engine.CursorY);
    }

    [Fact]
    public void AutoAssignedImageIdIsReported()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=t,f=32,s=1,v=1", B64(new byte[] { 1, 2, 3, 4 })));
        Assert.Equal([$"{Esc}_Gi=1;OK{Esc}\\"], responses);
        Assert.Empty(engine.Images); // transmit-only: nothing displayed
    }

    [Fact]
    public void ChunkedTransmissionAssemblesOneImage()
    {
        var (engine, responses) = CreateEngine();
        var pixels = new byte[2 * 2 * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)i;
        }

        var base64 = B64(pixels);
        var half = base64.Length / 2;
        half -= half % 4; // chunks split on a base64 quantum

        engine.Feed(Apc("a=T,f=32,s=2,v=2,i=9,m=1", base64[..half]));
        Assert.Empty(engine.Images);
        Assert.Empty(responses);

        engine.Feed(Apc("i=9,m=0", base64[half..]));

        var overlay = Assert.Single(engine.Images);
        Assert.Equal(pixels, overlay.Kitty!.Data.Rgba32Pixels);
        Assert.Equal([$"{Esc}_Gi=9;OK{Esc}\\"], responses);
    }

    [Fact]
    public void PutDisplaysTransmittedImageWithoutMovingCursor()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=t,f=32,s=1,v=1,i=4", B64(new byte[] { 1, 2, 3, 4 })));
        engine.Feed("AB"); // move cursor off the origin
        engine.Feed(Apc("a=p,i=4,p=2,c=4,r=2"));

        var overlay = Assert.Single(engine.Images);
        Assert.Equal(4u, overlay.Kitty!.ImageId);
        Assert.Equal(2u, overlay.Kitty.PlacementId);
        Assert.Equal(4, overlay.Kitty.Columns);
        Assert.Equal(2, overlay.Kitty.Rows);
        Assert.Equal(2, overlay.AnchorColumn);
        Assert.Equal(0, engine.CursorY); // put does not move the cursor
        Assert.Equal(2, engine.CursorX);
        Assert.Equal(
            [$"{Esc}_Gi=4;OK{Esc}\\", $"{Esc}_Gi=4;OK{Esc}\\"],
            responses);
    }

    [Fact]
    public void PutWithUnknownIdRespondsEnoent()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=p,i=42"));
        Assert.Equal([$"{Esc}_Gi=42;ENOENT: no image with that id{Esc}\\"], responses);
        Assert.Empty(engine.Images);
    }

    [Fact]
    public void QueryRespondsOkWithoutStoring()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=q,f=32,s=1,v=1,i=1", B64(new byte[] { 1, 2, 3, 4 })));
        Assert.Equal([$"{Esc}_Gi=1;OK{Esc}\\"], responses);
        Assert.Empty(engine.Images);

        // A put for the queried id fails: query does not store.
        engine.Feed(Apc("a=p,i=1"));
        Assert.EndsWith("ENOENT: no image with that id" + Esc + "\\", responses[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteByIdRemovesPlacements()
    {
        var (engine, _) = CreateEngine();
        var payload = B64(new byte[] { 1, 2, 3, 4 });
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=1,C=1", payload));
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=2,C=1", payload));
        Assert.Equal(2, engine.Images.Count);

        engine.Feed(Apc("a=d,d=i,i=1"));
        var remaining = Assert.Single(engine.Images);
        Assert.Equal(2u, remaining.Kitty!.ImageId);
    }

    [Fact]
    public void DeleteUppercaseFreesImageData()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=3,C=1", B64(new byte[] { 1, 2, 3, 4 })));
        engine.Feed(Apc("a=d,d=I,i=3"));
        Assert.Empty(engine.Images);

        // Data freed: a later put for the id fails.
        engine.Feed(Apc("a=p,i=3"));
        Assert.EndsWith("ENOENT: no image with that id" + Esc + "\\", responses[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteAllClearsEverything()
    {
        var (engine, _) = CreateEngine();
        var payload = B64(new byte[] { 1, 2, 3, 4 });
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=1,C=1", payload));
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=2,C=1", payload));

        engine.Feed(Apc("a=d,d=A"));
        Assert.Empty(engine.Images);
    }

    [Fact]
    public void QuietFlagsSuppressResponses()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=q,f=32,s=1,v=1,i=1,q=1", B64(new byte[] { 1, 2, 3, 4 })));
        Assert.Empty(responses); // q=1 suppresses OK

        engine.Feed(Apc("a=p,i=99,q=1"));
        Assert.Single(responses); // q=1 still reports errors
        Assert.Contains("ENOENT", responses[0], StringComparison.Ordinal);

        engine.Feed(Apc("a=p,i=99,q=2"));
        Assert.Single(responses); // q=2 suppresses errors too
    }

    [Fact]
    public void UnsupportedActionRespondsNotsup()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=f,i=1"));
        Assert.Contains("ENOTSUP", responses[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ResetClearsKittyState()
    {
        var (engine, _) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=1,C=1", B64(new byte[] { 1, 2, 3, 4 })));
        Assert.Single(engine.Images);

        engine.Reset();
        Assert.Empty(engine.Images);
    }

    [Fact]
    public void KittyOverlaySurvivesScrollbackAnchoring()
    {
        var (engine, _) = CreateEngine(rows: 5);
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=1,C=1", B64(new byte[] { 1, 2, 3, 4 })));
        // Scroll past the viewport: the anchor must keep the image attached.
        engine.Feed(string.Concat(Enumerable.Repeat("line\r\n", 20)));

        var snapshot = engine.CreateSnapshot(includeHistory: true);
        Assert.Contains(snapshot.Images, image => image.Kitty is { ImageId: 1 });
    }

    [Fact]
    public void CapabilityAdvertised()
    {
        var (engine, _) = CreateEngine();
        Assert.True(engine.Capabilities.HasFlag(TerminalEngineCapabilities.KittyImages));
    }

    [Fact]
    public void ApcWithoutGIsIgnored()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed($"{Esc}_Xsome-other-apc{Esc}\\");
        Assert.Empty(engine.Images);
        Assert.Empty(responses);
    }

    [Fact]
    public void ApcSplitAcrossFeedsAssembles()
    {
        var (engine, responses) = CreateEngine();
        var sequence = Apc("a=T,f=32,s=1,v=1,i=1,C=1", B64(new byte[] { 1, 2, 3, 4 }));
        var midpoint = sequence.Length / 2;
        engine.Feed(sequence[..midpoint]);
        Assert.Empty(engine.Images);
        engine.Feed(sequence[midpoint..]);

        Assert.Single(engine.Images);
        Assert.Single(responses);
    }

    [Fact]
    public void ApcEntryViaC1Works()
    {
        var (engine, responses) = CreateEngine();
        var bytes = Encoding.ASCII.GetBytes("Ga=q,f=32,s=1,v=1,i=1;" + B64(new byte[] { 1, 2, 3, 4 }));
        engine.Feed(new byte[] { 0x9F }.Concat(bytes).Concat(new byte[] { 0x9C }).ToArray());
        Assert.Equal([$"{Esc}_Gi=1;OK{Esc}\\"], responses);
    }

    [Fact]
    public void CanCancelsApc()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed($"{Esc}_Ga=T,i=1\u0018text after cancel");
        Assert.Empty(engine.Images);
        Assert.Empty(responses);
        // The printable tail was still processed as text.
        Assert.Equal('t', engine.CreateSnapshot().Buffer.Lines[0].Cells[0].Text.First());
    }

    [Fact]
    public void BelDoesNotTerminateApc()
    {
        var (engine, responses) = CreateEngine();
        // BEL inside the payload is not a terminator: it corrupts the base64,
        // which proves the APC ran to its ST.
        engine.Feed($"{Esc}_Ga=T,i=1;\a{Esc}\\");
        var response = Assert.Single(responses);
        Assert.Contains("EINVAL", response, StringComparison.Ordinal);
        Assert.Empty(engine.Images);
    }
}
