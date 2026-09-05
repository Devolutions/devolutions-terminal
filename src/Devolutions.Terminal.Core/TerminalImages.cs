namespace Devolutions.Terminal.Core;

public static class TerminalImageLimits
{
    public const int MaximumDcsPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumInlineImageBytes = 768 * 1024;
    public const int MaximumKittyImageBytes = 32 * 1024 * 1024;
    public const int MaximumPixelDimension = 4096;
    public const int MaximumPixelCount = 16 * 1024 * 1024;
    public const int MaximumSixelPixelWrites = 64 * 1024 * 1024;
    public const int MaximumRetainedImages = 64;
    public const int MaximumRetainedImageBytes = 64 * 1024 * 1024;
}

public enum TerminalImageProtocol : byte
{
    Sixel,
    Iterm2Inline,
    ConEmuInline,
    KittyGraphics,
}

public enum TerminalImageDimensionKind : byte
{
    Auto,
    Cells,
    Pixels,
    Percent,
}

public readonly record struct TerminalImageDimension(TerminalImageDimensionKind Kind, double Value)
{
    public static TerminalImageDimension Auto { get; } = new(TerminalImageDimensionKind.Auto, 0);
}

public sealed record InlineImageMetadata(
    string? Name,
    long? DeclaredSize,
    TerminalImageDimension Width,
    TerminalImageDimension Height,
    bool PreserveAspectRatio);

public sealed class InlineImage
{
    private readonly byte[] _data;

    public InlineImage(InlineImageMetadata metadata, byte[] data)
    {
        Metadata = metadata;
        _data = data;
    }

    public InlineImageMetadata Metadata { get; }
    public ReadOnlyMemory<byte> Data => _data;
    public int EstimatedByteSize => _data.Length;
}

public sealed class SixelImage
{
    public const ushort TransparentColorIndex = 256;

    private readonly ushort[] _indices;
    private readonly uint[] _palette;

    internal SixelImage(
        int width,
        int height,
        int pixelAspectRatio,
        bool transparentBackground,
        int finalCursorRowPixels,
        ushort[] indices,
        uint[] palette)
    {
        Width = width;
        Height = height;
        PixelAspectRatio = pixelAspectRatio;
        TransparentBackground = transparentBackground;
        FinalCursorRowPixels = finalCursorRowPixels;
        _indices = indices;
        _palette = palette;
    }

    public int Width { get; }
    public int Height { get; }
    public int PixelAspectRatio { get; }
    public bool TransparentBackground { get; }
    public int FinalCursorRowPixels { get; }
    public ReadOnlyMemory<ushort> PixelIndices => _indices;
    public ReadOnlyMemory<uint> Palette => _palette;
    public long EstimatedByteSize => ((long)_indices.Length * sizeof(ushort)) + ((long)_palette.Length * sizeof(uint));

    public uint[] ToRgba32()
    {
        var pixels = new uint[_indices.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            var colorIndex = _indices[index];
            pixels[index] = colorIndex == TransparentColorIndex ? 0u : _palette[colorIndex];
        }

        return pixels;
    }
}

/// <summary>
/// Immutable kitty-graphics pixel storage, shared by every placement of the same
/// image id. Either <see cref="Rgba32Pixels"/> (formats 24/32, converted to
/// 4-bytes-per-pixel RGBA) or <see cref="EncodedData"/> (format 100, codec bytes
/// retained for the renderer, mirroring the OSC 1337 contract) is set.
/// </summary>
public sealed class KittyImageData
{
    public KittyImageData(int width, int height, byte[] rgba32Pixels)
    {
        Width = width;
        Height = height;
        Rgba32Pixels = rgba32Pixels;
    }

    public KittyImageData(byte[] encodedData)
    {
        EncodedData = encodedData;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[]? Rgba32Pixels { get; }
    public byte[]? EncodedData { get; }
    public long EstimatedByteSize => Rgba32Pixels?.Length ?? EncodedData?.Length ?? 0;
}

/// <summary>
/// One kitty-graphics placement of a <see cref="KittyImageData"/>. Cell geometry of
/// zero means "natural pixel size"; the renderer applies crop and cell offset.
/// </summary>
public sealed class KittyImage
{
    public KittyImage(uint imageId, uint placementId, KittyImageData data)
    {
        ImageId = imageId;
        PlacementId = placementId;
        Data = data;
    }

    public uint ImageId { get; }
    public uint PlacementId { get; }
    public KittyImageData Data { get; }
    public int Columns { get; init; }
    public int Rows { get; init; }
    public int PixelOffsetX { get; init; }
    public int PixelOffsetY { get; init; }
    public int CropX { get; init; }
    public int CropY { get; init; }
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }
    public int ZIndex { get; init; }
}

public readonly record struct TerminalImageAnchor(long LogicalLineId, int LogicalOffset);

public readonly record struct TerminalImageCellGeometry(
    double CellWidth,
    double CellHeight);

public sealed record TerminalImageOverlay(
    long Id,
    TerminalImageProtocol Protocol,
    bool AlternateBuffer,
    int AnchorColumn,
    int AnchorRow,
    SixelImage? Sixel,
    InlineImage? InlineImage)
{
    public KittyImage? Kitty { get; init; }
    public TerminalImageAnchor LogicalAnchor { get; init; }
    public TerminalImageCellGeometry CellGeometry { get; init; } = new(10, 20);
}
