using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class SixelDecoderTests
{
    [Fact]
    public void DecodesRepeatAndSixVerticalPixels()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("!3~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(3, image.Width);
        Assert.Equal(6, image.Height);
        Assert.Equal(1, image.PixelAspectRatio);
        Assert.All(image.PixelIndices.ToArray(), value => Assert.Equal((ushort)15, value));
    }

    [Fact]
    public void DefinesRgbColorRegister()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("#2;2;100;0;0~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(0xFFFF0000u, image.Palette.Span[2]);
        Assert.All(image.PixelIndices.ToArray(), value => Assert.Equal((ushort)2, value));
    }

    [Fact]
    public void AbbreviatedColorDefinitionDefaultsMissingComponents()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("#2;2;100~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(0xFFFF0000u, image.Palette.Span[2]);
    }

    [Fact]
    public void ColorRegistersPersistAcrossImages()
    {
        var decoder = new SixelDecoder();
        Assert.True(decoder.TryDecode("#5;2;0;100;0~"u8, 7, 1, 0, out _));

        Assert.True(decoder.TryDecode("#5~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(0xFF00FF00u, image.Palette.Span[5]);
    }

    [Fact]
    public void RejectedImageDoesNotMutatePersistentColorRegisters()
    {
        var decoder = new SixelDecoder();
        Assert.False(decoder.TryDecode("#5;2;100;0;0\"1;1;4097;1~"u8, 7, 1, 0, out _));

        Assert.True(decoder.TryDecode("#5~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.NotEqual(0xFFFF0000u, image.Palette.Span[5]);
    }

    [Fact]
    public void ResetRestoresDefaultColorRegisters()
    {
        var decoder = new SixelDecoder();
        Assert.True(decoder.TryDecode("#5;2;100;0;0~"u8, 7, 1, 0, out _));

        decoder.Reset();
        Assert.True(decoder.TryDecode("#5~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.NotEqual(0xFFFF0000u, image.Palette.Span[5]);
    }

    [Fact]
    public void RasterAttributesSetDimensionsAndAspect()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("\"2;1;4;12~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(4, image.Width);
        Assert.Equal(12, image.Height);
        Assert.Equal(2, image.PixelAspectRatio);
    }

    [Fact]
    public void CarriageReturnAndNextLinePositionPixels()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("~~$~-~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(12, image.Height);
        Assert.Equal((ushort)15, image.PixelIndices.Span[0]);
        Assert.Equal((ushort)15, image.PixelIndices.Span[6 * image.Width]);
    }

    [Fact]
    public void TransparentAndOpaqueBackgroundModesDiffer()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("?"u8, 7, 1, 3, out var transparent));
        Assert.True(decoder.TryDecode("?"u8, 7, 0, 3, out var opaque));

        Assert.NotNull(transparent);
        Assert.NotNull(opaque);
        Assert.True(transparent.TransparentBackground);
        Assert.Equal(SixelImage.TransparentColorIndex, transparent.PixelIndices.Span[0]);
        Assert.False(opaque.TransparentBackground);
        Assert.Equal((ushort)3, opaque.PixelIndices.Span[0]);
        Assert.Equal(0u, transparent.ToRgba32()[0]);
        Assert.Equal(opaque.Palette.Span[3], opaque.ToRgba32()[0]);
    }

    [Theory]
    [InlineData("\"1;1;4097;1")]
    [InlineData("\"1;1;1;4097")]
    [InlineData("!65535~")]
    public void RejectsImagesBeyondHardDimensions(string sixel)
    {
        var decoder = new SixelDecoder();

        Assert.False(decoder.TryDecode(Encoding.ASCII.GetBytes(sixel), 7, 1, 0, out _));
    }

    [Fact]
    public void EmptySixelDoesNotProduceAnOverlay()
    {
        var decoder = new SixelDecoder();

        Assert.False(decoder.TryDecode([], 7, 1, 0, out var image));
        Assert.Null(image);
    }

    [Fact]
    public void RejectsExcessiveRepeatedPixelWork()
    {
        var decoder = new SixelDecoder();
        var command = "!4096~$"u8.ToArray();
        var repetitions = (TerminalImageLimits.MaximumSixelPixelWrites / (4096 * 6)) + 1;
        var payload = new byte[command.Length * repetitions];
        for (var offset = 0; offset < payload.Length; offset += command.Length)
        {
            command.CopyTo(payload, offset);
        }

        Assert.False(decoder.TryDecode(payload, 7, 1, 0, out _));
    }

    [Fact]
    public void RepeatAtMaximumWidthSucceedsButOnePixelBeyondDoesNotChangePalette()
    {
        var decoder = new SixelDecoder();
        Assert.True(decoder.TryDecode(
            Encoding.ASCII.GetBytes($"#5;2;100;0;0!{TerminalImageLimits.MaximumPixelDimension}~"),
            7, 1, 0, out var boundary));
        Assert.Equal(TerminalImageLimits.MaximumPixelDimension, boundary!.Width);
        Assert.Equal(6, boundary.Height);
        Assert.Equal(0xFFFF0000u, boundary.Palette.Span[5]);
        Assert.Equal((ushort)5, boundary.PixelIndices.Span[^1]);

        Assert.False(decoder.TryDecode(
            Encoding.ASCII.GetBytes($"#5;2;0;100;0!{TerminalImageLimits.MaximumPixelDimension + 1}~"),
            7, 1, 0, out var rejected));
        Assert.Null(rejected);
        Assert.True(decoder.TryDecode("#5~"u8, 7, 1, 0, out var after));
        Assert.Equal(0xFFFF0000u, after!.Palette.Span[5]);
    }

    [Fact]
    public void FormattedWhitespaceInsideRepeatAndParametersIsIgnored()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("#2;2;100;\r\n0;0!4\r\n ~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(4, image.Width);
        Assert.Equal(0xFFFF0000u, image.Palette.Span[2]);
    }

    [Fact]
    public void LeadingSemicolonInParametersDoesNotShiftIndices()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("\";;5;12#;2;0;100;0~"u8, 0, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(5, image.Width);
        Assert.Equal(12, image.Height);
        Assert.Equal(2, image.PixelAspectRatio);
        Assert.Equal(0xFF00FF00u, image.Palette.Span[0]);
    }

    [Fact]
    public void RasterAttributesDefaultOmittedDenominatorAndIgnoreMidStreamChanges()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("\"3~\"1;1;10;10~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(18, image.Height);
        Assert.Equal(3, image.PixelAspectRatio);
    }

    [Fact]
    public void RejectsExcessiveEmptySixelRepeats()
    {
        var decoder = new SixelDecoder();
        var command = "!4096?$"u8.ToArray();
        var repetitions = (TerminalImageLimits.MaximumSixelPixelWrites / 4096) + 2;
        var payload = new byte[command.Length * repetitions];
        for (var offset = 0; offset < payload.Length; offset += command.Length)
        {
            command.CopyTo(payload, offset);
        }

        Assert.False(decoder.TryDecode(payload, 7, 1, 0, out _));
    }

    [Fact]
    public void ColorRegisterIndexAndHlsHueWrapModuloRange()
    {
        var decoder = new SixelDecoder();

        Assert.True(decoder.TryDecode("#258;1;480;50;100~"u8, 7, 1, 0, out var image));

        Assert.NotNull(image);
        Assert.Equal(0xFFFF0000u, image.Palette.Span[2]);
        Assert.Equal((ushort)2, image.PixelIndices.Span[0]);
    }
}
