using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Render;
using SkiaSharp;

namespace Devolutions.Terminal.Automation;

internal sealed class AutomationVisualRenderer : IDisposable
{
    public const int MaximumPixels = 4_194_304;
    public const int MaximumPngBytes = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly SkiaTerminalRenderer _renderer = new();
    private SKSurface? _surface;
    private int _width;
    private int _height;
    private bool _disposed;

    public CellSize CellSize => _renderer.CellSize;

    public (TerminalAutomationFrame Frame, byte[]? Png) Render(
        TerminalSnapshot snapshot, ColorScheme scheme, long revision,
        TerminalAutomationRegion? region, bool includeCursor, bool encode)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var started = Stopwatch.GetTimestamp();
            var columns = snapshot.Buffer.Columns;
            var rows = snapshot.Buffer.Rows;
            region ??= new(0, 0, columns, rows);
            if (region.Column < 0 || region.Row < 0 || region.Columns <= 0 || region.Rows <= 0 ||
                (long)region.Column + region.Columns > columns || (long)region.Row + region.Rows > rows)
            {
                throw new ArgumentOutOfRangeException(nameof(region), "The capture region must be inside the terminal grid.");
            }

            var left = (int)Math.Floor(region.Column * CellSize.Width);
            var top = (int)Math.Floor(region.Row * CellSize.Height);
            var width = (int)Math.Ceiling((region.Column + region.Columns) * CellSize.Width) - left;
            var height = (int)Math.Ceiling((region.Row + region.Rows) * CellSize.Height) - top;
            if ((long)width * height > MaximumPixels)
            {
                throw new ArgumentOutOfRangeException(nameof(region), $"A capture may contain at most {MaximumPixels} pixels; use a smaller region.");
            }

            if (_surface is null || width != _width || height != _height)
            {
                _surface?.Dispose();
                _surface = null;
                _surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
                    ?? throw new InvalidOperationException("Could not allocate the offscreen terminal surface.");
                _width = width;
                _height = height;
            }

            _renderer.Resize(new RenderViewport(columns, rows, 1));
            var frame = TerminalRenderPlanner.Create(snapshot, scheme);
            var canvas = _surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Save();
            try
            {
                canvas.Translate(-left, -top);
                _renderer.Render(canvas, frame, TerminalRenderOverlays.Empty,
                    new SKRect(0, 0, (float)(columns * CellSize.Width), (float)(rows * CellSize.Height)),
                    0, includeCursor);
            }
            finally
            {
                canvas.Restore();
            }

            _surface.Flush();
            using var pixels = _surface.PeekPixels();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> dimensions = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(dimensions, width);
            BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], height);
            digest.AppendData(dimensions);
            digest.AppendData(pixels.GetPixelSpan());
            var hash = Convert.ToHexString(digest.GetHashAndReset());
            var result = new TerminalAutomationFrame(revision, hash, width, height,
                CellSize.Width, CellSize.Height, region, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
            {
                IncludesCursor = includeCursor,
            };
            if (!encode)
            {
                return (result, null);
            }

            using var image = _surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Could not encode the terminal PNG.");
            if (png.Size > MaximumPngBytes)
            {
                throw new InvalidOperationException("The terminal PNG exceeds 16 MiB; use a smaller capture region.");
            }

            return (result, png.ToArray());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _surface?.Dispose();
            _renderer.Dispose();
        }
    }
}
