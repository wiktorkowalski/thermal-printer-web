using System.Text;
using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Tests;

// Image blocks accept PNG and JPEG only. The format comes from the bytes.
public sealed class ImageBlockHandlerTests
{
    // GS v 0: the raster command ESCPOS_NET emits in legacy mode.
    private static readonly byte[] RasterCommand = [0x1D, 0x76, 0x30];

    private static byte[] Encode(IImageEncoder encoder, int width = 8, int height = 8)
    {
        using var image = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    private static byte[] Png(int width = 8, int height = 8) => Encode(new PngEncoder(), width, height);
    private static byte[] Jpeg() => Encode(new JpegEncoder());
    private static byte[] Gif() => Encode(new GifEncoder());

    private static BlockContext NewContext() => new(new EPSON(), null);

    private static Task RunAsync(string content, BlockContext ctx)
        => new ImageBlockHandler(NullLogger<ImageBlockHandler>.Instance)
            .HandleAsync(new PrintContent { Type = ContentType.Image, Content = content }, ctx);

    private static async Task<BlockContext> RunAsync(string content)
    {
        var ctx = NewContext();
        await RunAsync(content, ctx);
        return ctx;
    }

    private static async Task AssertPrintsAsync(string content)
    {
        var ctx = await RunAsync(content);
        Assert.Contains(RasterCommand.AsSpan(), ByteSplicer.Combine([.. ctx.Output]).AsSpan());
    }

    private static async Task<InvalidDataException> AssertRejectedAsync(string content)
    {
        var ctx = NewContext();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(content, ctx));
        Assert.Empty(ctx.Output);
        return ex;
    }

    [Fact]
    public async Task Png_Prints() => await AssertPrintsAsync(Convert.ToBase64String(Png()));

    [Fact]
    public async Task Jpeg_Prints() => await AssertPrintsAsync(Convert.ToBase64String(Jpeg()));

    [Fact]
    public async Task Png_WiderThanThePrintHead_IsResizedAndPrints()
    {
        var ctx = await RunAsync(Convert.ToBase64String(Png(width: 1200, height: 40)));

        // GS v 0 m xL xH yL yH: 576 dots = 72 bytes per row.
        var bytes = ByteSplicer.Combine([.. ctx.Output]);
        var at = bytes.AsSpan().IndexOf(RasterCommand);
        Assert.True(at >= 0);
        Assert.Equal(72, bytes[at + 4] | (bytes[at + 5] << 8));
    }

    [Theory]
    [InlineData("data:image/png;base64,")]
    [InlineData("data:image/jpeg;base64,")]
    [InlineData("base64,")]
    public async Task Png_WithPrefix_Prints(string prefix)
        => await AssertPrintsAsync(prefix + Convert.ToBase64String(Png()));

    [Fact]
    public async Task Png_DeclaredAsGif_Prints()
        => await AssertPrintsAsync("data:image/gif;base64," + Convert.ToBase64String(Png()));

    [Fact]
    public async Task Gif_DeclaredAsPng_IsRejected()
    {
        var ex = await AssertRejectedAsync("data:image/png;base64," + Convert.ToBase64String(Gif()));
        Assert.Contains("GIF", ex.Message);
    }

    public static TheoryData<string, byte[]> OtherFormats => new()
    {
        { "GIF", Gif() },
        { "BMP", Encode(new BmpEncoder()) },
        { "Webp", Encode(new WebpEncoder()) },
        { "TIFF", Encode(new TiffEncoder()) },
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
        // Noise does not compress, so the cut lands inside the pixel data.
        using var image = new Image<Rgba32>(64, 64);
        var random = new Random(34);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (ref var pixel in rows.GetRowSpan(y))
                    pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        });
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        var png = ms.ToArray();

        await AssertRejectedAsync(Convert.ToBase64String(png[..(png.Length / 2)]));
    }

    [Theory]
    [InlineData(8)]  // signature only
    [InlineData(20)] // cut inside the header chunk
    public async Task Png_HeaderOnly_IsRejected(int length)
        => await AssertRejectedAsync(Convert.ToBase64String(Png()[..length]));

    [Fact]
    public async Task Jpeg_HeaderOnly_IsRejected()
        => await AssertRejectedAsync(Convert.ToBase64String(Jpeg()[..4]));
}
