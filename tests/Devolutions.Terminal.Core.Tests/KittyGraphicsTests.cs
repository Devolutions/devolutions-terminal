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
    public void RgbFormatExpandsToRgbaAcrossSimdAndScalarTail()
    {
        Assert.True(KittyGraphicsDecoder.TryParse("a=t,f=24,s=10,v=1", out var command, out _));
        var rgb = new byte[30];
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)(i + 1);
        }

        Assert.True(KittyGraphicsDecoder.TryDecodeImageData(command!, rgb, out var data, out _));
        Assert.Equal(10, data!.Width);
        for (var pixel = 0; pixel < 10; pixel++)
        {
            Assert.Equal((byte)((pixel * 3) + 1), data.Rgba32Pixels![(pixel * 4)]);
            Assert.Equal((byte)((pixel * 3) + 2), data.Rgba32Pixels![(pixel * 4) + 1]);
            Assert.Equal((byte)((pixel * 3) + 3), data.Rgba32Pixels![(pixel * 4) + 2]);
            Assert.Equal((byte)0xFF, data.Rgba32Pixels![(pixel * 4) + 3]);
        }
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

    [Fact]
    public void PixelCountBoundaryDistinguishesExactLimitFromOnePixelOver()
    {
        Assert.True(KittyGraphicsDecoder.TryParse(
            "a=t,f=32,s=4096,v=4096",
            out var atLimit,
            out _));
        Assert.False(KittyGraphicsDecoder.TryDecodeImageData(
            atLimit!,
            Array.Empty<byte>(),
            out _,
            out var atLimitError));

        Assert.True(KittyGraphicsDecoder.TryParse(
            "a=t,f=32,s=4096,v=4097",
            out var overLimit,
            out _));
        Assert.False(KittyGraphicsDecoder.TryDecodeImageData(
            overLimit!,
            Array.Empty<byte>(),
            out _,
            out var overLimitError));

        Assert.StartsWith("EINVAL", atLimitError, StringComparison.Ordinal);
        Assert.StartsWith("ETOOMANY", overLimitError, StringComparison.Ordinal);
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
    public void InvalidContinuationAbortsPendingChunksAndNextImageRecovers()
    {
        var (engine, responses) = CreateEngine();
        var diagnostics = new List<TerminalEngineDiagnostic>();
        engine.Diagnostic += (_, value) => diagnostics.Add(value);
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=9,m=1", "AQID"));
        engine.Feed(Apc("i=9,m=0", "!!!"));
        engine.Feed(Apc("i=9,m=0", "BA=="));

        Assert.Empty(engine.Images);
        Assert.Equal(2, responses.Count);
        Assert.All(responses, response => Assert.Contains("EINVAL", response, StringComparison.Ordinal));
        Assert.Contains(diagnostics, value => value.Code == "image.kitty.rejected");

        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=10,C=1", "AQIDBA=="));
        Assert.Equal(10u, Assert.Single(engine.Images).Kitty!.ImageId);
        Assert.Equal([$"{Esc}_Gi=10;OK{Esc}\\"], responses.Skip(2));
    }

    [Fact]
    public void MismatchedContinuationIdDoesNotPublishPartialImage()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=9,m=1", "AQID"));
        engine.Feed(Apc("i=10,m=0", "BA=="));

        Assert.Empty(engine.Images);
        Assert.Single(responses);
        Assert.Contains("EINVAL", responses[0], StringComparison.Ordinal);
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=11,C=1", "AQIDBA=="));
        Assert.Equal(11u, Assert.Single(engine.Images).Kitty!.ImageId);
    }

    [Fact]
    public void ChunkedApcSplitBeforeTerminatorDoesNotPublishUntilCompleted()
    {
        var (engine, responses) = CreateEngine();
        var first = Apc("a=T,f=32,s=1,v=1,i=8,m=1", "AQID");
        engine.Feed(first[..^1]);
        Assert.Empty(engine.Images);
        engine.Feed(first[^1..]);
        Assert.Empty(engine.Images);
        var last = Apc("i=8,m=0", "BA==");
        engine.Feed(last[..^2]);
        Assert.Empty(responses);
        engine.Feed(last[^2..]);
        Assert.Equal([1, 2, 3, 4], Assert.Single(engine.Images).Kitty!.Data.Rgba32Pixels);
        Assert.Equal([$"{Esc}_Gi=8;OK{Esc}\\"], responses);
    }

    [Fact]
    public void OversizedApcFragmentIsDiscardedAndNextTransmissionRecovers()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed($"{Esc}_Ga=T,f=32,s=1,v=1,i=4;{new string('A', 1024 * 1024 + 1)}{Esc}\\");
        Assert.Empty(engine.Images);
        Assert.Empty(responses);

        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=5,C=1", "AQIDBA=="));
        Assert.Equal(5u, Assert.Single(engine.Images).Kitty!.ImageId);
        Assert.Equal([$"{Esc}_Gi=5;OK{Esc}\\"], responses);
    }

    [Fact]
    public void InflatedPayloadBeyondBoundIsRejectedWithoutPlacement()
    {
        var (engine, responses) = CreateEngine();
        var diagnostics = new List<TerminalEngineDiagnostic>();
        engine.Diagnostic += (_, value) => diagnostics.Add(value);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            var zeros = new byte[64 * 1024];
            for (var i = 0; i <= TerminalImageLimits.MaximumKittyImageBytes / zeros.Length; i++)
            {
                zlib.Write(zeros);
            }
        }
        engine.Feed(Apc("a=T,f=100,o=z,i=12", B64(output.ToArray())));
        Assert.Empty(engine.Images);
        Assert.Contains("ETOOMANY", Assert.Single(responses), StringComparison.Ordinal);
        Assert.Contains(diagnostics, value => value.Code == "image.kitty.rejected");
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
    public void PlacementDeletePreservesOtherKittyAndSixelAndStoredData()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed($"{Esc}P7q~{Esc}\\");
        engine.Feed(Apc("a=t,f=32,s=1,v=1,i=5", "AQIDBA=="));
        engine.Feed(Apc("a=p,i=5,p=1,X=0,Y=0,w=1,h=1,c=2,r=1"));
        engine.Feed(Apc("a=p,i=5,p=2"));
        engine.Feed(Apc("a=d,d=p,i=5,p=1"));

        Assert.Equal(2, engine.Images.Count);
        Assert.Contains(engine.Images, image => image.Protocol == TerminalImageProtocol.Sixel);
        Assert.Equal(2u, Assert.Single(engine.Images, image => image.Kitty is not null).Kitty!.PlacementId);
        engine.Feed(Apc("a=p,i=5,p=3"));
        Assert.Equal(3u, engine.Images[^1].Kitty!.PlacementId);
        Assert.Equal([1, 2, 3, 4], engine.Images[^1].Kitty!.Data.Rgba32Pixels);
        Assert.Equal(4, responses.Count);
    }

    [Theory]
    [InlineData('p')]
    [InlineData('i')]
    [InlineData('a')]
    public void DeletingLastPlacementDoesNotFreeTransmittedKittyPixels(char mode)
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=t,f=32,s=1,v=1,i=14", "AQIDBA=="));
        engine.Feed(Apc("a=p,i=14,p=1"));
        Assert.Equal(1u, Assert.Single(engine.Images).Kitty!.PlacementId);
        engine.Feed(Apc($"a=d,d={mode},i=14,p=1"));
        Assert.Empty(engine.Images);

        engine.Feed(Apc("a=p,i=14,p=2"));
        var replacement = Assert.Single(engine.Images);
        Assert.Equal(2u, replacement.Kitty!.PlacementId);
        Assert.Equal([1, 2, 3, 4], replacement.Kitty.Data.Rgba32Pixels);
        Assert.Equal($"{Esc}_Gi=14;OK{Esc}\\", responses[^1]);
    }

    [Theory]
    [InlineData('c')]
    [InlineData('x')]
    [InlineData('y')]
    [InlineData('z')]
    [InlineData('n')]
    public void UnsupportedTargetedDeleteDoesNotRemovePlacementOrData(char mode)
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=5,p=3,C=1", "AQIDBA=="));
        var placed = Assert.Single(engine.Images);
        engine.Feed(Apc($"a=d,d={mode},i=5,p=3"));
        Assert.Same(placed, Assert.Single(engine.Images));
        engine.Feed(Apc("a=p,i=5,p=4"));
        Assert.Equal(4u, engine.Images[^1].Kitty!.PlacementId);
        Assert.Equal(2, responses.Count); // deletes are silent, successful put responds
    }

    [Fact]
    public void ReflowAndScrollbackKeepKittyAnchorUntilOwningSegmentIsEvicted()
    {
        var (engine, _) = CreateEngine(columns: 6, rows: 2);
        engine.Feed("abcd" + Apc("a=T,f=32,s=1,v=1,i=6,C=1", "AQIDBA==") + "efgh");
        engine.Resize(3, 3);
        var anchor = Assert.Single(engine.CreateSnapshot(includeHistory: true).Images);
        Assert.Equal(1, anchor.AnchorRow);
        Assert.Equal(1, anchor.AnchorColumn);
        Assert.Equal(6u, anchor.Kitty!.ImageId);
        engine.Feed("\r\none\r\ntwo");
        Assert.Equal(6u, Assert.Single(engine.CreateSnapshot(includeHistory: true).Images).Kitty!.ImageId);
    }

    [Fact]
    public void AlternateKittyResetDoesNotRestorePreviousBufferPlacements()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=1,C=1", "AQIDBA=="));
        engine.Feed($"{Esc}[?1049h");
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=2,C=1", "AQIDBA=="));
        Assert.Equal(2, engine.Images.Count);
        Assert.True(engine.Images[^1].AlternateBuffer);
        engine.Feed($"{Esc}[?1049l");
        engine.Reset();
        Assert.Empty(engine.Images);
        Assert.Empty(engine.CreateSnapshot(includeHistory: true).Images);
        engine.Feed(Apc("a=p,i=1"));
        engine.Feed(Apc("a=p,i=2"));
        Assert.All(responses.TakeLast(2), response => Assert.Contains("ENOENT", response, StringComparison.Ordinal));
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

    [Theory]
    [InlineData('f')]
    [InlineData('s')]
    public void FileAndSharedMemoryMediaAreRejectedWithoutPublishingImage(char medium)
    {
        var (engine, responses) = CreateEngine();
        var diagnostics = new List<TerminalEngineDiagnostic>();
        engine.Diagnostic += (_, value) => diagnostics.Add(value);

        engine.Feed(Apc($"a=T,t={medium},f=100,i=7", B64([1, 2, 3])));

        Assert.Empty(engine.Images);
        Assert.Single(responses);
        Assert.Contains("ENOTSUP", responses[0], StringComparison.Ordinal);
        Assert.Contains(diagnostics, value => value.Code == "image.kitty.rejected");
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
        // The 8-bit C1 string terminator (0x9C) is ambiguous with UTF-8 continuation bytes,
        // so only the 7-bit ST (ESC \) is recognized here.
        engine.Feed(new byte[] { 0x9F }.Concat(bytes).Concat(new byte[] { 0x1B, (byte)'\\' }).ToArray());
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

    [Fact]
    public void DelInsideApcIsIgnoredAndOversizedApcIgnoresBelAndStartsFollowingEscape()
    {
        var (engine, responses) = CreateEngine(columns: 10, rows: 2);
        engine.Feed($"{Esc}_Ga=T,f=32,s=1,v=1,i=1,C=1;AQ\u007fIDBA=={Esc}\\");
        Assert.Single(engine.Images);
        Assert.Equal([$"{Esc}_Gi=1;OK{Esc}\\"], responses);

        engine.Feed($"{Esc}_Ga=T,i=2;{new string('A', 1024 * 1024 + 4)}\aNOTPRINTED{Esc}[1mZ");
        Assert.Equal("Z", engine.Buffer.GetCell(0, 0).Text);
        Assert.Equal(CellFlags.Bold, engine.Buffer.GetCell(0, 0).Attributes.Flags);
    }

    [Fact]
    public void NonQuantumAlignedChunkedBase64AssemblesCleanly()
    {
        var (engine, responses) = CreateEngine();
        // "AQIDBA==" (8 chars) split as 5 + 2 + 1 across chunks.
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=15,C=1,m=1", "AQIDB"));
        engine.Feed(Apc("m=1", "A="));
        engine.Feed(Apc("m=0", "="));

        var overlay = Assert.Single(engine.Images);
        Assert.Equal([1, 2, 3, 4], overlay.Kitty!.Data.Rgba32Pixels);
        Assert.Equal([$"{Esc}_Gi=15;OK{Esc}\\"], responses);
    }

    [Fact]
    public void ParseFailurePreservesImageIdAndHonorsQuietFlags()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=T,i=42,q=2", "!!!not-base64!!!"));
        Assert.Empty(responses);

        engine.Feed(Apc("a=T,i=42,q=1", "!!!not-base64!!!"));
        Assert.Equal($"{Esc}_Gi=42;EINVAL: payload is not valid base64{Esc}\\", Assert.Single(responses));
    }

    [Fact]
    public void ContinuationChunkErrorsInheritInitialQuietFlag()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=9,q=2,m=1", "AQID"));
        engine.Feed(Apc("i=10,m=0", "BA=="));
        Assert.Empty(responses);

        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=9,q=2,m=1", "AQID"));
        engine.Feed(Apc("i=9,m=0", "!!!"));
        Assert.Empty(responses);
    }

    [Fact]
    public void DeleteDuringIncompleteChunkAbortsTransferAndExecutesDelete()
    {
        var (engine, _) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=1,C=1", "AQIDBA=="));
        Assert.Single(engine.Images);

        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=2,m=1", "AQIDB"));
        engine.Feed(Apc("a=d,d=a"));

        Assert.Empty(engine.Images);
    }

    [Fact]
    public void ImageNumberValidatesAndResolvesPutAndDelete()
    {
        Assert.False(KittyGraphicsDecoder.TryParse("a=t,I=bad", out _, out var badNumberError));
        Assert.StartsWith("EINVAL", badNumberError, StringComparison.Ordinal);

        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=t,f=32,s=1,v=1,I=77", "AQIDBA=="));
        Assert.Equal($"{Esc}_Gi=1,I=77;OK{Esc}\\", Assert.Single(responses));

        engine.Feed(Apc("a=p,I=77,p=3"));
        Assert.Single(engine.Images);
        Assert.Equal($"{Esc}_Gi=1,I=77;OK{Esc}\\", responses[^1]);

        engine.Feed(Apc("a=d,d=I,I=77"));
        Assert.Empty(engine.Images);
    }

    [Fact]
    public void ReusingNonZeroPlacementIdReplacesExistingPlacement()
    {
        var (engine, _) = CreateEngine();
        engine.Feed(Apc("a=t,f=32,s=1,v=1,i=8", "AQIDBA=="));
        engine.Feed(Apc("a=p,i=8,p=5,c=2,r=1"));
        engine.Feed("\u001b[2;4H");
        engine.Feed(Apc("a=p,i=8,p=5,c=4,r=2"));

        var overlay = Assert.Single(engine.Images);
        Assert.Equal(3, overlay.AnchorColumn);
        Assert.Equal(1, overlay.AnchorRow);
        Assert.Equal(4, overlay.Kitty!.Columns);
        Assert.Equal(2, overlay.Kitty.Rows);
    }

    [Fact]
    public void MultiplePlacementsSharingKittyImageDataDoNotTriggerFalseByteEviction()
    {
        var (engine, _) = CreateEngine();
        // 12 MiB decompressed image (1536 x 2048 x 4 = 12,582,912 bytes):
        // 8 placements = 96 MiB if double-counted > 64 MiB limit.
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                zlib.Write(new byte[1536 * 2048 * 4]);
            }

            compressed = output.ToArray();
        }

        engine.Feed(Apc("a=t,f=32,o=z,s=1536,v=2048,i=1", B64(compressed)));
        for (uint p = 1; p <= 8; p++)
        {
            engine.Feed(Apc($"a=p,i=1,p={p},c=1,r=1"));
        }

        Assert.Equal(8, engine.Images.Count);
    }

    [Theory]
    [InlineData("a=t,o=x,f=100", "ENOTSUP")]
    [InlineData("a=t,C=2,f=100", "EINVAL")]
    [InlineData("a=t,m=2,f=100", "EINVAL")]
    [InlineData("a=t,q=-1,f=100", "EINVAL")]
    public void RejectsUnknownCompressionAndInvalidControlFlags(string control, string expectedPrefix)
    {
        if (!KittyGraphicsDecoder.TryParse(control, out var command, out var parseError))
        {
            Assert.StartsWith(expectedPrefix, parseError, StringComparison.Ordinal);
            return;
        }

        Assert.False(KittyGraphicsDecoder.TryDecodeImageData(command!, [1, 2, 3], out _, out var decodeError));
        Assert.StartsWith(expectedPrefix, decodeError, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteByIdWithPlacementPreservesSiblingPlacementsAndUppercasePlacementDeleteFreesUnreferencedData()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=t,f=32,s=1,v=1,i=20", "AQIDBA=="));
        engine.Feed(Apc("a=p,i=20,p=1"));
        engine.Feed(Apc("a=p,i=20,p=2"));

        engine.Feed(Apc("a=d,d=i,i=20,p=1"));
        Assert.Equal(2u, Assert.Single(engine.Images).Kitty!.PlacementId);

        engine.Feed(Apc("a=d,d=P,i=20,p=2"));
        Assert.Empty(engine.Images);
        engine.Feed(Apc("a=p,i=20,p=3"));
        Assert.Contains("ENOENT", responses[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteAllInAlternateBufferPreservesPrimaryBufferPlacements()
    {
        var (engine, _) = CreateEngine();
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=1,C=1", "AQIDBA=="));
        engine.Feed($"{Esc}[?1049h");
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=2,C=1", "AQIDBA=="));
        engine.Feed(Apc("a=d,d=A"));
        engine.Feed($"{Esc}[?1049l");

        var primary = Assert.Single(engine.Images);
        Assert.False(primary.AlternateBuffer);
        Assert.Equal(1u, primary.Kitty!.ImageId);
    }

    [Fact]
    public void CompressedRawWithMissingOrOversizedDimensionsFailsBeforeDecompression()
    {
        Assert.True(KittyGraphicsDecoder.TryParse("a=t,f=32,o=z", out var missingDims, out _));
        Assert.False(KittyGraphicsDecoder.TryDecodeImageData(missingDims!, [0xFF, 0xFF], out _, out var missingErr));
        Assert.StartsWith("EINVAL: raw formats require s and v", missingErr, StringComparison.Ordinal);

        Assert.True(KittyGraphicsDecoder.TryParse("a=t,f=32,o=z,s=5000,v=1", out var overDims, out _));
        Assert.False(KittyGraphicsDecoder.TryDecodeImageData(overDims!, [0xFF, 0xFF], out _, out var overErr));
        Assert.StartsWith("ETOOMANY: image dimensions exceed", overErr, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryDoesNotAdvanceAutoIdAndPrimaryTransmitOnlySurvivesAltScreen()
    {
        var (engine, responses) = CreateEngine();
        engine.Feed(Apc("a=q,f=32,s=1,v=1", "AQIDBA=="));
        Assert.Equal($"{Esc}_Gi=0;OK{Esc}\\", responses[0]);

        engine.Feed(Apc("a=t,f=32,s=1,v=1", "AQIDBA=="));
        Assert.Equal($"{Esc}_Gi=1;OK{Esc}\\", responses[1]);

        // Entering cleared alternate screen must not reap unplaced primary a=t image i=1.
        engine.Feed($"{Esc}[?1049h");
        engine.Feed(Apc("a=p,i=1,p=1"));
        Assert.Single(engine.Images);
        Assert.Equal($"{Esc}_Gi=1;OK{Esc}\\", responses[2]);
    }

    [Fact]
    public void ColumnScaledPlacementMovesCursorByAspectPreservedRowsAndRejectsNegativeUnsignedKeys()
    {
        Assert.False(KittyGraphicsDecoder.TryParse("a=T,c=-2", out _, out var negColErr));
        Assert.StartsWith("EINVAL", negColErr, StringComparison.Ordinal);
        Assert.False(KittyGraphicsDecoder.TryParse("a=d,d=", out _, out var emptyDelErr));
        Assert.StartsWith("EINVAL", emptyDelErr, StringComparison.Ordinal);

        var (engine, _) = CreateEngine(columns: 80, rows: 24);
        engine.Resize(80, 24, 10, 20);
        // 40x20 image scaled to c=2 (20px wide -> aspect-preserved 10px tall = 1 row, not 20px/unclamped).
        var pixels = new byte[40 * 80 * 4]; // 40x80 scaled to c=2 (20px wide -> 40px tall = 2 rows, not 80/20 = 4 rows!)
        engine.Feed(Apc("a=T,f=32,s=40,v=80,c=2,i=1", B64(pixels)));
        Assert.Equal(2, engine.CursorY);
    }

    [Fact]
    public void ContinuationChunkValidatesValuesHandlesInterruptedTransfersAndSoftResetClearsState()
    {
        var (engine, responses) = CreateEngine();

        // Empty value (`a=`) on continuation chunk rejects with initial chunk's image ID & quiet flag.
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=31,q=1,m=1", "AQID"));
        engine.Feed(Apc("a=,m=0", "BA=="));
        Assert.Equal($"{Esc}_Gi=31;EINVAL: malformed control value{Esc}\\", Assert.Single(responses));
        responses.Clear();

        // Query probe `a=q` during incomplete `m=1` aborts pending transfer and answers probe.
        engine.Feed(Apc("a=T,f=32,s=2,v=2,i=32,m=1", "AQIDBA=="));
        engine.Feed(Apc("a=q,f=32,s=1,v=1,i=33", "AQIDBA=="));
        Assert.Equal([$"{Esc}_Gi=33;OK{Esc}\\"], responses);
        Assert.Empty(engine.Images);
        responses.Clear();

        // Fresh transmission after dropped oversized chunk aborts stale transfer and succeeds.
        engine.Feed(Apc("a=T,f=32,s=2,v=2,i=34,m=1", "AQIDBA=="));
        engine.Feed($"{Esc}_Gm=1;{new string('A', (1024 * 1024) + 8)}{Esc}\\");
        engine.Feed(Apc("a=T,f=32,s=1,v=1,i=35,C=1", "AQIDBA=="));
        Assert.Equal(35u, Assert.Single(engine.Images).Kitty!.ImageId);
        Assert.Equal([$"{Esc}_Gi=35;OK{Esc}\\"], responses);
        responses.Clear();

        // DECSTR soft reset (`CSI ! p`) clears incomplete chunk state.
        engine.Feed(Apc("a=t,f=32,s=1,v=1,i=36,m=1", "AQID"));
        engine.Feed($"{Esc}[!p");
        engine.Feed(Apc("a=t,f=32,s=1,v=1,i=37", "AQIDBA=="));
        Assert.Equal([$"{Esc}_Gi=37;OK{Esc}\\"], responses);
    }

    [Fact]
    public void UppercaseDeletePrunesSameFeedScrollbackEvictedPlacementsBeforeFreeingImageData()
    {
        var smallEngine = new TerminalEngine(10, 2, historySize: 1);
        var smallResponses = new List<string>();
        smallEngine.ResponseReady += (_, bytes) => smallResponses.Add(Encoding.UTF8.GetString(bytes));

        smallEngine.Feed(Apc("a=t,f=32,s=1,v=1,i=1", "AQIDBA=="));
        smallEngine.Feed(Apc("a=p,i=1,p=1"));
        // In one Feed batch: scroll p=1 out of history, place p=2, then uppercase-delete p=2.
        smallEngine.Feed(
            "\r\none\r\ntwo\r\nthree" +
            Apc("a=p,i=1,p=2") +
            Apc("a=d,d=P,i=1,p=2"));

        Assert.Empty(smallEngine.Images);
        smallEngine.Feed(Apc("a=p,i=1,p=3"));
        Assert.Contains("ENOENT", smallResponses[^1], StringComparison.Ordinal);
    }
}
