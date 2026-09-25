using Devolutions.Terminal.Core;
using Devolutions.Terminal.Render;
using SkiaSharp;
using Xunit;

namespace Devolutions.Terminal.Render.Tests;

public sealed class SkiaTerminalRendererTests
{
    [Fact]
    public void RetroScanlineEffectIsOptionalAndDeterministic()
    {
        var frame = CreateFrame("effect");
        using var plainRenderer = new SkiaTerminalRenderer();
        using var effectRenderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            Effect = TerminalRenderEffect.RetroScanlines,
        });
        using var plain = NewBitmap(plainRenderer, frame);
        using var first = NewBitmap(effectRenderer, frame);
        using var second = NewBitmap(effectRenderer, frame);
        using var plainCanvas = new SKCanvas(plain);
        using var firstCanvas = new SKCanvas(first);
        using var secondCanvas = new SKCanvas(second);

        Draw(plainRenderer, plainCanvas, frame);
        Draw(effectRenderer, firstCanvas, frame);
        Draw(effectRenderer, secondCanvas, frame);

        Assert.NotEqual(PixelDigest(plain), PixelDigest(first));
        Assert.Equal(PixelDigest(first), PixelDigest(second));
    }

    [Fact]
    public void DrawsDrcsMaskWithoutFontFallback()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("\u001bP0;1;0;2;1;2;6;0{ B~\u001b\\\u001b( B!");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal(0, renderer.CacheStatistics.Count);
        Assert.Contains(
            Enumerable.Range(0, bitmap.Width),
            x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y) != SKColors.Black));
    }

    [Fact]
    public void ShapesComplexUnicodeAndReusesBoundedCache()
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            GlyphCacheCapacity = 8,
            RowPictureCacheCapacity = 0,
        });
        var frame = CreateFrame("e\u0301界 \U0001F469\u200D\U0001F4BB \uE0B0");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);
        var first = renderer.CacheStatistics;
        Draw(renderer, canvas, frame);
        var second = renderer.CacheStatistics;

        Assert.InRange(first.Count, 1, first.Capacity);
        Assert.True(second.Hits > first.Hits);
        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public void EvictsLeastRecentlyUsedGlyphsAtCapacity()
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            GlyphCacheCapacity = 2,
        });
        var frame = CreateFrame("abc");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal(2, renderer.CacheStatistics.Count);
        Assert.True(renderer.CacheStatistics.Evictions >= 1);
    }

    [Fact]
    public void RecordedRowsMatchUncachedPaintWhenScrollingAndRestyling()
    {
        using var engine = new TerminalEngine(16, 4);
        using var cached = new SkiaTerminalRenderer();
        using var uncached = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            RowPictureCacheCapacity = 0,
            ReuseAsciiGlyphs = false,
        });
        using var actual = NewBitmap(cached, TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme));
        using var expected = NewBitmap(uncached, TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme));
        using var actualCanvas = new SKCanvas(actual);
        using var expectedCanvas = new SKCanvas(expected);

        foreach (var input in new[]
        {
            "first\r\nsecond\r\nthird\r\nfourth",
            "\r\nfifth",
            "\r\nsixth \u754c",
            "\u001b[1;1H\u001b[31;44mRED\u001b[0m",
            "\u001b[?25l\u001b[2;4H!",
            "\u001b[?25h\u001b[1;1H\u001b#6wide",
            "\u001bP0;1;0;2;1;2;6;0{ B~\u001b\\\u001b( B!",
        })
        {
            engine.Feed(input);
            var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
            Draw(cached, actualCanvas, frame);
            Draw(uncached, expectedCanvas, frame);
            Assert.Equal(PixelDigest(expected), PixelDigest(actual));
        }

        Assert.True(cached.RowCacheStatistics.Hits > 0);
        Assert.True(cached.RowCacheStatistics.Count > 0);
        cached.Invalidate();
        Assert.Equal(0, cached.RowCacheStatistics.Count);
    }

    [Fact]
    public void AsciiGlyphsAreSharedAcrossRunsWithoutMergingUnicodeShaping()
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            RowPictureCacheCapacity = 0,
        });
        foreach (var text in new[] { "cab", "dab", "e\u0301界", "e\u0301界" })
        {
            var frame = CreateFrame(text);
            using var bitmap = NewBitmap(renderer, frame);
            using var canvas = new SKCanvas(bitmap);
            Draw(renderer, canvas, frame);
        }

        Assert.Equal(4, renderer.CacheStatistics.Hits);
        Assert.Equal(6, renderer.CacheStatistics.Misses);
    }

    [Fact]
    public void RecordedRowCacheEvictsAndResetsWhenScaleChanges()
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            RowPictureCacheCapacity = 2,
        });
        var frame = CreateFrame("a\r\nb");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);
        Draw(renderer, canvas, frame);
        Assert.Equal(0, renderer.RowCacheStatistics.Count);
        Draw(renderer, canvas, frame);
        Assert.Equal(2, renderer.RowCacheStatistics.Count);

        var second = CreateFrame("c\r\nd");
        Draw(renderer, canvas, second);
        Draw(renderer, canvas, second);
        Assert.Equal(2, renderer.RowCacheStatistics.Count);
        Assert.True(renderer.RowCacheStatistics.Evictions > 0);

        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, 1.5));
        Assert.Equal(0, renderer.RowCacheStatistics.Count);
    }

    [Fact]
    public void SingleUseRowsAreNotRecordedAndInvalidationClearsCandidates()
    {
        using var engine = new TerminalEngine(8, 1);
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            RowPictureCacheCapacity = 2,
        });
        using var bitmap = new SKBitmap(128, 64);
        using var canvas = new SKCanvas(bitmap);
        TerminalRenderFrame? last = null;

        foreach (var character in "abcde")
        {
            engine.Feed($"\u001b[1;1H{character}");
            last = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
            Draw(renderer, canvas, last);
        }

        Assert.Equal(0, renderer.RowCacheStatistics.Count);
        Draw(renderer, canvas, last!);
        Assert.Equal(1, renderer.RowCacheStatistics.Count);
        renderer.Invalidate();
        Draw(renderer, canvas, last!);
        Assert.Equal(0, renderer.RowCacheStatistics.Count);
        Draw(renderer, canvas, last!);
        Assert.Equal(1, renderer.RowCacheStatistics.Count);
    }

    [Fact]
    public void RecordedRowsMatchUncachedPaintAtDifferentPaddingAndScale()
    {
        var frame = CreateFrame("\u001b[4mleft\u001b[0m\r\nright");
        using var cached = new SkiaTerminalRenderer();
        using var reference = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            RowPictureCacheCapacity = 0,
            ReuseAsciiGlyphs = false,
        });
        cached.Resize(new RenderViewport(frame.Columns, frame.Rows, 1.5));
        reference.Resize(new RenderViewport(frame.Columns, frame.Rows, 1.5));
        using var actual = NewBitmap(cached, frame);
        using var expected = NewBitmap(reference, frame);
        using var actualCanvas = new SKCanvas(actual);
        using var expectedCanvas = new SKCanvas(expected);
        var bounds = new SKRect(0, 0, actual.Width, actual.Height);

        foreach (var padding in new[] { 3f, 8f, 8f })
        {
            cached.Render(actualCanvas, frame, TerminalRenderOverlays.Empty, bounds, padding, drawCursor: true);
            reference.Render(expectedCanvas, frame, TerminalRenderOverlays.Empty, bounds, padding, drawCursor: true);
            Assert.Equal(PixelDigest(expected), PixelDigest(actual));
        }

        Assert.True(cached.RowCacheStatistics.Hits > 0);
    }

    [Fact]
    public void StylesHaveIndependentShapingEntries()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("a\u001b[1mb\u001b[3mc");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.True(renderer.CacheStatistics.Count >= 3);
    }

    [Fact]
    public void ContextualGlyphPositionsHaveIndependentCacheEntries()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("\u0633\u0633");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.True(renderer.CacheStatistics.Count >= 1);
    }

    [Fact]
    public void BuiltinPowerlineSeparatorsAvoidMissingGlyphEntries()
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            FontFamily = "A font family that does not exist",
        });
        var frame = CreateFrame("\uE0A0\uE0A1\uE0A2\uE0A3\uE0B0\uE0B1\uE0B2\uE0B3");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal(0, renderer.CacheStatistics.Count);
    }

    [Fact]
    public void BuiltinBlockElementsSnapClaudeLogoToCellEdges()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame(
            "\u001b[38;2;215;119;87m\u2590\u259B\u2588\u259D\u259C\u2580");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var orange = new SKColor(215, 119, 87);
        var background = new SKColor(12, 12, 12);
        var cellWidth = (int)renderer.CellSize.Width;
        var cellHeight = (int)renderer.CellSize.Height;
        var left = 8;
        var top = 8;

        Assert.Equal(background, bitmap.GetPixel(left, top + (cellHeight / 2)));
        Assert.Equal(orange, bitmap.GetPixel(left + cellWidth - 1, top + (cellHeight / 2)));

        left += cellWidth;
        Assert.Equal(orange, bitmap.GetPixel(left, top));
        Assert.Equal(orange, bitmap.GetPixel(left + cellWidth - 1, top));
        Assert.Equal(orange, bitmap.GetPixel(left, top + cellHeight - 1));
        Assert.Equal(background, bitmap.GetPixel(left + cellWidth - 1, top + cellHeight - 1));

        left += cellWidth;
        Assert.Equal(orange, bitmap.GetPixel(left, top));
        Assert.Equal(orange, bitmap.GetPixel(left + cellWidth - 1, top + cellHeight - 1));

        left += cellWidth;
        Assert.Equal(background, bitmap.GetPixel(left, top));
        Assert.Equal(orange, bitmap.GetPixel(left + cellWidth - 1, top));
        Assert.Equal(background, bitmap.GetPixel(left + cellWidth - 1, top + cellHeight - 1));

        left += cellWidth;
        Assert.Equal(orange, bitmap.GetPixel(left, top));
        Assert.Equal(background, bitmap.GetPixel(left, top + cellHeight - 1));
        Assert.Equal(orange, bitmap.GetPixel(left + cellWidth - 1, top + cellHeight - 1));

        left += cellWidth;
        Assert.Equal(orange, bitmap.GetPixel(left, top));
        Assert.Equal(background, bitmap.GetPixel(left, top + cellHeight - 1));
        Assert.Equal(0, renderer.CacheStatistics.Count);
    }

    [Fact]
    public void EmojiUsesAnInstalledPlatformFallback()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("\U0001F600");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.False(string.IsNullOrWhiteSpace(renderer.LastResolvedFontFamily));
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("Segoe UI Emoji", renderer.LastResolvedFontFamily);
        var hasColorLayer = false;
        for (var y = 8; y < 8 + (int)renderer.CellSize.Height; y++)
        {
            for (var x = 8; x < 8 + ((int)renderer.CellSize.Width * 2); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 150 && pixel.Green > 80 && pixel.Blue < 100)
                {
                    hasColorLayer = true;
                }
            }
        }

        Assert.True(hasColorLayer);
    }

    [Fact]
    public void ClaudeHeaderSymbolUsesMacOsDingbatsFallback()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            FallbackFontFamilies = ["Zapf Dingbats"],
        });
        var frame = CreateFrame("\u2733");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal("Zapf Dingbats", renderer.LastResolvedFontFamily);
    }

    [Fact]
    public void DpiChangeInvalidatesDeviceIndependentGlyphResources()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("abc");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);
        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, 1));
        Draw(renderer, canvas, frame);
        var generation = renderer.ResourceGeneration;

        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, 1.5));

        Assert.Equal(0, renderer.CacheStatistics.Count);
        Assert.True(renderer.ResourceGeneration > generation);
    }

    [Theory]
    [InlineData(TerminalCursorStyle.Bar, 0.05, 0.25)]
    [InlineData(TerminalCursorStyle.Underscore, 0.05, 0.25)]
    [InlineData(TerminalCursorStyle.DoubleUnderscore, 0.08, 0.35)]
    [InlineData(TerminalCursorStyle.Vintage, 0.20, 0.35)]
    [InlineData(TerminalCursorStyle.FilledBox, 0.90, 1.0)]
    [InlineData(TerminalCursorStyle.EmptyBox, 0.10, 0.50)]
    public void CursorStylesProduceStableGeometry(
        TerminalCursorStyle style,
        double minimumFill,
        double maximumFill)
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("") with
        {
            CursorStyle = style,
            CursorHeightPercentage = 25,
        };
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var colored = 0;
        var total = (int)(renderer.CellSize.Width * renderer.CellSize.Height);
        for (var y = 8; y < 8 + (int)renderer.CellSize.Height; y++)
        {
            for (var x = 8; x < 8 + (int)renderer.CellSize.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != new SKColor(12, 12, 12))
                {
                    colored++;
                }
            }
        }

        var fill = (double)colored / total;
        Assert.InRange(fill, minimumFill, maximumFill);
    }

    [Fact]
    public void OverlayColorsComposeInSelectionSearchHyperlinkOrder()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);
        var overlays = new TerminalRenderOverlays(
            [new TerminalCellRange(0, 0, 0, 0xFF0000FF)],
            [new TerminalCellRange(0, 0, 0, 0xFF00FF00)],
            [new TerminalCellRange(0, 0, 0, 0xFFFF0000)]);

        renderer.Render(
            canvas,
            frame,
            overlays,
            new SKRect(0, 0, bitmap.Width, bitmap.Height),
            8,
            drawCursor: false);

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void FilledCursorRedrawsCellGlyphWithContrastingColor()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("X\b") with { CursorStyle = TerminalCursorStyle.FilledBox };
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var hasContrastingPixel = false;
        for (var y = 8; y < 8 + (int)renderer.CellSize.Height; y++)
        {
            for (var x = 8; x < 8 + (int)renderer.CellSize.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == new SKColor(12, 12, 12))
                {
                    hasContrastingPixel = true;
                }
            }
        }

        Assert.True(hasContrastingPixel);
    }

    [Fact]
    public void FilledCursorForcesContrastWhenCellBackgroundMatchesCursor()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("\u001b[107mX\b") with
        {
            CursorStyle = TerminalCursorStyle.FilledBox,
            CursorColor = 0xFFFFFFFF,
        };
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var hasBlackGlyphPixel = false;
        for (var y = 8; y < 8 + (int)renderer.CellSize.Height; y++)
        {
            for (var x = 8; x < 8 + (int)renderer.CellSize.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == SKColors.Black)
                {
                    hasBlackGlyphPixel = true;
                }
            }
        }

        Assert.True(hasBlackGlyphPixel);
    }

    [Fact]
    public void RendersSixelOverlayAtTerminalAnchor()
    {
        var engine = new TerminalEngine(16, 2);
        engine.Feed("\u001bPq#2;2;100;0;0~\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        renderer.Render(
            canvas,
            frame,
            TerminalRenderOverlays.Empty,
            new SKRect(0, 0, bitmap.Width, bitmap.Height),
            8,
            drawCursor: false);

        Assert.Single(frame.Images);
        var pixel = bitmap.GetPixel(8, 8);
        Assert.True(pixel.Red > 200 && pixel.Green < 30 && pixel.Blue < 30);
    }

    [Fact]
    public void RendersSixelWithRetainedCellGeometry()
    {
        var engine = new TerminalEngine(16, 2);
        engine.Resize(16, 2, 20, 40);
        engine.Feed("\u001bPq#2;2;100;0;0!2~\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.True(bitmap.GetPixel(11, 12).Red > 200);
        Assert.Equal(new SKColor(12, 12, 12), bitmap.GetPixel(13, 12));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SixelPhysicalCellGeometryIsConvertedToDips(double scale)
    {
        var engine = new TerminalEngine(16, 4);
        engine.Resize(16, 4, 20 * scale, 40 * scale);
        engine.Feed("\u001bPq\"1;1;10;6#2;2;100;0;0!10~\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, scale));
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        renderer.Render(
            canvas, frame, TerminalRenderOverlays.Empty,
            new SKRect(0, 0, bitmap.Width, bitmap.Height), 8, drawCursor: false);

        Assert.Equal(SKColors.Red, bitmap.GetPixel(8, 8));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(27, 19));
        Assert.Equal(new SKColor(12, 12, 12), bitmap.GetPixel(28, 19));
        Assert.Equal(new SKColor(12, 12, 12), bitmap.GetPixel(27, 20));
    }

    [Fact]
    public void ImageAnchorColumnScalesWithDoubleWidthRendition()
    {
        var engine = new TerminalEngine(16, 2);
        engine.Feed("\u001b#6\u001b[3G\u001bPq#2;2;100;0;0~\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var expectedLeft = 8 + (4 * (int)renderer.CellSize.Width);
        Assert.True(bitmap.GetPixel(expectedLeft, 8).Red > 200);
        Assert.Equal(new SKColor(12, 12, 12), bitmap.GetPixel(
            8 + (2 * (int)renderer.CellSize.Width),
            8));
    }

    [Fact]
    public void RendersBoundedConEmuEncodedImage()
    {
        using var source = new SKBitmap(2, 2);
        source.Erase(SKColors.Red);
        using var encoded = source.Encode(SKEncodedImageFormat.Png, 100);
        var payload = Convert.ToBase64String(encoded.ToArray());
        var engine = new TerminalEngine(16, 2);
        engine.Feed($"\u001b]9;4;st=0;sz={encoded.Size};{payload}\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal(TerminalImageProtocol.ConEmuInline, Assert.Single(frame.Images).Protocol);
        Assert.True(bitmap.GetPixel(8, 8).Red > 200);
    }

    [Fact]
    public void RendersKittyRawRgbaAtAnchor()
    {
        var red = Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 16).SelectMany(static b => b).ToArray();
        var engine = new TerminalEngine(16, 2);
        engine.Feed($"\u001b_Ga=T,f=32,s=4,v=4,i=1,C=1;{Convert.ToBase64String(red)}\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal(TerminalImageProtocol.KittyGraphics, Assert.Single(frame.Images).Protocol);
        var pixel = bitmap.GetPixel(10, 10);
        Assert.True(pixel.Red > 200 && pixel.Green < 30 && pixel.Blue < 30);
    }

    [Fact]
    public void RendersKittyEncodedPng()
    {
        using var source = new SKBitmap(4, 4);
        source.Erase(SKColors.Blue);
        using var encoded = source.Encode(SKEncodedImageFormat.Png, 100);
        var engine = new TerminalEngine(16, 2);
        engine.Feed($"\u001b_Ga=T,f=100,i=2,C=1;{Convert.ToBase64String(encoded.ToArray())}\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.True(bitmap.GetPixel(10, 10).Blue > 200);
    }

    [Fact]
    public void KittyCellSizingScalesToColumnsAndRows()
    {
        var green = Enumerable.Repeat(new byte[] { 0, 255, 0, 255 }, 16).SelectMany(static b => b).ToArray();
        var engine = new TerminalEngine(16, 4);
        engine.Feed($"\u001b_Ga=T,f=32,s=4,v=4,i=3,c=2,r=1,C=1;{Convert.ToBase64String(green)}\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var cellWidth = (int)renderer.CellSize.Width;
        var cellHeight = (int)renderer.CellSize.Height;
        // Inside the 2x1 cell rectangle: green.
        Assert.True(bitmap.GetPixel(8 + cellWidth + (cellWidth / 2), 8 + (cellHeight / 2)).Green > 200);
        // Past the second column: not green.
        Assert.True(bitmap.GetPixel(8 + (2 * cellWidth) + 1, 8 + (cellHeight / 2)).Green < 30);
        // Below the first row: not green.
        Assert.True(bitmap.GetPixel(8 + (cellWidth / 2), 8 + cellHeight + 1).Green < 30);
    }

    [Fact]
    public void KittyNegativeZIndexDrawsUnderTextBackground()
    {
        var pixel = RenderKittyBehindOrOverText(zIndex: -1);
        Assert.True(pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200, "white run background must cover a z<0 image");
    }

    [Fact]
    public void KittyNonNegativeZIndexDrawsOverText()
    {
        var pixel = RenderKittyBehindOrOverText(zIndex: 1);
        Assert.True(pixel.Red > 200 && pixel.Green < 30, "z>=0 image must composite over text");
    }

    [Fact]
    public void KittyCropSelectsSourceRegion()
    {
        // 4x1 pixels: red, green, blue, white. Crop to the green pixel only.
        var pixels = new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255,
        };
        var engine = new TerminalEngine(16, 2);
        engine.Feed($"\u001b_Ga=T,f=32,s=4,v=1,i=6,X=1,Y=0,w=1,h=1,c=1,r=1,C=1;{Convert.ToBase64String(pixels)}\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var center = bitmap.GetPixel(8 + ((int)renderer.CellSize.Width / 2), 8 + ((int)renderer.CellSize.Height / 2));
        Assert.True(center.Green > 200 && center.Red < 30 && center.Blue < 30, $"expected green, got {center}");
    }

    [Theory]
    [InlineData(4, 1, 3, 0, 3, 1)]
    [InlineData(1, 4, 0, 3, 1, 3)]
    public void KittyCropPastRightOrBottomEdgeUsesOnlyAvailableSourcePixel(
        int width, int height, int cropX, int cropY, int cropWidth, int cropHeight)
    {
        var pixels = new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255,
        };
        var engine = new TerminalEngine(16, 2);
        engine.Feed($"\u001b_Ga=T,f=32,s={width},v={height},i=6,X={cropX},Y={cropY},w={cropWidth},h={cropHeight},c=1,r=1,C=1;{Convert.ToBase64String(pixels)}\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        DrawImageFrame(renderer, canvas, frame);

        Assert.Equal(cropWidth, Assert.Single(frame.Images).Kitty!.CropWidth);
        Assert.Equal(SKColors.White, bitmap.GetPixel(8 + ((int)renderer.CellSize.Width / 2), 8 + ((int)renderer.CellSize.Height / 2)));
        Assert.Equal(new SKColor(12, 12, 12), bitmap.GetPixel(8 + (int)renderer.CellSize.Width + 1, 8 + 2));
    }

    [Fact]
    public void RendersInlineOsc1337AtCellAnchorButInvalidEncodedImageLeavesBackground()
    {
        using var source = new SKBitmap(2, 2);
        source.Erase(SKColors.Green);
        using var encoded = source.Encode(SKEncodedImageFormat.Png, 100);
        var engine = new TerminalEngine(16, 2);
        engine.Feed($"\u001b]1337;File=inline=1;size={encoded.Size};width=1;height=1:{Convert.ToBase64String(encoded.ToArray())}\u0007");
        engine.Feed("\u001b[3G\u001b]1337;File=inline=1;size=3:AQID\u0007");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        DrawImageFrame(renderer, canvas, frame);

        Assert.Equal(2, frame.Images.Count);
        Assert.All(frame.Images, image => Assert.Equal(TerminalImageProtocol.Iterm2Inline, image.Protocol));
        var green = bitmap.GetPixel(9, 9);
        Assert.True(green.Green > 100 && green.Red < 50 && green.Blue < 50);
        Assert.Equal(new SKColor(12, 12, 12), bitmap.GetPixel(8 + (2 * (int)renderer.CellSize.Width) + 1, 9));
    }

    [Fact]
    public void ReusingRendererAfterKittyDeleteAndResetDoesNotDrawStaleCachedPixels()
    {
        var engine = new TerminalEngine(16, 2);
        engine.Feed("\u001b_Ga=T,f=32,s=1,v=1,i=3,c=1,r=1,C=1;/wAA/w==\u001b\\");
        using var renderer = new SkiaTerminalRenderer();
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);
        DrawImageFrame(renderer, canvas, frame);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(9, 9));

        engine.Feed("\u001b_Ga=d,d=I,i=3\u001b\\");
        frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        DrawImageFrame(renderer, canvas, frame);
        Assert.Empty(frame.Images);
        Assert.Equal(new SKColor(12, 12, 12), bitmap.GetPixel(9, 9));

        engine.Feed("\u001b_Ga=T,f=32,s=1,v=1,i=4,c=1,r=1,C=1;AAD//w==\u001b\\");
        frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        DrawImageFrame(renderer, canvas, frame);
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(9, 9));
        engine.Reset();
        frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        DrawImageFrame(renderer, canvas, frame);
        Assert.Empty(frame.Images);
        Assert.Equal(new SKColor(12, 12, 12), bitmap.GetPixel(9, 9));
    }

    private static void DrawImageFrame(
        SkiaTerminalRenderer renderer,
        SKCanvas canvas,
        TerminalRenderFrame frame)
    {
        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, 1));
        renderer.Render(
            canvas, frame, TerminalRenderOverlays.Empty,
            new SKRect(canvas.DeviceClipBounds.Left, canvas.DeviceClipBounds.Top,
                canvas.DeviceClipBounds.Right, canvas.DeviceClipBounds.Bottom),
            8, drawCursor: false);
    }

    private static SKColor RenderKittyBehindOrOverText(int zIndex)
    {
        var red = Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 64).SelectMany(static b => b).ToArray();
        var engine = new TerminalEngine(16, 2);
        // White-background space in the anchor cell, then back to origin.
        engine.Feed("\u001b[47m \u001b[0m\u001b[H");
        engine.Feed($"\u001b_Ga=T,f=32,s=8,v=8,i=5,z={zIndex},c=1,r=1,C=1;{Convert.ToBase64String(red)}\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);
        return bitmap.GetPixel(8 + ((int)renderer.CellSize.Width / 2), 8 + ((int)renderer.CellSize.Height / 2));
    }

    [Fact]
    public void WarmRenderDoesNotAllocatePerCell()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("allocation");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);
        Draw(renderer, canvas, frame);
        Draw(renderer, canvas, frame);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            Draw(renderer, canvas, frame);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 20_480);
    }

    private static TerminalRenderFrame CreateFrame(string text)
    {
        var engine = new TerminalEngine(16, 2);
        engine.Feed(text);
        return TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme) with
        {
            Background = 0xFF0C0C0C,
            CursorColor = 0xFFFFFFFF,
        };
    }

    private static SKBitmap NewBitmap(
        SkiaTerminalRenderer renderer,
        TerminalRenderFrame frame) =>
        new(
            (int)Math.Ceiling((renderer.CellSize.Width * frame.Columns) + 16),
            (int)Math.Ceiling((renderer.CellSize.Height * frame.Rows) + 16));

    private static void Draw(
        SkiaTerminalRenderer renderer,
        SKCanvas canvas,
        TerminalRenderFrame frame)
    {
        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, 1));
        renderer.Render(
            canvas,
            frame,
            TerminalRenderOverlays.Empty,
            new SKRect(
                canvas.DeviceClipBounds.Left,
                canvas.DeviceClipBounds.Top,
                canvas.DeviceClipBounds.Right,
                canvas.DeviceClipBounds.Bottom),
            8,
            drawCursor: true);
    }

    private static ulong PixelDigest(SKBitmap bitmap)
    {
        var digest = 14695981039346656037UL;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                digest ^= (uint)(pixel.Alpha << 24 |
                                 pixel.Red << 16 |
                                 pixel.Green << 8 |
                                 pixel.Blue);
                digest *= 1099511628211UL;
            }
        }
        return digest;
    }
}
