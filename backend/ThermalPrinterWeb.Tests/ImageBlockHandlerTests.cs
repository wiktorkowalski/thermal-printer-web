using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Tests;

public sealed class ImageBlockHandlerTests
{
    // GS v 0: the raster command ESCPOS_NET emits in legacy mode.
    private static readonly byte[] RasterCommand = [0x1D, 0x76, 0x30];

    private static byte[] Jpeg() => TestImages.Encode(new JpegEncoder());
    private static byte[] Gif() => TestImages.Encode(new GifEncoder());

    private static Task RunAsync(string content, BlockContext ctx, ImageOptions? options = null)
        => new ImageBlockHandler().HandleAsync(TestBlocks.ImageBlock(content, options), ctx);

    private static async Task<BlockContext> RunAsync(string content, ImageOptions? options = null)
    {
        var ctx = TestBlocks.NewContext();
        await RunAsync(content, ctx, options);
        return ctx;
    }

    // GS v 0 m xL xH yL yH: width in bytes (8 dots each), height in dots.
    private static (int WidthBytes, int Height) RasterSize(BlockContext ctx)
    {
        var bytes = ctx.OutputBytes();
        var at = bytes.AsSpan().IndexOf(RasterCommand);
        Assert.True(at >= 0);
        return (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 4)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 6)));
    }

    // A PNG that only declares its size: valid header, one compressed pixel row of zeros.
    private static byte[] PngDeclaring(int width, int height, byte bitDepth = 8, (string Type, byte[] Data)? extraChunk = null)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = 6; // RGBA
        WriteChunk(ms, "IHDR", header);
        if (extraChunk is { } chunk)
            WriteChunk(ms, chunk.Type, chunk.Data);

        WriteChunk(ms, "IDAT", Deflate(new byte[16]));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return ms.ToArray();
    }

    // Compressed text chunks: 64 MB of zeros in about 64 KB.
    public static TheoryData<string, byte[]> TextBombChunks()
    {
        var zeros = Deflate(new byte[64 * 1024 * 1024]);
        return new()
        {
            // keyword, NUL, compression method, data
            { "zTXt", [.. "Comment"u8, 0, 0, .. zeros] },
            // keyword, NUL, compressed flag, method, empty language, NUL, empty translated keyword, NUL, data
            { "iTXt", [.. "Comment"u8, 0, 1, 0, 0, 0, .. zeros] }
        };
    }

    [Theory]
    [MemberData(nameof(TextBombChunks))]
    public async Task Png_CompressedTextChunk_IsNotInflated(string type, byte[] data)
    {
        // Oversize too, so the handler throws before its first await and the count is for this thread.
        var bomb = Convert.ToBase64String(PngDeclaring(30000, 30000, extraChunk: (type, data)));
        var ctx = TestBlocks.NewContext();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = await Assert.ThrowsAsync<PrintContentException>(() => RunAsync(bomb, ctx));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("30000x30000 pixels is over the limit", ex.Message);
        Assert.True(allocated < 4 * 1024 * 1024, $"allocated {allocated} bytes");
    }

    [Theory]
    [InlineData(0, 576, "maxWidth 0")]
    [InlineData(-1, 576, "maxWidth -1")]
    [InlineData(576, 0, "maxHeight 0")]
    [InlineData(576, int.MinValue, "maxHeight -2147483648")]
    public async Task MaxSize_BelowOne_IsRejected(int maxWidth, int maxHeight, string expected)
    {
        var ctx = TestBlocks.NewContext();
        var ex = await Assert.ThrowsAsync<PrintContentException>(() => RunAsync(
            Convert.ToBase64String(TestImages.Png()), ctx, new ImageOptions { MaxWidth = maxWidth, MaxHeight = maxHeight }));

        Assert.Equal($"imageOptions.{expected} must be at least 1", ex.Message);
        Assert.Empty(ctx.Output);
    }

    [Theory]
    [InlineData(576, 72)]
    [InlineData(577, 72)]
    [InlineData(568, 71)]
    public async Task MaxWidth_AtTheHeadWidth_IsTheUpperLimit(int maxWidth, int expectedWidthBytes)
    {
        var ctx = await RunAsync(
            Convert.ToBase64String(TestImages.Png(width: 1200, height: 40)),
            new ImageOptions { MaxWidth = maxWidth });

        Assert.Equal(expectedWidthBytes, RasterSize(ctx).WidthBytes);
    }

    [Fact]
    public async Task Jpeg_PhotoSized12Megapixels_IsResizedAndPrints()
    {
        var ctx = await RunAsync(Convert.ToBase64String(TestImages.Encode(new JpegEncoder(), 4032, 3024)));

        Assert.Equal((72, 432), RasterSize(ctx));
        // The paper limit counts the printed height.
        Assert.Equal(432, ctx.PaperDots);
    }

    [Theory]
    [InlineData(432, true)]
    [InlineData(431, false)]
    public async Task Image_OverThePaperLimit_IsRejectedBeforeTheDecode(int dotsLeft, bool printed)
    {
        var ctx = TestBlocks.NewContext();
        ctx.AddPaper(PaperLength.MaxDots - dotsLeft);
        // 4032 x 3024 prints 432 dots high.
        var run = RunAsync(Convert.ToBase64String(PngDeclaring(4032, 3024)), ctx);

        if (printed)
        {
            await run;
            Assert.Equal(PaperLength.MaxDots, ctx.PaperDots);
            Assert.NotEmpty(ctx.Output);
            return;
        }

        var ex = await Assert.ThrowsAsync<PrintContentException>(() => run);
        Assert.Equal("the document is over the limit of 32000 dots of paper (4 m)", ex.Message);
        Assert.Empty(ctx.Output);
    }

    // The decoder checks the CRC of every critical chunk.
    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];

        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        stream.Write(typeAndData);

        var crc = 0xFFFFFFFFu;
        foreach (var b in typeAndData)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }

    private static async Task AssertPrintsAsync(string content)
    {
        var ctx = await RunAsync(content);
        Assert.Contains(RasterCommand.AsSpan(), ctx.OutputBytes().AsSpan());
    }

    private static async Task<PrintContentException> AssertRejectedAsync(string content)
    {
        var ctx = TestBlocks.NewContext();
        var ex = await Assert.ThrowsAsync<PrintContentException>(() => RunAsync(content, ctx));
        Assert.Empty(ctx.Output);
        return ex;
    }

    [Fact]
    public async Task Png_Prints() => await AssertPrintsAsync(Convert.ToBase64String(TestImages.Png()));

    [Fact]
    public async Task Jpeg_Prints() => await AssertPrintsAsync(Convert.ToBase64String(Jpeg()));

    [Fact]
    public async Task Png_WiderThanThePrintHead_IsResizedAndPrints()
    {
        var ctx = await RunAsync(Convert.ToBase64String(TestImages.Png(width: 1200, height: 40)));

        // 576 dots = 72 bytes per row.
        Assert.Equal(72, RasterSize(ctx).WidthBytes);
    }

    [Fact]
    public async Task Png_HeaderDeclares30000x30000_IsRejectedWithoutThePixelBuffer()
    {
        var bomb = Convert.ToBase64String(PngDeclaring(30000, 30000));
        Assert.True(bomb.Length < 200);
        var ctx = TestBlocks.NewContext();

        // The handler throws before its first await, so the count is for this thread.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = await Assert.ThrowsAsync<PrintContentException>(() => RunAsync(bomb, ctx));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("30000x30000 pixels is over the limit", ex.Message);
        Assert.Contains("format PNG,", ex.Message);
        Assert.Empty(ctx.Output);
        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes");
    }

    [Theory]
    [InlineData(16384, 1, false)]
    [InlineData(16385, 1, true)]
    [InlineData(1, 16384, false)]
    [InlineData(1, 16385, true)]
    [InlineData(8192, 6144, false)]  // 50.3 MP, the pixel limit
    [InlineData(8193, 6144, true)]
    [InlineData(8192, 6145, true)]
    [InlineData(8000, 8000, true)]   // 64 MP
    [InlineData(8000, 6000, false)]  // 48 MP phone photos
    [InlineData(8064, 6048, false)]
    [InlineData(8160, 6120, false)]  // 50 MP phone photos
    [InlineData(8160, 6144, false)]
    [InlineData(16382, 3072, false)] // the longest panorama that passes
    [InlineData(16382, 3628, true)]  // full phone panorama, 59 MP
    [InlineData(int.MaxValue, int.MaxValue, true)]
    public void PixelsOverLimit_AtTheBoundaries(int width, int height, bool expected)
        => Assert.Equal(expected, ImageBlockHandler.PixelsOverLimit(width, height));

    [Fact]
    public async Task Png_PhotoSized12Megapixels_IsResizedAndPrints()
    {
        using var photo = new Image<L8>(4032, 3024);
        using var ms = new MemoryStream();
        photo.SaveAsPng(ms);

        var ctx = await RunAsync(Convert.ToBase64String(ms.ToArray()));

        Assert.Equal((72, 432), RasterSize(ctx));
    }

    // 48 MP passes the header check, but 16 bits per channel is 384 MB of pixels.
    [Fact]
    public async Task Png_OverTheAllocatorLimit_IsRejected()
    {
        var ex = await AssertRejectedAsync(Convert.ToBase64String(PngDeclaring(8000, 6000, bitDepth: 16)));
        Assert.Contains("decoding needs more than 256 MB (format PNG,", ex.Message);
    }

    [Fact]
    public async Task Base64_AtTheLengthLimit_PassesTheSizeCheck()
    {
        var ex = await AssertRejectedAsync(new string('A', ImageBlockHandler.MaxBase64Length));
        Assert.Contains($"format not supported (format unknown, {ImageBlockHandler.MaxBase64Length / 4 * 3} bytes)", ex.Message);
    }

    [Fact]
    public async Task Base64_OverTheLengthLimit_IsRejectedBeforeDecoding()
    {
        var ex = await AssertRejectedAsync(new string('A', ImageBlockHandler.MaxBase64Length + 4));
        Assert.Contains($"{ImageBlockHandler.MaxBase64Length + 4} base64 characters is over the limit", ex.Message);
    }

    [Fact]
    public async Task MaxWidth_OverTheHeadWidth_IsClampedToTheHead()
    {
        var ctx = await RunAsync(
            Convert.ToBase64String(TestImages.Png(width: 1200, height: 40)),
            new ImageOptions { MaxWidth = 100_000, MaxHeight = 100_000 });

        Assert.Equal(72, RasterSize(ctx).WidthBytes);
    }

    [Fact]
    public async Task MaxHeight_OverThePrintLimit_IsClamped()
    {
        var ctx = await RunAsync(
            Convert.ToBase64String(TestImages.Png(width: 8, height: ImageBlockHandler.MaxPrintHeight + 904)),
            new ImageOptions { MaxHeight = 100_000, PreserveAspectRatio = false });

        Assert.Equal(ImageBlockHandler.MaxPrintHeight, RasterSize(ctx).Height);
    }

    [Theory]
    [InlineData("data:image/png;base64,")]
    [InlineData("data:image/jpeg;base64,")]
    [InlineData("base64,")]
    public async Task Png_WithPrefix_Prints(string prefix)
        => await AssertPrintsAsync(prefix + Convert.ToBase64String(TestImages.Png()));

    [Fact]
    public async Task Png_DeclaredAsGif_Prints()
        => await AssertPrintsAsync("data:image/gif;base64," + Convert.ToBase64String(TestImages.Png()));

    [Fact]
    public async Task Gif_DeclaredAsPng_IsRejected()
    {
        var ex = await AssertRejectedAsync("data:image/png;base64," + Convert.ToBase64String(Gif()));
        Assert.Contains("GIF", ex.Message);
    }

    public static TheoryData<string, byte[]> OtherFormats => new()
    {
        { "GIF", Gif() },
        { "BMP", TestImages.Encode(new BmpEncoder()) },
        { "Webp", TestImages.Encode(new WebpEncoder()) },
        { "TIFF", TestImages.Encode(new TiffEncoder()) },
        { "unknown", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\" height=\"8\"/>") },
        { "unknown", [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60] },
        // BigTIFF header (version 43): the decoder named in the advisory.
        { "TIFF", [0x49, 0x49, 0x2B, 0x00, 0x08, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
    };

    [Theory]
    [MemberData(nameof(OtherFormats))]
    public async Task OtherFormat_IsRejected(string formatName, byte[] bytes)
    {
        var ex = await AssertRejectedAsync(Convert.ToBase64String(bytes));
        Assert.Contains($"format {formatName},", ex.Message);
        Assert.Contains($"{bytes.Length} bytes", ex.Message);
    }

    [Fact]
    public async Task ZeroBytes_AreRejected()
        => await AssertRejectedAsync("data:image/png;base64,");

    [Fact]
    public async Task EmptyContent_PrintsNothing()
    {
        var ctx = await RunAsync("");
        Assert.Empty(ctx.Output);
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("data:image/png")]
    public async Task InvalidBase64_IsRejected(string content)
    {
        var ex = await AssertRejectedAsync(content);
        Assert.DoesNotContain(content, ex.Message);
    }

    [Fact]
    public async Task Png_Truncated_IsRejected()
    {
        var png = TestImages.NoisePng(seed: 34);

        await AssertRejectedAsync(Convert.ToBase64String(png[..(png.Length / 2)]));
    }

    [Theory]
    [InlineData(8)]  // signature only
    [InlineData(20)] // cut inside the header chunk
    public async Task Png_HeaderOnly_IsRejected(int length)
        => await AssertRejectedAsync(Convert.ToBase64String(TestImages.Png()[..length]));

    [Fact]
    public async Task Jpeg_ArithmeticCoded_IsRejected()
    {
        // SOF0 (FF C0) -> SOF9 (FF C9): the decoder throws NotSupportedException, not ImageFormatException.
        var jpeg = Jpeg();
        var sof = jpeg.AsSpan().IndexOf<byte>([0xFF, 0xC0]);
        Assert.True(sof >= 0);
        jpeg[sof + 1] = 0xC9;

        var ex = await AssertRejectedAsync(Convert.ToBase64String(jpeg));
        Assert.Contains("file is damaged (format JPEG,", ex.Message);
    }

    [Fact]
    public async Task Jpeg_HeaderOnly_IsRejected()
        => await AssertRejectedAsync(Convert.ToBase64String(Jpeg()[..4]));
}
