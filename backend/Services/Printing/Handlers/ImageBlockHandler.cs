using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class ImageBlockHandler(ILogger<ImageBlockHandler> logger) : IBlockHandler
{
    private const int DefaultMaxWidth = 500;
    private const int DefaultMaxHeight = 500;

    public ContentType Type => ContentType.Image;

    public async Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        if (string.IsNullOrEmpty(item.Content))
            return;

        var imageBytes = await ProcessImageContentAsync(item.Content, item.ImageOptions);
        if (imageBytes != null)
        {
            var legacy = item.ImageOptions?.UseLegacyMode ?? true;
            ctx.Add(ctx.Emitter.PrintImage(imageBytes, true, isLegacy: legacy));
        }
    }

    private async Task<byte[]?> ProcessImageContentAsync(string content, ImageOptions? options)
    {
        try
        {
            byte[] imageBytes;
            if (content.StartsWith("data:image"))
                imageBytes = Convert.FromBase64String(content.Split(',')[1]);
            else if (content.StartsWith("base64,"))
                imageBytes = Convert.FromBase64String(content[7..]);
            else
                imageBytes = Convert.FromBase64String(content);

            var opts = options ?? new ImageOptions();
            using var image = Image.Load(imageBytes);
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
        catch (FormatException ex)
        {
            logger.LogError(ex, "Invalid base64 image format. Content length: {Length}", content.Length);
            throw;
        }
        catch (UnknownImageFormatException ex)
        {
            logger.LogError(ex, "Unsupported image format. Content prefix: {Prefix}", content[..Math.Min(50, content.Length)]);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process image. Content length: {Length}", content.Length);
            throw;
        }
    }
}
