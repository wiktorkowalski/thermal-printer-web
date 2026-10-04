using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ThermalPrinterWeb.Tests.Support;

// Image files for image blocks.
internal static class TestImages
{
    public static byte[] Encode(IImageEncoder encoder, int width = 8, int height = 8)
    {
        using var image = new Image<Rgba32>(width, height);
        return Save(image, encoder);
    }

    public static byte[] Png(int width = 8, int height = 8) => Encode(new PngEncoder(), width, height);

    public static string PngBase64(int width = 8, int height = 8) => Convert.ToBase64String(Png(width, height));

    // Noise does not compress: a cut in the middle of the file lands inside the pixel data.
    public static byte[] NoisePng(int seed)
    {
        using var image = new Image<Rgba32>(64, 64);
        var random = new Random(seed);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (ref var pixel in rows.GetRowSpan(y))
                    pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        });
        return Save(image, new PngEncoder());
    }

    private static byte[] Save(Image image, IImageEncoder encoder)
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }
}
