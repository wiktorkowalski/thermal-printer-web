using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class ImageBlockHandler(ILogger<ImageBlockHandler> logger) : IBlockHandler
{
    // 576 = full V330M print-head width (80mm head).
    private const int DefaultMaxWidth = 576;
    private const int DefaultMaxHeight = 576;

    // Only these two decoders ever run on caller bytes. The pinned ImageSharp
    // has open advisories in other decoders (BigTIFF loop), so they stay off
    // the path even if the format check below is wrong.
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

    private async Task<byte[]> ProcessImageContentAsync(string content, ImageOptions? options)
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

    private Image LoadPngOrJpeg(byte[] imageBytes)
    {
        // The format comes from the bytes. A declared MIME type in a data URI is ignored.
        var format = Image.DetectFormat(imageBytes);
        if (format is not (PngFormat or JpegFormat))
            throw Reject(format, imageBytes.Length, "format not supported");

        try
        {
            return Image.Load(PngAndJpegOnly, imageBytes);
        }
        catch (ImageFormatException)
        {
            throw Reject(format, imageBytes.Length, "file is damaged");
        }
    }

    private byte[] DecodeBase64(string content)
    {
        var base64 = content.StartsWith("data:", StringComparison.Ordinal) || content.StartsWith("base64,", StringComparison.Ordinal)
            ? content[(content.IndexOf(',') + 1)..]
            : content;
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            logger.LogWarning("Image rejected: not valid base64 ({Length} characters)", content.Length);
            throw new InvalidDataException("Image is not valid base64.");
        }
    }

    // One log line per rejected image: facts only, never the image content.
    private InvalidDataException Reject(IImageFormat? format, int byteCount, string reason)
    {
        var formatName = format?.Name ?? "unknown";
        logger.LogWarning("Image rejected: {Reason} (format {Format}, {ByteCount} bytes)", reason, formatName, byteCount);
        return new InvalidDataException($"Image rejected: {reason} (format {formatName}, {byteCount} bytes). Send a PNG or JPEG.");
    }
}
