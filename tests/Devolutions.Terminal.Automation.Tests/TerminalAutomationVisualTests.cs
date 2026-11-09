using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Devolutions.Terminal.Automation;
using SkiaSharp;
using Xunit;
using FakeConnection = Devolutions.Terminal.Automation.Tests.TerminalAutomationServiceTests.FakeConnection;

namespace Devolutions.Terminal.Automation.Tests;

public sealed class TerminalAutomationVisualTests
{
    internal const string SixelRed = "\u001bPq\"1;1;8;6#2;2;100;0;0!8~\u001b\\";
    internal const string KittyRed = "\u001b_Ga=T,f=32,s=1,v=1,i=3,c=1,r=1,C=1;/wAA/w==\u001b\\";
    internal const string KittyGreen = "\u001b_Ga=T,f=32,s=1,v=1,i=3,c=1,r=1,C=1;AP8A/w==\u001b\\";

    [Theory]
    [InlineData(SixelRed, "Sixel", 8, 6)]
    [InlineData(KittyRed, "KittyGraphics", 1, 1)]
    public async Task Capture_RealGraphicsDecodeToRedPixelsWithoutChangingText(
        string output, string protocol, int width, int height)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        var text = service.Observe(session.Id).Text;
        var before = service.Capture(session.Id);

        connection.EmitOutput(output);
        var capture = service.Capture(session.Id);
        using var bitmap = Decode(capture);
        var snapshot = service.Observe(session.Id);

        Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
        Assert.NotEqual(before.Frame.Hash, capture.Frame.Hash);
        Assert.Equal(text, snapshot.Text);
        var image = Assert.Single(snapshot.Images);
        Assert.Equal(protocol, image.Protocol);
        Assert.Equal((0, 0, width, height), (image.Column, image.Row, image.PixelWidth, image.PixelHeight));
        Assert.Equal(new TerminalAutomationRegion(0, 0, 8, 3), capture.Frame.Region);
        Assert.Equal((bitmap.Width, bitmap.Height), (capture.Frame.PixelWidth, capture.Frame.PixelHeight));
        Assert.Equal((int)Math.Ceiling(8 * capture.Frame.CellWidth), bitmap.Width);
        Assert.Equal((int)Math.Ceiling(3 * capture.Frame.CellHeight), bitmap.Height);
        Assert.False(capture.Frame.IncludesCursor);
        Assert.True(capture.Frame.RenderMilliseconds >= 0);
    }

    [Fact]
    public async Task Capture_HashIsSha256OfDimensionsAndBgraPixelsNotRevision()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var first = service.Capture(session.Id);
        connection.EmitOutput("\u001b]2;new title\u0007");
        var second = service.Capture(session.Id);

        Assert.True(second.Frame.Revision > first.Frame.Revision);
        Assert.Equal(first.Frame.Hash, second.Frame.Hash);
        using var decoded = Decode(second);
        using var bgra = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bgra))
        {
            canvas.DrawBitmap(decoded, 0, 0);
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var dimensions = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, bgra.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions.AsSpan(4), bgra.Height);
        digest.AppendData(dimensions);
        digest.AppendData(bgra.GetPixelSpan());
        Assert.Equal(Convert.ToHexString(digest.GetHashAndReset()), second.Frame.Hash);
    }

    [Fact]
    public async Task Capture_CropUsesCellOriginAndDimensionsAndOptionalCursor()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput("\u001b[2;3H" + KittyGreen);
        var full = service.Capture(session.Id);
        var region = new TerminalAutomationRegion(2, 1, 2, 1);
        var cropped = service.Capture(session.Id, region);
        using var bitmap = Decode(cropped);
        using var fullBitmap = Decode(full);
        var left = (int)Math.Floor(2 * full.Frame.CellWidth);
        var top = (int)Math.Floor(full.Frame.CellHeight);

        Assert.Equal(region, cropped.Frame.Region);
        Assert.Equal((int)Math.Ceiling(4 * full.Frame.CellWidth) - left, bitmap.Width);
        Assert.Equal((int)Math.Ceiling(2 * full.Frame.CellHeight) - top, bitmap.Height);
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(2, 2));
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                Assert.Equal(fullBitmap.GetPixel(left + x, top + y), bitmap.GetPixel(x, y));
            }
        }

        connection.EmitOutput("\u001b[H");
        var withoutCursor = service.Capture(session.Id);
        var withCursor = service.Capture(session.Id, includeCursor: true);
        Assert.False(withoutCursor.Frame.IncludesCursor);
        Assert.True(withCursor.Frame.IncludesCursor);
        Assert.NotEqual(withoutCursor.Frame.Hash, withCursor.Frame.Hash);
    }

    [Fact]
    public async Task Capture_ImageReplacementAndDeletionInvalidatePixelsButNotText()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var blank = service.Capture(session.Id);
        var text = service.Observe(session.Id).Text;
        connection.EmitOutput(KittyRed);
        var red = service.Capture(session.Id);
        connection.EmitOutput(KittyGreen);
        var green = service.Capture(session.Id);
        using var bitmap = Decode(green);
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(2, 2));
        Assert.NotEqual(red.Frame.Hash, green.Frame.Hash);
        connection.EmitOutput("\u001b_Ga=d,d=I,i=3\u001b\\");

        Assert.Empty(service.Observe(session.Id).Images);
        Assert.Equal(text, service.Observe(session.Id).Text);
        Assert.Equal(blank.Frame.Hash, service.Capture(session.Id).Frame.Hash);
    }

    [Fact]
    public async Task Observe_ImagesAndWaitForChangeFollowTheActiveBuffer()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput(KittyRed);
        var primary = service.Capture(session.Id);
        var primaryImage = Assert.Single(service.Observe(session.Id).Images);
        Assert.Equal("KittyGraphics", primaryImage.Protocol);
        Assert.False(service.Observe(session.Id).AlternateBufferActive);
        using (var bitmap = Decode(primary))
        {
            Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
        }

        var waiting = service.WaitForChangeAsync(session.Id, primary.Frame.Hash, TimeSpan.FromSeconds(2));
        connection.EmitOutput("\u001b[?1049h" + KittyGreen);
        var alternateChange = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(alternateChange.Matched);
        Assert.NotEqual(primary.Frame.Hash, alternateChange.Frame.Hash);
        var alternate = service.Observe(session.Id);
        Assert.True(alternate.AlternateBufferActive);
        Assert.Equal("KittyGraphics", Assert.Single(alternate.Images).Protocol);
        using (var bitmap = Decode(service.Capture(session.Id)))
        {
            Assert.Equal(SKColors.Lime, bitmap.GetPixel(2, 2));
        }

        connection.EmitOutput("\u001b[?1049l");
        var restored = service.Observe(session.Id);
        Assert.False(restored.AlternateBufferActive);
        Assert.Equal("KittyGraphics", Assert.Single(restored.Images).Protocol);
        using var restoredBitmap = Decode(service.Capture(session.Id));
        Assert.Equal(SKColors.Red, restoredBitmap.GetPixel(2, 2));
    }

    [Fact]
    public async Task ProbeAsync_UsesActiveAlternateBufferGraphicsAndHidesInactiveImages()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        connection.EmitOutput(KittyRed);
        var primaryHash = service.Capture(session.Id).Frame.Hash;
        connection.OnWrite = text =>
        {
            if (text == "go")
            {
                connection.EmitOutput("\u001b[?1049h" + KittyGreen);
            }
        };

        var result = await service.ProbeAsync(session.Id, new() { Type = "text", Text = "go" },
            TimeSpan.FromSeconds(2));

        Assert.True(result.Matched);
        Assert.NotEqual(primaryHash, result.Frame.Hash);
        var alternate = service.Observe(session.Id);
        Assert.True(alternate.AlternateBufferActive);
        Assert.Equal("KittyGraphics", Assert.Single(alternate.Images).Protocol);
        using (var bitmap = Decode(service.Capture(session.Id)))
        {
            Assert.Equal(SKColors.Lime, bitmap.GetPixel(2, 2));
        }

        connection.OnWrite = null;
        connection.EmitOutput("\u001b[?1049l");
        var restored = service.Observe(session.Id);
        Assert.False(restored.AlternateBufferActive);
        Assert.Equal("KittyGraphics", Assert.Single(restored.Images).Protocol);
        using var restoredBitmap = Decode(service.Capture(session.Id));
        Assert.Equal(SKColors.Red, restoredBitmap.GetPixel(2, 2));
    }

    [Fact]
    public async Task Observe_EncodedKittyUnknownDimensionsAreNullWhileCaptureDecodesPixels()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        using var source = new SKBitmap(2, 3);
        source.Erase(SKColors.Red);
        using var png = source.Encode(SKEncodedImageFormat.Png, 100);
        connection.EmitOutput($"\u001b_Ga=T,f=100,i=5,c=1,r=1,C=1;{Convert.ToBase64String(png.ToArray())}\u001b\\");

        var image = Assert.Single(service.Observe(session.Id).Images);
        Assert.Equal("KittyGraphics", image.Protocol);
        Assert.Null(image.PixelWidth);
        Assert.Null(image.PixelHeight);
        using var bitmap = Decode(service.Capture(session.Id));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(7, 0, 2, 1)]
    [InlineData(0, 2, 1, 2)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 1)]
    public async Task Capture_RejectsInvalidCrop(int column, int row, int columns, int rows)
    {
        await using var service = new TerminalAutomationService(() => new FakeConnection());
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.Capture(session.Id, new(column, row, columns, rows)));
    }

    [Fact]
    public async Task Capture_LargeGridRequiresCropWithinPixelBudget()
    {
        await using var service = new TerminalAutomationService(() => new FakeConnection());
        var session = await service.StartAsync(new("fake", Columns: 500, Rows: 200));
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => service.Capture(session.Id));
        Assert.Contains("4194304", error.Message, StringComparison.Ordinal);
        var crop = service.Capture(session.Id, new(499, 199, 1, 1));
        using var bitmap = Decode(crop);
        Assert.Equal((crop.Frame.PixelWidth, crop.Frame.PixelHeight), (bitmap.Width, bitmap.Height));
        Assert.True((long)bitmap.Width * bitmap.Height <= 4_194_304);
    }

    [Fact]
    public async Task StartAndResize_PropagateActualCellPixelGeometryToConnectionAndEngine()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        var frame = service.Capture(session.Id).Frame;
        var cellWidth = (int)Math.Max(1, Math.Round(frame.CellWidth));
        var cellHeight = (int)Math.Max(1, Math.Round(frame.CellHeight));
        Assert.Equal((8, 3, 8 * cellWidth, 3 * cellHeight), Assert.Single(connection.PixelResizeCalls));
        service.Resize(session.Id, 10, 4);
        Assert.Equal((10, 4, 10 * cellWidth, 4 * cellHeight), connection.PixelResizeCalls[^1]);
        connection.EmitOutput("\u001b[16t");
        Assert.Equal($"\u001b[6;{cellHeight};{cellWidth}t", Assert.Single(connection.Writes));
    }

    [Fact]
    public async Task Observe_RetainsOnlyLast32DiagnosticsInOrder()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        connection.EmitOutput("\u001b]1337;File=inline=0:AA==\u0007");
        for (var index = 0; index < 31; index++)
        {
            connection.EmitOutput("\u001b]1337;File=inline=1:@@@\u0007");
        }

        var full = service.Observe(session.Id).Diagnostics;
        Assert.Equal(32, full.Count);
        Assert.Equal("image.osc1337.non-inline-unsupported", full[0].Code);
        connection.EmitOutput("\u001b]9;4;st=1;sz=1;AQ==\u0007");
        var retained = service.Observe(session.Id).Diagnostics;
        Assert.Equal(32, retained.Count);
        Assert.All(retained.Take(31), diagnostic => Assert.Equal("image.osc1337.rejected", diagnostic.Code));
        Assert.Equal("image.conemu.rejected", retained[^1].Code);
    }

    [Theory]
    [InlineData(SixelRed)]
    [InlineData(KittyRed)]
    public async Task WaitForChangeAsync_MatchesGraphicsOnlyOutput(string output)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        var baseline = service.Capture(session.Id).Frame.Hash;
        var text = service.Observe(session.Id).Text;
        var pending = service.WaitForChangeAsync(session.Id, baseline, TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        connection.EmitOutput(output);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Matched);
        Assert.NotEqual(baseline, result.Frame.Hash);
        Assert.Equal(service.Capture(session.Id).Frame.Hash, result.Frame.Hash);
        Assert.Equal(text, service.Observe(session.Id).Text);
        Assert.True(result.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public async Task WaitForChangeAsync_IgnoresCursorAndTitleOnlyOutputAndTimesOut()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var baseline = service.Capture(session.Id).Frame;
        var pending = service.WaitForChangeAsync(session.Id, baseline.Hash.ToLowerInvariant(), TimeSpan.FromMilliseconds(20));
        connection.EmitOutput("\u001b[2;2H\u001b]2;new title\u0007");
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Matched);
        Assert.Equal(baseline.Hash, result.Frame.Hash);
        Assert.True(service.Observe(session.Id).Revision > baseline.Revision);
    }

    [Fact]
    public async Task VisualOperations_CroppedRegionIgnoresGraphicsOutsideIt()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        var region = new TerminalAutomationRegion(4, 2, 1, 1);
        var croppedHash = service.Capture(session.Id, region).Frame.Hash;
        var fullHash = service.Capture(session.Id).Frame.Hash;
        var waiting = service.WaitForChangeAsync(session.Id, croppedHash, TimeSpan.FromMilliseconds(20), region);
        connection.EmitOutput(KittyRed);
        Assert.False((await waiting).Matched);
        Assert.Equal(croppedHash, service.Capture(session.Id, region).Frame.Hash);
        Assert.NotEqual(fullHash, service.Capture(session.Id).Frame.Hash);
        connection.OnWrite = text =>
        {
            if (text == "go")
            {
                connection.EmitOutput(KittyGreen);
            }
        };
        var probe = await service.ProbeAsync(session.Id, new() { Type = "text", Text = "go" },
            TimeSpan.FromMilliseconds(20), region);
        Assert.False(probe.Matched);
        Assert.Equal(croppedHash, probe.Frame.Hash);
        var pending = service.MeasureAsync(session.Id, 64, 16, region);
        connection.EmitOutput(KittyRed);
        var measured = await pending;
        Assert.Equal(0, measured.ChangedFrames);
        Assert.Equal(Encoding.UTF8.GetByteCount(KittyRed), measured.OutputBytes);
        Assert.All(measured.Samples, sample => Assert.Equal(croppedHash, sample.Frame.Hash));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(3000000000L)]
    public async Task WaitForChangeAsync_AcceptsInclusiveTimeoutBoundaries(long ticks)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var hash = service.Capture(session.Id).Frame.Hash;
        connection.EmitOutput(SixelRed);
        var result = await service.WaitForChangeAsync(session.Id, hash, TimeSpan.FromTicks(ticks));
        Assert.True(result.Matched);
        Assert.NotEqual(hash, result.Frame.Hash);
    }

    [Fact]
    public async Task WaitForChangeAsync_CancellationAndFaultArePropagated()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var hash = service.Capture(session.Id).Frame.Hash;
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.WaitForChangeAsync(session.Id, hash, TimeSpan.FromSeconds(2),
            cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var faulted = service.WaitForChangeAsync(session.Id, hash, TimeSpan.FromSeconds(2));
        var expected = new IOException("fake graphics output failure");
        connection.EmitFault(expected);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => faulted);
        Assert.Same(expected, error.InnerException);
    }

    [Fact]
    public async Task WaitForChangeAsync_ExitWaitsForFinalGraphicsAndOutputDrain()
    {
        var connection = new FakeConnection { CompleteOutputOnExit = false };
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var hash = service.Capture(session.Id).Frame.Hash;
        var pending = service.WaitForChangeAsync(session.Id, hash, TimeSpan.FromSeconds(2));
        connection.EmitExit(7);
        Assert.False(pending.IsCompleted);
        connection.EmitOutput(KittyRed);
        connection.CompleteOutput();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Matched);
        Assert.NotEqual(hash, result.Frame.Hash);
        Assert.Equal(7, service.Observe(session.Id).ExitCode);
    }

    [Fact]
    public async Task WaitForChangeAsync_DrainedExitCompletesUnmatched()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var hash = service.Capture(session.Id).Frame.Hash;
        var pending = service.WaitForChangeAsync(session.Id, hash, TimeSpan.FromMinutes(5));
        connection.EmitExit(0);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Matched);
        Assert.Equal(hash, result.Frame.Hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task WaitForChangeAsync_RejectsInvalidHash(string hash)
    {
        await using var service = new TerminalAutomationService(() => new FakeConnection());
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        await Assert.ThrowsAsync<ArgumentException>(() => service.WaitForChangeAsync(session.Id, hash, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(300001)]
    public async Task VisualWaitAndProbe_RejectInvalidTimeoutBeforeSendingInput(int timeout)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var hash = service.Capture(session.Id).Frame.Hash;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.WaitForChangeAsync(session.Id, hash, TimeSpan.FromMilliseconds(timeout)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ProbeAsync(session.Id, new() { Type = "text", Text = "go" }, TimeSpan.FromMilliseconds(timeout)));
        Assert.Empty(connection.Writes);
    }

    [Theory]
    [InlineData("text", "go")]
    [InlineData("paste", "\u001b[200~go\u001b[201~")]
    [InlineData("key", "\u001b[A")]
    [InlineData("click", "\u001b[<0;2;2M\u001b[<0;2;2m")]
    [InlineData("mouse", "\u001b[<0;2;2M")]
    public async Task ProbeAsync_RecordsBaselineBeforeExactInputAndSynchronousGraphicsResponse(string type, string expected)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        connection.EmitOutput("\u001b[?2004h\u001b[?1000h\u001b[?1006h");
        var baseline = service.Capture(session.Id).Frame.Hash;
        var text = service.Observe(session.Id).Text;
        connection.OnWrite = _ => connection.EmitOutput(SixelRed);

        var result = await service.ProbeAsync(session.Id, new()
        {
            Type = type,
            Text = "go",
            Key = "Up",
            Event = "press",
            Column = 1,
            Row = 1,
        }, TimeSpan.FromSeconds(2));

        Assert.Equal(expected, Assert.Single(connection.Writes));
        Assert.True(result.Matched);
        Assert.NotEqual(baseline, result.Frame.Hash);
        Assert.Equal(text, service.Observe(session.Id).Text);
    }

    [Fact]
    public async Task ProbeAsync_IdleResponseTimesOutAndCancelledProbeDoesNotWrite()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var hash = service.Capture(session.Id).Frame.Hash;
        var input = new TerminalAutomationInput { Type = "text", Text = "go" };
        var result = await service.ProbeAsync(session.Id, input, TimeSpan.FromMilliseconds(20));
        Assert.False(result.Matched);
        Assert.Equal(hash, result.Frame.Hash);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ProbeAsync(session.Id, input, TimeSpan.FromSeconds(1), cancellationToken: cancellation.Token));
        Assert.Equal("go", Assert.Single(connection.Writes));
    }

    [Fact]
    public async Task ProbeAsync_StartsTimeoutAfterBlockedInputWriteCompletes()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWriteToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnWrite = _ => writeStarted.TrySetResult();
        connection.AfterWriteAsync = async cancellationToken =>
            await allowWriteToComplete.Task.WaitAsync(cancellationToken);

        var pending = service.ProbeAsync(session.Id, new() { Type = "text", Text = "go" },
            TimeSpan.FromMilliseconds(250));
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(pending.IsCompleted);

        allowWriteToComplete.TrySetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        connection.EmitOutput(SixelRed);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Matched);
        Assert.Equal("go", Assert.Single(connection.Writes));
        using var bitmap = Decode(service.Capture(session.Id));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
        Assert.Equal(service.Capture(session.Id).Frame.Hash, result.Frame.Hash);
    }

    [Fact]
    public async Task ProbeAsync_TimedOutResultUsesLatestRenderedRegion()
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        var region = new TerminalAutomationRegion(3, 1, 1, 1);
        var initialRegion = service.Capture(session.Id, region).Frame;
        var initialFull = service.Capture(session.Id).Frame;
        connection.OnWrite = text =>
        {
            if (text == "go")
            {
                connection.EmitOutput(KittyRed);
            }
        };

        var result = await service.ProbeAsync(session.Id, new() { Type = "text", Text = "go" },
            TimeSpan.FromMilliseconds(20), region);

        var latestRegion = service.Capture(session.Id, region).Frame;
        var latestFull = service.Capture(session.Id);
        Assert.False(result.Matched);
        Assert.Equal(initialRegion.Hash, latestRegion.Hash);
        Assert.Equal(latestRegion.Hash, result.Frame.Hash);
        Assert.Equal(latestRegion.Revision, result.Frame.Revision);
        Assert.NotEqual(initialFull.Hash, latestFull.Frame.Hash);
        using var bitmap = Decode(latestFull);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("text")]
    [InlineData("paste")]
    [InlineData("key")]
    [InlineData("mouse")]
    public async Task ProbeAsync_RejectsUnsupportedOrIncompleteInput(string type)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ProbeAsync(session.Id, new() { Type = type }, TimeSpan.FromSeconds(1)));
        Assert.Empty(connection.Writes);
    }

    [Fact]
    public async Task MeasureAsync_IdleHasZeroChangesAndConcreteSampleStatistics()
    {
        await using var service = new TerminalAutomationService(() => new FakeConnection());
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        var hash = service.Capture(session.Id).Frame.Hash;
        var result = await service.MeasureAsync(session.Id, 16, 16);
        Assert.Equal(0, result.ChangedFrames);
        Assert.Equal(0, result.ObservedChangesPerSecond);
        Assert.Equal(0, result.OutputBytes);
        Assert.Equal(0, result.ParseMilliseconds);
        Assert.Null(result.ChangeIntervalMilliseconds);
        Assert.Equal(16, result.IntervalMilliseconds);
        Assert.Equal(result.DurationMilliseconds, result.LongestUnchangedMilliseconds);
        Assert.Equal(2, result.Samples.Count);
        Assert.Equal(0, result.Samples[0].ElapsedMilliseconds);
        Assert.All(result.Samples.Zip(result.Samples.Skip(1)), pair =>
            Assert.True(pair.Second.ElapsedMilliseconds > pair.First.ElapsedMilliseconds));
        Assert.True(result.Samples[^1].ElapsedMilliseconds <= result.DurationMilliseconds);
        Assert.All(result.Samples, sample =>
        {
            Assert.Equal(hash, sample.Frame.Hash);
            Assert.False(sample.Frame.IncludesCursor);
            Assert.True(sample.Frame.RenderMilliseconds >= 0);
        });
        AssertTiming(result.RenderMilliseconds, result.Samples.Select(sample => sample.Frame.RenderMilliseconds));
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData(KittyGreen)]
    public async Task MeasureAsync_PersistentOutputAfterBaselineCountsOneChangeAndExactBytes(string output)
    {
        var connection = new FakeConnection();
        await using var service = new TerminalAutomationService(() => connection);
        var session = await service.StartAsync(new("fake", Columns: 8, Rows: 3));
        // The async method synchronously captures its initial sample before returning at the first delay.
        var pending = service.MeasureAsync(session.Id, 64, 16);
        connection.EmitOutput(output);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.ChangedFrames);
        Assert.Equal(Encoding.UTF8.GetByteCount(output), result.OutputBytes);
        Assert.True(result.ParseMilliseconds >= 0);
        Assert.Equal(1000d / result.DurationMilliseconds, result.ObservedChangesPerSecond);
        Assert.Equal(2, result.Samples.Select(sample => sample.Frame.Hash).Distinct().Count());
        Assert.Equal(service.Capture(session.Id).Frame.Hash, result.Samples[^1].Frame.Hash);
        var changed = Assert.Single(result.Samples.Zip(result.Samples.Skip(1)),
            pair => pair.First.Frame.Hash != pair.Second.Frame.Hash);
        var interval = changed.Second.ElapsedMilliseconds;
        Assert.Equal(new TerminalAutomationTiming(interval, interval, interval, interval), result.ChangeIntervalMilliseconds);
        AssertTiming(result.RenderMilliseconds, result.Samples.Select(sample => sample.Frame.RenderMilliseconds));
    }

    [Theory]
    [InlineData(15, 16)]
    [InlineData(30001, 1000)]
    [InlineData(100, 15)]
    [InlineData(2000, 1001)]
    [InlineData(16, 17)]
    [InlineData(9585, 16)]
    [InlineData(30000, 16)]
    public async Task MeasureAsync_RejectsInvalidDurationIntervalAndCeilingSampleBudget(int duration, int interval)
    {
        await using var service = new TerminalAutomationService(() => new FakeConnection());
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MeasureAsync(session.Id, duration, interval));
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(9584, 16)]
    [InlineData(30000, 1000)]
    public async Task MeasureAsync_ValidBoundaryReachesCancellationInsteadOfValidationError(int duration, int interval)
    {
        await using var service = new TerminalAutomationService(() => new FakeConnection());
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.MeasureAsync(session.Id, duration, interval, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task MeasureAsync_PendingMeasurementHonorsCancellation()
    {
        await using var service = new TerminalAutomationService(() => new FakeConnection());
        var session = await service.StartAsync(new("fake", Columns: 4, Rows: 2));
        using var cancellation = new CancellationTokenSource();
        var pending = service.MeasureAsync(session.Id, 30000, 1000, cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    internal static SKBitmap Decode(TerminalAutomationCapture capture) =>
        SKBitmap.Decode(Convert.FromBase64String(capture.PngBase64))
        ?? throw new InvalidDataException("Capture was not a PNG.");

    private static void AssertTiming(TerminalAutomationTiming timing, IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        Assert.Equal(sorted[0], timing.Minimum);
        Assert.Equal(sorted[^1], timing.Maximum);
        Assert.Equal(sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], timing.P95);
        var middle = sorted.Length / 2;
        Assert.Equal(sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle], timing.Median);
    }
}
