using System.Buffers;
using System.Numerics;

namespace Devolutions.Terminal.Core;

public sealed class SixelDecoder
{
    private const int MaximumColors = 256;
    private const int MaximumAspectRatio = 20;

    private static readonly uint[] DefaultPalette = CreateDefaultPalette();
    private static readonly byte[] PercentToByteTable = CreatePercentToByteTable();

    private readonly uint[] _palette = (uint[])DefaultPalette.Clone();
    private readonly uint[] _workingPalette = new uint[MaximumColors];

    public void Reset() => DefaultPalette.AsSpan().CopyTo(_palette);

    public bool TryDecode(
        ReadOnlySpan<byte> data,
        int macroParameter,
        int backgroundSelect,
        int backgroundColor,
        out SixelImage? image) =>
        TryDecode(data, macroParameter, backgroundSelect, backgroundColor, usePrivatePalette: false, out image);

    public bool TryDecode(
        ReadOnlySpan<byte> data,
        int macroParameter,
        int backgroundSelect,
        int backgroundColor,
        bool usePrivatePalette,
        out SixelImage? image)
    {
        image = null;
        if (data.Length > TerminalImageLimits.MaximumDcsPayloadBytes)
        {
            return false;
        }

        (usePrivatePalette ? DefaultPalette : _palette).AsSpan().CopyTo(_workingPalette);
        using var state = new DecodeState(
            _workingPalette,
            MacroAspectRatio(macroParameter),
            backgroundSelect == 1,
            backgroundColor);
        Span<int> parameters = stackalloc int[5];
        var offset = 0;
        while (offset < data.Length)
        {
            var value = data[offset++];
            if (value is >= (byte)'?' and <= (byte)'~')
            {
                var run = 1;
                while (offset < data.Length && data[offset] == value)
                {
                    offset++;
                    run++;
                }

                if (!state.WriteSixel(value - '?', run))
                {
                    return false;
                }

                continue;
            }

            switch (value)
            {
                case (byte)'!':
                    {
                        var parameterCount = ReadParameters(data, ref offset, parameters[..1]);
                        var repeat = Parameter(parameters[..parameterCount], 0, 1);
                        while (offset < data.Length && IsIgnoredByte(data[offset]))
                        {
                            offset++;
                        }

                        if (offset < data.Length && data[offset] is >= (byte)'?' and <= (byte)'~')
                        {
                            if (!state.WriteSixel(data[offset++] - '?', Math.Max(1, repeat)))
                            {
                                return false;
                            }
                        }

                        break;
                    }
                case (byte)'#':
                    {
                        var parameterCount = ReadParameters(data, ref offset, parameters);
                        state.SelectColor(parameters[..parameterCount]);
                        break;
                    }
                case (byte)'"':
                    {
                        var parameterCount = ReadParameters(data, ref offset, parameters[..4]);
                        if (!state.SetRasterAttributes(parameters[..parameterCount]))
                        {
                            return false;
                        }

                        break;
                    }
                case (byte)'$':
                    state.CarriageReturn();
                    break;
                case (byte)'-':
                    if (!state.NextLine())
                    {
                        return false;
                    }

                    break;
            }
        }

        if (!state.TryCreateImage(out image))
        {
            return false;
        }

        if (!usePrivatePalette)
        {
            _workingPalette.AsSpan().CopyTo(_palette);
        }

        return true;
    }

    private sealed class DecodeState : IDisposable
    {
        private readonly uint[] _palette;
        private readonly bool _transparentBackground;
        private ushort[] _pixels = [];
        private bool _pixelsRented;
        private int _capacityWidth;
        private int _capacityHeight;
        private int _cursorX;
        private int _cursorY;
        private int _imageWidth;
        private int _imageHeight;
        private int _rasterWidth;
        private int _rasterHeight;
        private int _aspectRatio;
        private ushort _foreground = 15;
        private readonly ushort _background;
        private bool _hasSixelData;
        private long _remainingPixelWrites = TerminalImageLimits.MaximumSixelPixelWrites;

        public DecodeState(uint[] palette, int aspectRatio, bool transparentBackground, int backgroundColor)
        {
            _palette = palette;
            _aspectRatio = aspectRatio;
            _transparentBackground = transparentBackground;
            _background = (ushort)NormalizeColorIndex(backgroundColor);
        }

        public bool WriteSixel(int value, int repeat)
        {
            if (repeat > TerminalImageLimits.MaximumPixelDimension)
            {
                return false;
            }
            var requiredWidth = (long)_cursorX + repeat;
            var requiredHeight = (long)_cursorY + (6L * _aspectRatio);
            var pixelWrites = Math.Max(1L, (long)CountSetBits(value) * _aspectRatio) * repeat;
            if (!DimensionsAllowed(requiredWidth, requiredHeight) ||
                pixelWrites > _remainingPixelWrites ||
                !EnsureCapacity((int)requiredWidth, (int)requiredHeight))
            {
                return false;
            }

            _hasSixelData = true;
            _imageWidth = Math.Max(_imageWidth, (int)requiredWidth);
            _imageHeight = Math.Max(_imageHeight, (int)requiredHeight);
            _remainingPixelWrites -= pixelWrites;
            if (value == 0)
            {
                _cursorX += repeat;
                return true;
            }

            if (value == 0x3F && _cursorX == 0 && repeat == _capacityWidth)
            {
                _pixels.AsSpan(_cursorY * _capacityWidth, 6 * _aspectRatio * _capacityWidth).Fill(_foreground);
                _cursorX += repeat;
                return true;
            }

            if (repeat == 1 && _aspectRatio == 1)
            {
                var pixels = _pixels;
                var stride = _capacityWidth;
                var rowOffset = (_cursorY * stride) + _cursorX;
                var fg = _foreground;
                for (var bit = 0; bit < 6; bit++)
                {
                    if ((value & (1 << bit)) != 0)
                    {
                        pixels[rowOffset] = fg;
                    }

                    rowOffset += stride;
                }
            }
            else
            {
                var pixelsSpan = _pixels.AsSpan();
                var stride = _capacityWidth;
                var fg = _foreground;
                for (var bit = 0; bit < 6; bit++)
                {
                    if ((value & (1 << bit)) == 0)
                    {
                        continue;
                    }

                    var firstRow = _cursorY + (bit * _aspectRatio);
                    for (var row = 0; row < _aspectRatio; row++)
                    {
                        pixelsSpan.Slice(((firstRow + row) * stride) + _cursorX, repeat).Fill(fg);
                    }
                }
            }

            _cursorX += repeat;
            return true;
        }

        public void SelectColor(ReadOnlySpan<int> parameters)
        {
            var color = NormalizeColorIndex(Parameter(parameters, 0, 0));
            if (parameters.Length >= 2)
            {
                var model = Parameter(parameters, 1, 0);
                var first = Parameter(parameters, 2, 0);
                var second = Parameter(parameters, 3, 0);
                var third = Parameter(parameters, 4, 0);
                if (model == 1)
                {
                    _palette[color] = HlsToRgba(first, second, third);
                }
                else if (model == 2)
                {
                    _palette[color] = RgbPercentToRgba(first, second, third);
                }
            }

            _foreground = (ushort)color;
        }

        public bool SetRasterAttributes(ReadOnlySpan<int> parameters)
        {
            if (_hasSixelData)
            {
                return true;
            }

            var numerator = Parameter(parameters, 0, 0);
            var denominator = numerator > 0 && (parameters.Length < 2 || parameters[1] < 0)
                ? 1
                : Parameter(parameters, 1, 0);
            if (numerator > 0 && denominator > 0)
            {
                _aspectRatio = Math.Clamp((numerator + denominator - 1) / denominator, 1, MaximumAspectRatio);
            }

            var width = Parameter(parameters, 2, 0);
            var height = Parameter(parameters, 3, 0);
            if (width > 0)
            {
                _rasterWidth = width;
            }

            if (height > 0)
            {
                _rasterHeight = height;
            }

            return DimensionsAllowed(
                Math.Max(_imageWidth, _rasterWidth),
                Math.Max(_imageHeight, _rasterHeight));
        }

        public void CarriageReturn() => _cursorX = 0;

        public bool NextLine()
        {
            _hasSixelData = true;
            _cursorX = 0;
            var next = (long)_cursorY + (6L * _aspectRatio);
            if (next > TerminalImageLimits.MaximumPixelDimension)
            {
                return false;
            }

            _cursorY = (int)next;
            return true;
        }

        public bool TryCreateImage(out SixelImage? image)
        {
            image = null;
            var width = Math.Max(_imageWidth, _rasterWidth);
            var height = Math.Max(_imageHeight, _rasterHeight);
            if (width == 0 || height == 0 || !DimensionsAllowed(width, height))
            {
                return false;
            }

            var result = new ushort[checked(width * height)];
            var emptyColor = _transparentBackground ? SixelImage.TransparentColorIndex : _background;
            for (var row = 0; row < _imageHeight; row++)
            {
                var dstRow = result.AsSpan(row * width, width);
                _pixels.AsSpan(row * _capacityWidth, _imageWidth).CopyTo(dstRow);
                if (emptyColor != 0 && width > _imageWidth)
                {
                    dstRow[_imageWidth..].Fill(emptyColor);
                }
            }

            if (emptyColor != 0 && height > _imageHeight)
            {
                result.AsSpan(_imageHeight * width, (height - _imageHeight) * width).Fill(emptyColor);
            }

            image = new SixelImage(
                width,
                height,
                _aspectRatio,
                _transparentBackground,
                _cursorY,
                result,
                (uint[])_palette.Clone());
            return true;
        }

        public void Dispose()
        {
            if (_pixelsRented)
            {
                ArrayPool<ushort>.Shared.Return(_pixels);
                _pixels = [];
                _pixelsRented = false;
            }
        }

        private bool EnsureCapacity(int width, int height)
        {
            if (width <= _capacityWidth && height <= _capacityHeight)
            {
                return true;
            }

            var targetWidth = _capacityWidth == 0 && _rasterWidth > width && _rasterWidth <= TerminalImageLimits.MaximumPixelDimension
                ? _rasterWidth
                : width;
            var targetHeight = _capacityHeight == 0 && _rasterHeight > height && _rasterHeight <= TerminalImageLimits.MaximumPixelDimension
                ? _rasterHeight
                : height;
            if ((long)targetWidth * targetHeight > TerminalImageLimits.MaximumPixelCount)
            {
                targetWidth = width;
                targetHeight = height;
            }

            var newWidth = Grow(_capacityWidth, targetWidth);
            var newHeight = Grow(_capacityHeight, targetHeight);
            if ((long)newWidth * newHeight > TerminalImageLimits.MaximumPixelCount)
            {
                newWidth = width;
                newHeight = height;
                if ((long)newWidth * newHeight > TerminalImageLimits.MaximumPixelCount)
                {
                    return false;
                }
            }

            var requiredLength = checked(newWidth * newHeight);
            var replacement = ArrayPool<ushort>.Shared.Rent(requiredLength);
            var emptyColor = _transparentBackground ? SixelImage.TransparentColorIndex : _background;
            if (_capacityWidth == 0 || _capacityHeight == 0)
            {
                replacement.AsSpan(0, requiredLength).Fill(emptyColor);
            }
            else
            {
                for (var row = 0; row < _capacityHeight; row++)
                {
                    var dstRow = replacement.AsSpan(row * newWidth, newWidth);
                    _pixels.AsSpan(row * _capacityWidth, _capacityWidth).CopyTo(dstRow);
                    if (newWidth > _capacityWidth)
                    {
                        dstRow[_capacityWidth..].Fill(emptyColor);
                    }
                }

                if (newHeight > _capacityHeight)
                {
                    replacement.AsSpan(_capacityHeight * newWidth, (newHeight - _capacityHeight) * newWidth).Fill(emptyColor);
                }
            }

            if (_pixelsRented)
            {
                ArrayPool<ushort>.Shared.Return(_pixels);
            }

            _pixels = replacement;
            _pixelsRented = true;
            _capacityWidth = newWidth;
            _capacityHeight = newHeight;
            return true;
        }

        private static int Grow(int current, int required)
        {
            var value = Math.Max(8, current);
            while (value < required && value < TerminalImageLimits.MaximumPixelDimension)
            {
                value = Math.Min(value * 2, TerminalImageLimits.MaximumPixelDimension);
            }

            return value;
        }

        private static int CountSetBits(int value) => BitOperations.PopCount((uint)value);
    }

    private static int ReadParameters(ReadOnlySpan<byte> data, ref int offset, Span<int> parameters)
    {
        parameters.Fill(-1);
        var index = 0;
        var hasParameterByte = false;
        while (offset < data.Length)
        {
            var value = data[offset];
            if (IsIgnoredByte(value))
            {
                offset++;
                continue;
            }

            if (value is >= (byte)'0' and <= (byte)'9')
            {
                hasParameterByte = true;
                if (index < parameters.Length)
                {
                    parameters[index] = Math.Min(
                        (Math.Max(0, parameters[index]) * 10) + (value - '0'),
                        65535);
                }

                offset++;
            }
            else if (value == (byte)';')
            {
                hasParameterByte = true;
                index++;
                offset++;
            }
            else
            {
                break;
            }
        }

        return hasParameterByte ? Math.Min(index + 1, parameters.Length) : 0;
    }

    private static bool IsIgnoredByte(byte value) => value <= 0x20 || value == 0x7F;

    private static int NormalizeColorIndex(int color) =>
        ((color % MaximumColors) + MaximumColors) % MaximumColors;

    private static int Parameter(ReadOnlySpan<int> parameters, int index, int defaultValue) =>
        (uint)index < (uint)parameters.Length && parameters[index] >= 0 ? parameters[index] : defaultValue;

    private static bool DimensionsAllowed(long width, long height) =>
        width is >= 0 and <= TerminalImageLimits.MaximumPixelDimension &&
        height is >= 0 and <= TerminalImageLimits.MaximumPixelDimension &&
        width * height <= TerminalImageLimits.MaximumPixelCount;

    private static int MacroAspectRatio(int value) => value switch
    {
        0 or 1 or 5 or 6 => 2,
        2 => 5,
        3 or 4 => 3,
        _ => 1,
    };

    private static uint RgbPercentToRgba(int red, int green, int blue) =>
        Pack(
            PercentToByte(red),
            PercentToByte(green),
            PercentToByte(blue));

    private static uint HlsToRgba(int hue, int lightness, int saturation)
    {
        var normalizedHue = ((hue % 360) + 360) % 360;
        var h = ((normalizedHue + 240) % 360) / 360d;
        var l = Math.Clamp(lightness, 0, 100) / 100d;
        var s = Math.Clamp(saturation, 0, 100) / 100d;
        if (s == 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return Pack(gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return Pack(
            UnitToByte(HueToRgb(p, q, h + (1d / 3d))),
            UnitToByte(HueToRgb(p, q, h)),
            UnitToByte(HueToRgb(p, q, h - (1d / 3d))));
    }

    private static double HueToRgb(double p, double q, double value)
    {
        if (value < 0)
        {
            value++;
        }
        else if (value > 1)
        {
            value--;
        }

        if (value < 1d / 6d)
        {
            return p + ((q - p) * 6 * value);
        }

        if (value < 0.5)
        {
            return q;
        }

        return value < 2d / 3d ? p + ((q - p) * ((2d / 3d) - value) * 6) : p;
    }

    private static byte PercentToByte(int value) =>
        PercentToByteTable[Math.Clamp(value, 0, 100)];

    private static byte[] CreatePercentToByteTable()
    {
        var table = new byte[101];
        for (var i = 0; i <= 100; i++)
        {
            table[i] = (byte)Math.Round(i * 255d / 100d);
        }

        return table;
    }

    private static byte UnitToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static uint Pack(byte red, byte green, byte blue) =>
        0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;

    private static uint[] CreateDefaultPalette()
    {
        var palette = new uint[MaximumColors];
        uint[] vt340 =
        [
            0xFF000000, 0xFF3333CC, 0xFFCC3333, 0xFF33CC33,
            0xFFCC33CC, 0xFF33CCCC, 0xFFCCCC33, 0xFF878787,
            0xFF424242, 0xFF5454FF, 0xFFFF5454, 0xFF54FF54,
            0xFFFF54FF, 0xFF54FFFF, 0xFFFFFF54, 0xFFFFFFFF,
        ];
        vt340.CopyTo(palette, 0);

        var index = 16;
        int[] levels = [0, 95, 135, 175, 215, 255];
        foreach (var red in levels)
        {
            foreach (var green in levels)
            {
                foreach (var blue in levels)
                {
                    palette[index++] = Pack((byte)red, (byte)green, (byte)blue);
                }
            }
        }

        for (var gray = 0; gray < 24; gray++)
        {
            var value = (byte)(8 + (gray * 10));
            palette[index++] = Pack(value, value, value);
        }

        return palette;
    }
}
