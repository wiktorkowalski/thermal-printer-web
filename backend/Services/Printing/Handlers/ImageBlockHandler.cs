using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class ImageBlockHandler : IBlockHandler
{
    // 576 = full V330M print-head width (80mm head).
    private const int DefaultMaxWidth = 576;
    private const int DefaultMaxHeight = 576;

    // The pinned ImageSharp has open advisories in other decoders (BigTIFF loop): keep them off caller bytes.
    private static readonly Configuration PngAndJpegOnly = new(new PngConfigurationModule(), new JpegConfigurationModule());

    public ContentType Type => ContentType.Image;

    public async Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        // An image block with no picture yet (the editor inserts these) prints nothing.
        if (string.IsNullOrEmpty(item.Content))
            return;

        var imageBytes = await ProcessImageContentAsync(item.Content, item.ImageOptions);
        var legacy = item.ImageOptions?.UseLegacyMode ?? true;
        var highDensity = item.ImageOptions?.HighDensity ?? true;
        ctx.Add(ctx.Emitter.PrintImage(imageBytes, highDensity, isLegacy: legacy));
    }

    private static async Task<byte[]> ProcessImageContentAsync(string content, ImageOptions? options)
    {
        using var image = LoadPngOrJpeg(DecodeBase64(content));

        var opts = options ?? new ImageOptions();
        var maxWidth = opts.MaxWidth ?? DefaultMaxWidth;
        var maxHeight = opts.MaxHeight ?? DefaultMaxHeight;

        if (image.Width > maxWidth || image.Height > maxHeight)
        {
            if (opts.PreserveAspectRatio)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(maxWidth, maxHeight),
                    Mode = ResizeMode.Max
                }));
            }
            else
            {
                image.Mutate(x => x.Resize(maxWidth, maxHeight));
            }
        }

        using var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    private static Image LoadPngOrJpeg(byte[] imageBytes)
    {
        // The format comes from the bytes. A declared MIME type in a data URI is ignored.
        var format = Image.DetectFormat(imageBytes);
        if (format is not (PngFormat or JpegFormat))
            throw Rejected(format, imageBytes.Length, "format not supported");

        try
        {
            return Image.Load(PngAndJpegOnly, imageBytes);
        }
        // Damaged files also surface as NullReference, IndexOutOfRange and NotSupported from the decoders.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Rejected(format, imageBytes.Length, "file is damaged", ex);
        }
    }

    private static byte[] DecodeBase64(string content)
    {
        var base64 = content.StartsWith("data:", StringComparison.Ordinal) || content.StartsWith("base64,", StringComparison.Ordinal)
            ? content[(content.IndexOf(',') + 1)..]
            : content;
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new PrintContentException($"Image rejected: not valid base64 ({content.Length} characters).", ex);
        }
    }

    // No log here: PrinterService logs the failure once, with this message. Facts only, never the image content.
    private static PrintContentException Rejected(IImageFormat? format, int byteCount, string reason, Exception? inner = null)
        => new($"Image rejected: {reason} (format {format?.Name ?? "unknown"}, {byteCount} bytes). Send a PNG or JPEG.", inner);
}
