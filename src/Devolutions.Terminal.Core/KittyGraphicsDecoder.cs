using System.Globalization;
using System.IO.Compression;

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
    public char Medium { get; set; } = 'd';
    public int Format { get; set; } = 32;
    public bool Compressed { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public uint ImageId { get; set; }
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
}

/// <summary>
/// Parser for the kitty graphics protocol (APC G). Supports the direct transmission
/// medium only; file/shared-memory media are rejected without I/O, matching the
/// repository's policy for non-inline OSC 1337 transfers. Animation frame control
/// (a=f) and Unicode placeholders are intentionally unsupported.
/// </summary>
public static class KittyGraphicsDecoder
{
    /// <summary>
    /// Splits an APC G body (everything after 'G') at the payload separator and parses
    /// the comma-separated control keys. The payload is base64-decoded per chunk.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<char> body,
        out KittyGraphicsCommand? command,
        out string? error)
    {
        command = null;
        error = null;

        var separator = body.IndexOf(';');
        var control = separator >= 0 ? body[..separator] : body;
        var payload = separator >= 0 ? body[(separator + 1)..] : ReadOnlySpan<char>.Empty;

        var parsed = new KittyGraphicsCommand();
        foreach (var pair in control.Split(','))
        {
            var part = control[pair];
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
            switch (key)
            {
                case 'a':
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
                    parsed.Medium = value.Length == 1 ? value[0] : '\0';
                    break;
                case 'f':
                    if (!TryInt(value, out var parsedFormat))
                    {
                        error = "EINVAL: bad format";
                        return false;
                    }

                    parsed.Format = parsedFormat;
                    break;
                case 'o':
                    parsed.Compressed = value.SequenceEqual("z");
                    break;
                case 's':
                    if (!TryInt(value, out var parsedSourceWidth))
                    {
                        error = "EINVAL: bad width";
                        return false;
                    }

                    parsed.SourceWidth = parsedSourceWidth;
                    break;
                case 'v':
                    if (!TryInt(value, out var parsedSourceHeight))
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
                    // Terminal-assigned image number; accepted and ignored.
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
                    if (!TryInt(value, out var parsedColumns))
                    {
                        error = "EINVAL: bad columns";
                        return false;
                    }

                    parsed.Columns = parsedColumns;
                    break;
                case 'r':
                    if (!TryInt(value, out var parsedRows))
                    {
                        error = "EINVAL: bad rows";
                        return false;
                    }

                    parsed.Rows = parsedRows;
                    break;
                case 'x':
                    if (!TryInt(value, out var parsedPixelOffsetX))
                    {
                        error = "EINVAL: bad x offset";
                        return false;
                    }

                    parsed.PixelOffsetX = parsedPixelOffsetX;
                    break;
                case 'y':
                    if (!TryInt(value, out var parsedPixelOffsetY))
                    {
                        error = "EINVAL: bad y offset";
                        return false;
                    }

                    parsed.PixelOffsetY = parsedPixelOffsetY;
                    break;
                case 'X':
                    if (!TryInt(value, out var parsedCropX))
                    {
                        error = "EINVAL: bad crop x";
                        return false;
                    }

                    parsed.CropX = parsedCropX;
                    break;
                case 'Y':
                    if (!TryInt(value, out var parsedCropY))
                    {
                        error = "EINVAL: bad crop y";
                        return false;
                    }

                    parsed.CropY = parsedCropY;
                    break;
                case 'w':
                    if (!TryInt(value, out var parsedCropWidth))
                    {
                        error = "EINVAL: bad crop width";
                        return false;
                    }

                    parsed.CropWidth = parsedCropWidth;
                    break;
                case 'h':
                    if (!TryInt(value, out var parsedCropHeight))
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
                    if (!TryInt(value, out var parsedQuiet))
                    {
                        error = "EINVAL: bad quiet flag";
                        return false;
                    }

                    parsed.Quiet = parsedQuiet;
                    break;
                case 'C':
                    parsed.NoCursorMove = value.SequenceEqual("1");
                    break;
                case 'm':
                    parsed.MoreChunks = value.SequenceEqual("1");
                    break;
                case 'd':
                    parsed.DeleteWhat = value.Length == 1 ? value[0] : '\0';
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

        if (!payload.IsEmpty)
        {
            try
            {
                parsed.Payload = Convert.FromBase64String(payload.ToString());
            }
            catch (FormatException)
            {
                error = "EINVAL: payload is not valid base64";
                return false;
            }
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
        out string? error)
    {
        data = null;
        error = null;

        if (command.Medium != 'd')
        {
            error = "ENOTSUP: only the direct transmission medium is supported";
            return false;
        }

        if (command.Format is not (24 or 32 or 100))
        {
            error = "EINVAL: unsupported pixel format";
            return false;
        }

        var bytes = payload;
        if (command.Compressed)
        {
            if (!TryInflate(payload, out bytes))
            {
                error = "EINVAL: zlib payload did not decompress";
                return false;
            }
        }

        if (bytes.Length > TerminalImageLimits.MaximumKittyImageBytes)
        {
            error = "ETOOMANY: image data exceeds the size limit";
            return false;
        }

        if (command.Format == 100)
        {
            if (bytes.Length == 0)
            {
                error = "ENODATA: empty image payload";
                return false;
            }

            data = new KittyImageData(bytes);
            return true;
        }

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

        var bytesPerPixel = command.Format == 32 ? 4 : 3;
        var expected = (long)width * height * bytesPerPixel;
        if (bytes.LongLength != expected)
        {
            error = "EINVAL: payload length does not match s*v*format";
            return false;
        }

        var rgba = new byte[width * height * 4];
        if (bytesPerPixel == 4)
        {
            Array.Copy(bytes, rgba, rgba.Length);
        }
        else
        {
            for (int source = 0, target = 0; target < rgba.Length; source += 3, target += 4)
            {
                rgba[target] = bytes[source];
                rgba[target + 1] = bytes[source + 1];
                rgba[target + 2] = bytes[source + 2];
                rgba[target + 3] = 0xFF;
            }
        }

        data = new KittyImageData(width, height, rgba);
        return true;
    }

    private static bool TryInflate(byte[] payload, out byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(payload);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var remaining = (long)TerminalImageLimits.MaximumKittyImageBytes + 1;
            var buffer = new byte[64 * 1024];
            int read;
            while (remaining > 0 && (read = zlib.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
            {
                output.Write(buffer, 0, read);
                remaining -= read;
            }

            bytes = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            bytes = [];
            return false;
        }
    }

    private static bool TryInt(ReadOnlySpan<char> value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryUInt(ReadOnlySpan<char> value, out uint result) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}
