using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Devolutions.Terminal.Core;

public enum KittyGraphicsAction : byte
{
    Transmit,
    TransmitAndDisplay,
    Put,
    Delete,
    Query,
    Unsupported,
}

/// <summary>
/// One parsed APC G command. Pixel data is still base64-decoded but otherwise raw
/// (possibly zlib-compressed); decoding to <see cref="KittyImageData"/> happens in
/// <see cref="KittyGraphicsDecoder.DecodeImageData"/>.
/// </summary>
public sealed class KittyGraphicsCommand
{
    public KittyGraphicsAction Action { get; set; } = KittyGraphicsAction.TransmitAndDisplay;
    public bool HasExplicitAction { get; set; }
    public char Medium { get; set; } = 'd';
    public int Format { get; set; } = 32;
    public bool Compressed { get; set; }
    public char Compression { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public uint ImageId { get; set; }
    public uint ImageNumber { get; set; }
    public uint PlacementId { get; set; }
    public int Columns { get; set; }
    public int Rows { get; set; }
    public int PixelOffsetX { get; set; }
    public int PixelOffsetY { get; set; }
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }
    public int ZIndex { get; set; }
    public int Quiet { get; set; }
    public bool NoCursorMove { get; set; }
    public char DeleteWhat { get; set; } = 'a';
    public bool MoreChunks { get; set; }
    public byte[] Payload { get; set; } = [];
    internal string TrailingBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Parser for the kitty graphics protocol (APC G). Supports the direct transmission
/// medium only; file/shared-memory media are rejected without I/O, matching the
/// repository's policy for non-inline OSC 1337 transfers. Animation frame control
/// (a=f) and Unicode placeholders are intentionally unsupported.
/// </summary>
internal readonly record struct KittyContinuationChunk(
    bool MoreChunks,
    uint ImageId,
    uint ImageNumber,
    int Quiet,
    bool IsNonContinuationAction);

public static class KittyGraphicsDecoder
{
    private static readonly SearchValues<char> Base64Whitespace = SearchValues.Create(" \t\r\n");
    private static readonly Vector128<byte> RgbToRgbaShuffleMask = Vector128.Create(
        (byte)0, 1, 2, 0xFF,
        3, 4, 5, 0xFF,
        6, 7, 8, 0xFF,
        9, 10, 11, 0xFF);
    private static readonly Vector128<byte> RgbToRgbaAlphaMask = Vector128.Create(
        (byte)0, 0, 0, 0xFF,
        0, 0, 0, 0xFF,
        0, 0, 0, 0xFF,
        0, 0, 0, 0xFF);
    /// <summary>
    /// Splits an APC G body (everything after 'G') at the payload separator and parses
    /// the comma-separated control keys. The payload is base64-decoded per chunk.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<char> body,
        out KittyGraphicsCommand? command,
        out string? error) =>
        TryParseCore(body, ReadOnlySpan<char>.Empty, allowPartialQuantumOnMore: false, out command, out _, out _, out _, out error);

    internal static bool TryParseChunk(
        ReadOnlySpan<char> body,
        ReadOnlySpan<char> base64Carry,
        out KittyGraphicsCommand? command,
        out uint errorImageId,
        out uint errorImageNumber,
        out int errorQuiet,
        out string? error) =>
        TryParseCore(
            body,
            base64Carry,
            allowPartialQuantumOnMore: true,
            out command,
            out errorImageId,
            out errorImageNumber,
            out errorQuiet,
            out error);

    internal static bool TryParseContinuationChunk(
        ReadOnlySpan<char> body,
        KittyGraphicsAction pendingAction,
        Span<char> carryBuffer,
        ref int carryLength,
        List<byte> payloadTarget,
        out KittyContinuationChunk chunk,
        out uint errorImageId,
        out uint errorImageNumber,
        out int errorQuiet,
        out string? error)
    {
        chunk = default;
        error = null;
        var separator = body.IndexOf(';');
        var control = separator >= 0 ? body[..separator] : body;
        var payload = separator >= 0 ? body[(separator + 1)..] : ReadOnlySpan<char>.Empty;

        ScanErrorMetadata(control, out errorImageId, out errorImageNumber, out errorQuiet);

        var moreChunks = false;
        uint imageId = 0;
        uint imageNumber = 0;
        var quiet = 0;
        var remaining = control;
        while (!remaining.IsEmpty)
        {
            var comma = remaining.IndexOf(',');
            var part = comma >= 0 ? remaining[..comma] : remaining;
            remaining = comma >= 0 ? remaining[(comma + 1)..] : ReadOnlySpan<char>.Empty;
            if (part.IsEmpty)
            {
                continue;
            }

            if (part.IndexOf('=') != 1)
            {
                error = "EINVAL: malformed control key";
                return false;
            }

            var key = part[0];
            var value = part[2..];
            if (value.IsEmpty || value.Contains('='))
            {
                error = "EINVAL: malformed control value";
                return false;
            }

            switch (key)
            {
                case 'a':
                    var action = value.Length == 1 ? value[0] switch
                    {
                        't' => KittyGraphicsAction.Transmit,
                        'T' => KittyGraphicsAction.TransmitAndDisplay,
                        'p' => KittyGraphicsAction.Put,
                        'd' => KittyGraphicsAction.Delete,
                        'q' => KittyGraphicsAction.Query,
                        _ => KittyGraphicsAction.Unsupported,
                    } : KittyGraphicsAction.Unsupported;
                    if (action is KittyGraphicsAction.Delete or KittyGraphicsAction.Put or KittyGraphicsAction.Unsupported ||
                        action != pendingAction)
                    {
                        chunk = new KittyContinuationChunk(false, 0, 0, 0, IsNonContinuationAction: true);
                        return true;
                    }

                    break;
                case 'f':
                case 's':
                case 'v':
                case 't':
                case 'o':
                    // New transmission header keys indicate the previous m=1 transfer was
                    // abandoned mid-stream (e.g. an oversized chunk was dropped) and a new
                    // command has begun.
                    chunk = new KittyContinuationChunk(false, 0, 0, 0, IsNonContinuationAction: true);
                    return true;
                case 'i':
                    if (!TryUInt(value, out imageId))
                    {
                        error = "EINVAL: bad image id";
                        return false;
                    }

                    break;
                case 'I':
                    if (!TryUInt(value, out imageNumber))
                    {
                        error = "EINVAL: bad image number";
                        return false;
                    }

                    break;
                case 'm':
                    if (value.SequenceEqual("0"))
                    {
                        moreChunks = false;
                    }
                    else if (value.SequenceEqual("1"))
                    {
                        moreChunks = true;
                    }
                    else
                    {
                        error = "EINVAL: bad chunk flag";
                        return false;
                    }

                    break;
                case 'q':
                    if (!TryUInt(value, out var parsedQuiet) || parsedQuiet > int.MaxValue)
                    {
                        error = "EINVAL: bad quiet flag";
                        return false;
                    }

                    quiet = (int)parsedQuiet;
                    break;
            }
        }

        if (carryLength > 0 || !payload.IsEmpty)
        {
            if (!TryAppendChunkBase64(carryBuffer, ref carryLength, payload, moreChunks, payloadTarget))
            {
                error = "EINVAL: payload is not valid base64";
                return false;
            }
        }

        chunk = new KittyContinuationChunk(moreChunks, imageId, imageNumber, quiet, IsNonContinuationAction: false);
        return true;
    }

    private static bool TryParseCore(
        ReadOnlySpan<char> body,
        ReadOnlySpan<char> base64Carry,
        bool allowPartialQuantumOnMore,
        out KittyGraphicsCommand? command,
        out uint errorImageId,
        out uint errorImageNumber,
        out int errorQuiet,
        out string? error)
    {
        command = null;
        error = null;
        errorImageId = 0;
        errorImageNumber = 0;
        errorQuiet = 0;

        var separator = body.IndexOf(';');
        var control = separator >= 0 ? body[..separator] : body;
        var payload = separator >= 0 ? body[(separator + 1)..] : ReadOnlySpan<char>.Empty;

        // Pre-scan i, I, and q so malformed commands still honor quiet flags and echo ids.
        ScanErrorMetadata(control, out errorImageId, out errorImageNumber, out errorQuiet);

        var parsed = new KittyGraphicsCommand();
        var remainingControl = control;
        while (!remainingControl.IsEmpty)
        {
            var comma = remainingControl.IndexOf(',');
            var part = comma >= 0 ? remainingControl[..comma] : remainingControl;
            remainingControl = comma >= 0 ? remainingControl[(comma + 1)..] : ReadOnlySpan<char>.Empty;
            if (part.IsEmpty)
            {
                continue;
            }

            var equals = part.IndexOf('=');
            if (equals != 1)
            {
                error = "EINVAL: malformed control key";
                return false;
            }

            var key = part[0];
            var value = part[2..];
            if (value.IsEmpty || value.Contains('='))
            {
                error = "EINVAL: malformed control value";
                return false;
            }

            switch (key)
            {
                case 'a':
                    parsed.HasExplicitAction = true;
                    parsed.Action = value.Length == 1 ? value[0] switch
                    {
                        't' => KittyGraphicsAction.Transmit,
                        'T' => KittyGraphicsAction.TransmitAndDisplay,
                        'p' => KittyGraphicsAction.Put,
                        'd' => KittyGraphicsAction.Delete,
                        'q' => KittyGraphicsAction.Query,
                        _ => KittyGraphicsAction.Unsupported,
                    } : KittyGraphicsAction.Unsupported;
                    break;
                case 't':
                    if (value.Length != 1)
                    {
                        error = "EINVAL: bad transmission medium";
                        return false;
                    }

                    parsed.Medium = value[0];
                    break;
                case 'f':
                    if (!TryNonNegativeInt(value, out var parsedFormat))
                    {
                        error = "EINVAL: bad format";
                        return false;
                    }

                    parsed.Format = parsedFormat;
                    break;
                case 'o':
                    if (value.SequenceEqual("z"))
                    {
                        parsed.Compressed = true;
                        parsed.Compression = 'z';
                    }
                    else
                    {
                        parsed.Compressed = false;
                        parsed.Compression = value.Length == 1 ? value[0] : '?';
                    }

                    break;
                case 's':
                    if (!TryNonNegativeInt(value, out var parsedSourceWidth))
                    {
                        error = "EINVAL: bad width";
                        return false;
                    }

                    parsed.SourceWidth = parsedSourceWidth;
                    break;
                case 'v':
                    if (!TryNonNegativeInt(value, out var parsedSourceHeight))
                    {
                        error = "EINVAL: bad height";
                        return false;
                    }

                    parsed.SourceHeight = parsedSourceHeight;
                    break;
                case 'i':
                    if (!TryUInt(value, out var parsedImageId))
                    {
                        error = "EINVAL: bad image id";
                        return false;
                    }

                    parsed.ImageId = parsedImageId;
                    break;
                case 'I':
                    if (!TryUInt(value, out var parsedImageNumber))
                    {
                        error = "EINVAL: bad image number";
                        return false;
                    }

                    parsed.ImageNumber = parsedImageNumber;
                    break;
                case 'p':
                    if (!TryUInt(value, out var parsedPlacementId))
                    {
                        error = "EINVAL: bad placement id";
                        return false;
                    }

                    parsed.PlacementId = parsedPlacementId;
                    break;
                case 'c':
                    if (!TryNonNegativeInt(value, out var parsedColumns))
                    {
                        error = "EINVAL: bad columns";
                        return false;
                    }

                    parsed.Columns = parsedColumns;
                    break;
                case 'r':
                    if (!TryNonNegativeInt(value, out var parsedRows))
                    {
                        error = "EINVAL: bad rows";
                        return false;
                    }

                    parsed.Rows = parsedRows;
                    break;
                case 'x':
                    if (!TryNonNegativeInt(value, out var parsedPixelOffsetX))
                    {
                        error = "EINVAL: bad x offset";
                        return false;
                    }

                    parsed.PixelOffsetX = parsedPixelOffsetX;
                    break;
                case 'y':
                    if (!TryNonNegativeInt(value, out var parsedPixelOffsetY))
                    {
                        error = "EINVAL: bad y offset";
                        return false;
                    }

                    parsed.PixelOffsetY = parsedPixelOffsetY;
                    break;
                case 'X':
                    if (!TryNonNegativeInt(value, out var parsedCropX))
                    {
                        error = "EINVAL: bad crop x";
                        return false;
                    }

                    parsed.CropX = parsedCropX;
                    break;
                case 'Y':
                    if (!TryNonNegativeInt(value, out var parsedCropY))
                    {
                        error = "EINVAL: bad crop y";
                        return false;
                    }

                    parsed.CropY = parsedCropY;
                    break;
                case 'w':
                    if (!TryNonNegativeInt(value, out var parsedCropWidth))
                    {
                        error = "EINVAL: bad crop width";
                        return false;
                    }

                    parsed.CropWidth = parsedCropWidth;
                    break;
                case 'h':
                    if (!TryNonNegativeInt(value, out var parsedCropHeight))
                    {
                        error = "EINVAL: bad crop height";
                        return false;
                    }

                    parsed.CropHeight = parsedCropHeight;
                    break;
                case 'z':
                    if (!TryInt(value, out var parsedZIndex))
                    {
                        error = "EINVAL: bad z-index";
                        return false;
                    }

                    parsed.ZIndex = parsedZIndex;
                    break;
                case 'q':
                    if (!TryNonNegativeInt(value, out var parsedQuiet))
                    {
                        error = "EINVAL: bad quiet flag";
                        return false;
                    }

                    parsed.Quiet = parsedQuiet;
                    break;
                case 'C':
                    if (value.SequenceEqual("0"))
                    {
                        parsed.NoCursorMove = false;
                    }
                    else if (value.SequenceEqual("1"))
                    {
                        parsed.NoCursorMove = true;
                    }
                    else
                    {
                        error = "EINVAL: bad cursor movement flag";
                        return false;
                    }

                    break;
                case 'm':
                    if (value.SequenceEqual("0"))
                    {
                        parsed.MoreChunks = false;
                    }
                    else if (value.SequenceEqual("1"))
                    {
                        parsed.MoreChunks = true;
                    }
                    else
                    {
                        error = "EINVAL: bad chunk flag";
                        return false;
                    }

                    break;
                case 'd':
                    if (value.Length != 1)
                    {
                        error = "EINVAL: bad delete target";
                        return false;
                    }

                    parsed.DeleteWhat = value[0];
                    break;
                case 'S':
                case 'O':
                case 'u':
                case 'U':
                    // Animation frame size/offset and Unicode placeholders: parsed
                    // and ignored; animation actions themselves are unsupported.
                    break;
                default:
                    // Unknown keys are ignored for forward compatibility.
                    break;
            }
        }

        var effectiveCarry = parsed.HasExplicitAction &&
                             parsed.Action is KittyGraphicsAction.Delete or KittyGraphicsAction.Put or KittyGraphicsAction.Unsupported
            ? ReadOnlySpan<char>.Empty
            : base64Carry;
        if (!effectiveCarry.IsEmpty || !payload.IsEmpty)
        {
            if (!TryDecodeChunkBase64(
                    effectiveCarry,
                    payload,
                    allowPartialQuantumOnMore && parsed.MoreChunks,
                    out var decodedPayload,
                    out var trailingBase64))
            {
                error = "EINVAL: payload is not valid base64";
                return false;
            }

            parsed.Payload = decodedPayload;
            parsed.TrailingBase64 = trailingBase64;
        }

        command = parsed;
        return true;
    }

    /// <summary>
    /// Validates a transmission and decodes its assembled payload into
    /// <see cref="KittyImageData"/>. Raw formats (24/32) become 4-byte RGBA;
    /// format 100 retains codec bytes for the renderer (the OSC 1337 contract).
    /// </summary>
    public static bool TryDecodeImageData(
        KittyGraphicsCommand command,
        byte[] payload,
        out KittyImageData? data,
        out string? error) =>
        TryDecodeImageData(command, payload, takePayloadOwnership: false, out data, out error);

    internal static bool TryDecodeImageData(
        KittyGraphicsCommand command,
        byte[] payload,
        bool takePayloadOwnership,
        out KittyImageData? data,
        out string? error)
    {
        data = null;
        error = null;

        if (command.Medium != 'd')
        {
            error = "ENOTSUP: only the direct transmission medium is supported";
            return false;
        }

        if (command.Compression is not ('\0' or 'z'))
        {
            error = "ENOTSUP: unsupported compression";
            return false;
        }

        if (command.Format is not (24 or 32 or 100))
        {
            error = "EINVAL: unsupported pixel format";
            return false;
        }

        if (command.Format == 100)
        {
            var encodedBytes = payload;
            if (command.Compressed)
            {
                if (!TryInflate(payload, expectedExactLength: -1, out encodedBytes, out var tooManyEncoded))
                {
                    error = tooManyEncoded
                        ? "ETOOMANY: image data exceeds the size limit"
                        : "EINVAL: zlib payload did not decompress";
                    return false;
                }
            }
            else if (encodedBytes.Length > TerminalImageLimits.MaximumKittyImageBytes)
            {
                error = "ETOOMANY: image data exceeds the size limit";
                return false;
            }

            if (encodedBytes.Length == 0)
            {
                error = "ENODATA: empty image payload";
                return false;
            }

            data = new KittyImageData(encodedBytes);
            return true;
        }

        var bytesPerPixel = command.Format == 32 ? 4 : 3;
        var width = command.SourceWidth;
        var height = command.SourceHeight;
        if (width <= 0 || height <= 0)
        {
            error = "EINVAL: raw formats require s and v pixel dimensions";
            return false;
        }

        if (width > TerminalImageLimits.MaximumPixelDimension ||
            height > TerminalImageLimits.MaximumPixelDimension ||
            (long)width * height > TerminalImageLimits.MaximumPixelCount)
        {
            error = "ETOOMANY: image dimensions exceed the pixel limits";
            return false;
        }

        var exactExpected = (long)width * height * bytesPerPixel;
        var bytes = payload;
        if (command.Compressed)
        {
            var inflateExpected = exactExpected <= TerminalImageLimits.MaximumKittyImageBytes
                ? (int)exactExpected
                : -1;
            if (!TryInflate(payload, inflateExpected, out bytes, out var tooManyRaw))
            {
                error = tooManyRaw
                    ? "ETOOMANY: image data exceeds the size limit"
                    : "EINVAL: zlib payload did not decompress";
                return false;
            }
        }

        if (bytes.Length > TerminalImageLimits.MaximumKittyImageBytes)
        {
            error = "ETOOMANY: image data exceeds the size limit";
            return false;
        }

        if (bytes.LongLength != exactExpected)
        {
            error = "EINVAL: payload length does not match s*v*format";
            return false;
        }

        if (bytesPerPixel == 4)
        {
            var rgbaPixels = takePayloadOwnership || command.Compressed
                ? bytes
                : bytes.AsSpan().ToArray();
            data = new KittyImageData(width, height, rgbaPixels);
            return true;
        }

        var rgba = new byte[width * height * 4];
        ExpandRgb24ToRgba32(bytes, rgba);
        data = new KittyImageData(width, height, rgba) { IsOpaque = true };
        return true;
    }

    private static void ExpandRgb24ToRgba32(ReadOnlySpan<byte> rgb, Span<byte> rgba)
    {
        var source = 0;
        var target = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            while (source + 16 <= rgb.Length)
            {
                var chunk = Vector128.Create(rgb.Slice(source, 16));
                var expanded = Vector128.Shuffle(chunk, RgbToRgbaShuffleMask) | RgbToRgbaAlphaMask;
                expanded.CopyTo(rgba.Slice(target, 16));
                source += 12;
                target += 16;
            }
        }

        while (target < rgba.Length)
        {
            rgba[target] = rgb[source];
            rgba[target + 1] = rgb[source + 1];
            rgba[target + 2] = rgb[source + 2];
            rgba[target + 3] = 0xFF;
            source += 3;
            target += 4;
        }
    }

    private static bool TryInflate(
        byte[] payload,
        int expectedExactLength,
        out byte[] bytes,
        out bool exceededLimit)
    {
        exceededLimit = false;
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var input = new MemoryStream(payload);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);

            if (expectedExactLength >= 0)
            {
                var exact = new byte[expectedExactLength];
                var offset = 0;
                while (offset < expectedExactLength)
                {
                    var count = zlib.Read(exact, offset, expectedExactLength - offset);
                    if (count == 0)
                    {
                        // Fewer bytes than expected: return truncated buffer so caller reports EINVAL vs ETOOMANY accurately.
                        bytes = exact.AsSpan(0, offset).ToArray();
                        return true;
                    }

                    offset += count;
                }

                var extra = zlib.Read(rented, 0, Math.Min(rented.Length, (TerminalImageLimits.MaximumKittyImageBytes - expectedExactLength) + 1));
                if (extra == 0)
                {
                    bytes = exact;
                    return true;
                }

                // More bytes than s*v*bpp: keep reading up to MaximumKittyImageBytes + 1 to classify ETOOMANY vs EINVAL.
                var total = (long)expectedExactLength + extra;
                while (total <= TerminalImageLimits.MaximumKittyImageBytes)
                {
                    var more = zlib.Read(rented, 0, (int)Math.Min(rented.Length, (TerminalImageLimits.MaximumKittyImageBytes - total) + 1));
                    if (more == 0)
                    {
                        break;
                    }

                    total += more;
                }

                if (total > TerminalImageLimits.MaximumKittyImageBytes)
                {
                    exceededLimit = true;
                    bytes = [];
                    return false;
                }

                // Valid zlib stream within byte cap, wrong pixel length -> caller reports EINVAL length mismatch.
                bytes = [];
                return true;
            }

            using var output = new MemoryStream();
            var remaining = (long)TerminalImageLimits.MaximumKittyImageBytes + 1;
            int read;
            while (remaining > 0 && (read = zlib.Read(rented, 0, (int)Math.Min(rented.Length, remaining))) > 0)
            {
                output.Write(rented, 0, read);
                remaining -= read;
            }

            if (output.Length > TerminalImageLimits.MaximumKittyImageBytes)
            {
                exceededLimit = true;
                bytes = [];
                return false;
            }

            bytes = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            bytes = [];
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ScanErrorMetadata(
        ReadOnlySpan<char> control,
        out uint imageId,
        out uint imageNumber,
        out int quiet)
    {
        imageId = 0;
        imageNumber = 0;
        quiet = 0;
        var remaining = control;
        while (!remaining.IsEmpty)
        {
            var comma = remaining.IndexOf(',');
            var part = comma >= 0 ? remaining[..comma] : remaining;
            remaining = comma >= 0 ? remaining[(comma + 1)..] : ReadOnlySpan<char>.Empty;
            if (part.Length < 3 || part[1] != '=')
            {
                continue;
            }

            var value = part[2..];
            switch (part[0])
            {
                case 'i' when TryUInt(value, out var parsedId):
                    imageId = parsedId;
                    break;
                case 'I' when TryUInt(value, out var parsedNumber):
                    imageNumber = parsedNumber;
                    break;
                case 'q' when TryUInt(value, out var parsedQuiet) && parsedQuiet <= int.MaxValue:
                    quiet = (int)parsedQuiet;
                    break;
            }
        }
    }

    private static bool TryDecodeChunkBase64(
        ReadOnlySpan<char> carry,
        ReadOnlySpan<char> payload,
        bool allowTrailingRemainder,
        out byte[] decoded,
        out string trailing)
    {
        decoded = [];
        trailing = string.Empty;

        if (carry.IsEmpty && payload.IndexOfAny(Base64Whitespace) < 0)
        {
            if (payload.IsEmpty)
            {
                return true;
            }

            var fastDecodeLength = payload.Length;
            if (allowTrailingRemainder)
            {
                var remainder = fastDecodeLength & 3;
                fastDecodeLength -= remainder;
                if (remainder > 0)
                {
                    var tail = payload.Slice(fastDecodeLength, remainder);
                    for (var i = 0; i < tail.Length; i++)
                    {
                        if (!IsValidBase64Char(tail[i]))
                        {
                            return false;
                        }
                    }

                    trailing = new string(tail);
                }
            }
            else if ((fastDecodeLength & 3) != 0)
            {
                return false;
            }

            if (fastDecodeLength == 0)
            {
                return true;
            }

            var maxBytes = (fastDecodeLength / 4) * 3;
            var rentedBytes = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                if (!Convert.TryFromBase64Chars(payload[..fastDecodeLength], rentedBytes, out var written))
                {
                    return false;
                }

                decoded = rentedBytes.AsSpan(0, written).ToArray();
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBytes);
            }
        }

        var maxChars = carry.Length + payload.Length;
        char[]? rentedChars = null;
        Span<char> clean = maxChars <= 256
            ? stackalloc char[maxChars]
            : (rentedChars = ArrayPool<char>.Shared.Rent(maxChars));
        try
        {
            var length = 0;
            for (var i = 0; i < carry.Length; i++)
            {
                var ch = carry[i];
                if (ch is ' ' or '\t' or '\r' or '\n')
                {
                    continue;
                }

                if (!IsValidBase64Char(ch))
                {
                    return false;
                }

                clean[length++] = ch;
            }

            for (var i = 0; i < payload.Length; i++)
            {
                var ch = payload[i];
                if (ch is ' ' or '\t' or '\r' or '\n')
                {
                    continue;
                }

                if (!IsValidBase64Char(ch))
                {
                    return false;
                }

                clean[length++] = ch;
            }

            if (length == 0)
            {
                return true;
            }

            var decodeLength = length;
            if (allowTrailingRemainder)
            {
                var remainder = length & 3;
                decodeLength = length - remainder;
                if (remainder > 0)
                {
                    trailing = new string(clean.Slice(decodeLength, remainder));
                }
            }
            else if ((length & 3) != 0)
            {
                return false;
            }

            if (decodeLength == 0)
            {
                return true;
            }

            var maxDecoded = (decodeLength / 4) * 3;
            var rentedOut = ArrayPool<byte>.Shared.Rent(maxDecoded);
            try
            {
                if (!Convert.TryFromBase64Chars(clean[..decodeLength], rentedOut, out var written))
                {
                    return false;
                }

                decoded = rentedOut.AsSpan(0, written).ToArray();
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedOut);
            }
        }
        finally
        {
            if (rentedChars is not null)
            {
                ArrayPool<char>.Shared.Return(rentedChars);
            }
        }
    }

    private static bool TryAppendChunkBase64(
        Span<char> carryBuffer,
        ref int carryLength,
        ReadOnlySpan<char> payload,
        bool allowTrailingRemainder,
        List<byte> target)
    {
        if (carryLength == 0 && payload.IndexOfAny(Base64Whitespace) < 0)
        {
            var fastDecodeLength = payload.Length;
            if (allowTrailingRemainder)
            {
                var remainder = fastDecodeLength & 3;
                fastDecodeLength -= remainder;
                if (remainder > 0)
                {
                    var tail = payload.Slice(fastDecodeLength, remainder);
                    for (var i = 0; i < tail.Length; i++)
                    {
                        if (!IsValidBase64Char(tail[i]))
                        {
                            return false;
                        }

                        carryBuffer[i] = tail[i];
                    }

                    carryLength = remainder;
                }
            }
            else if ((fastDecodeLength & 3) != 0)
            {
                return false;
            }

            if (fastDecodeLength == 0)
            {
                return true;
            }

            return TryAppendDecodedQuanta(payload[..fastDecodeLength], target);
        }

        var maxChars = carryLength + payload.Length;
        char[]? rentedChars = null;
        Span<char> clean = maxChars <= 256
            ? stackalloc char[maxChars]
            : (rentedChars = ArrayPool<char>.Shared.Rent(maxChars));
        try
        {
            var length = 0;
            for (var i = 0; i < carryLength; i++)
            {
                clean[length++] = carryBuffer[i];
            }

            for (var i = 0; i < payload.Length; i++)
            {
                var ch = payload[i];
                if (ch is ' ' or '\t' or '\r' or '\n')
                {
                    continue;
                }

                if (!IsValidBase64Char(ch))
                {
                    return false;
                }

                clean[length++] = ch;
            }

            carryLength = 0;
            if (length == 0)
            {
                return true;
            }

            var decodeLength = length;
            if (allowTrailingRemainder)
            {
                var remainder = length & 3;
                decodeLength = length - remainder;
                if (remainder > 0)
                {
                    clean.Slice(decodeLength, remainder).CopyTo(carryBuffer);
                    carryLength = remainder;
                }
            }
            else if ((length & 3) != 0)
            {
                return false;
            }

            return decodeLength == 0 || TryAppendDecodedQuanta(clean[..decodeLength], target);
        }
        finally
        {
            if (rentedChars is not null)
            {
                ArrayPool<char>.Shared.Return(rentedChars);
            }
        }
    }

    private static bool TryAppendDecodedQuanta(ReadOnlySpan<char> alignedBase64, List<byte> target)
    {
        var maxBytes = (alignedBase64.Length / 4) * 3;
        var oldCount = target.Count;
        target.EnsureCapacity(oldCount + maxBytes);
        CollectionsMarshal.SetCount(target, oldCount + maxBytes);
        var dest = CollectionsMarshal.AsSpan(target).Slice(oldCount, maxBytes);
        if (!Convert.TryFromBase64Chars(alignedBase64, dest, out var written))
        {
            CollectionsMarshal.SetCount(target, oldCount);
            return false;
        }

        CollectionsMarshal.SetCount(target, oldCount + written);
        return true;
    }

    private static bool IsValidBase64Char(char ch) =>
        ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=';

    private static bool TryNonNegativeInt(ReadOnlySpan<char> value, out int result)
    {
        if (!TryUInt(value, out var parsed) || parsed > int.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (int)parsed;
        return true;
    }

    private static bool TryInt(ReadOnlySpan<char> value, out int result)
    {
        result = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        var negative = false;
        var index = 0;
        if (value[0] == '-')
        {
            negative = true;
            index = 1;
        }

        if (index >= value.Length)
        {
            return false;
        }

        long acc = 0;
        var limit = negative ? -(long)int.MinValue : int.MaxValue;
        for (; index < value.Length; index++)
        {
            var digit = value[index] - '0';
            if ((uint)digit > 9)
            {
                return false;
            }

            acc = (acc * 10) + digit;
            if (acc > limit)
            {
                return false;
            }
        }

        result = negative ? (int)-acc : (int)acc;
        return true;
    }

    private static bool TryUInt(ReadOnlySpan<char> value, out uint result)
    {
        result = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        ulong acc = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var digit = value[index] - '0';
            if ((uint)digit > 9)
            {
                return false;
            }

            acc = (acc * 10) + (uint)digit;
            if (acc > uint.MaxValue)
            {
                return false;
            }
        }

        result = (uint)acc;
        return true;
    }
}
