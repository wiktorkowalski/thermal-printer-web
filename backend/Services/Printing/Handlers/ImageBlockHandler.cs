using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Processing;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class ImageBlockHandler : IBlockHandler
{
    // 576 = full V330M print-head width (80mm head).
    private const int HeadWidth = 576;
    private const int DefaultMaxHeight = 576;

    // Printed height limit for one image: 4096 dots = 512 mm of paper at 8 dots/mm.
    internal const int MaxPrintHeight = 4096;

    // Limits on the image the caller sends. The endpoint is public, so the header
    // must not decide how much memory a decode takes.
    // 16 MiB as base64 is 22.4 MB, below PrinterService.MaxRequestBodyBytes.
    internal const int MaxImageBytes = 16 * 1024 * 1024;
    internal const int MaxBase64Length = (MaxImageBytes + 2) / 3 * 4;
    // Phone photos must pass: 12 MP 4032x3024, 48 MP 8064x6048, 50 MP 8160x6144 and 8192x6144.
    // A panorama passes up to 16382x3072; a full 16382x3628 one (59 MP) does not.
    internal const int MaxSidePixels = 16384;
    internal const long MaxPixels = 8192 * 6144;
    // Second guard, for what the header check cannot see. The pixel limit as Rgba32 = 192 MiB.
    // The limit is per buffer: a progressive 4:4:4 JPEG at the pixel limit also holds its coefficient planes, 460 MB in total (measured).
    private const int AllocationLimitMegabytes = 256;

    // Jobs that wait for the decode slot. Each one holds its image: up to 16 MiB of bytes and the base64 text.
    internal const int MaxDecodeWaiters = 4;
    // One decode takes about 1 s, so a full queue clears in less time than this.
    internal static readonly TimeSpan DecodeWaitTimeout = TimeSpan.FromSeconds(10);

    // The pinned ImageSharp has open advisories in other decoders (BigTIFF loop): keep them off caller bytes.
    private static readonly Configuration PngAndJpegOnly = CreateConfiguration();

    // One queue for the process: an accepted photo takes up to 460 MB while it is decoded.
    internal static readonly DecodeQueue SharedQueue = new(MaxDecodeWaiters, DecodeWaitTimeout);

    private readonly DecodeQueue _queue;

    public ImageBlockHandler() : this(SharedQueue)
    {
    }

    // Tests pass their own queue.
    internal ImageBlockHandler(DecodeQueue queue) => _queue = queue;

    public ContentType Type => ContentType.Image;

    public async Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        // An image block with no picture yet (the editor inserts these) prints nothing.
        if (string.IsNullOrEmpty(item.Content))
            return;

        // Cheap checks first: a rejected image does not wait for the queue.
        var options = item.ImageOptions ?? new ImageOptions();
        var maxWidth = PrintLimit(options.MaxWidth, HeadWidth, HeadWidth, "maxWidth");
        var maxHeight = PrintLimit(options.MaxHeight, DefaultMaxHeight, MaxPrintHeight, "maxHeight");
        var source = DecodeBase64(item.Content);
        var (format, size) = CheckHeader(source);
        // Before the gate: a job over the paper limit is not decoded.
        ctx.AddPaper(PaperLength.ImageDots(size.Width, size.Height, maxWidth, maxHeight, options.PreserveAspectRatio));

        await _queue.RunAsync(async () =>
        {
            var png = await ResizeToPngAsync(source, format, new Size(maxWidth, maxHeight), options.PreserveAspectRatio);
            // ESCPOS_NET decodes the PNG again, so this stays inside the queue.
            ctx.Add(ctx.Emitter.PrintImage(png, options.HighDensity, isLegacy: options.UseLegacyMode));
        });
    }

    private static Configuration CreateConfiguration()
    {
        var configuration = new Configuration(new PngConfigurationModule(), new JpegConfigurationModule())
        {
            MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = AllocationLimitMegabytes })
        };

        // Metadata is not printed. A compressed PNG text chunk (zTXt, iTXt) inflates to gigabytes
        // on the managed heap, where the allocator limit does not apply.
        configuration.ImageFormatsManager.SetDecoder(PngFormat.Instance, new PngDecoder { IgnoreMetadata = true });
        configuration.ImageFormatsManager.SetDecoder(JpegFormat.Instance, new JpegDecoder { IgnoreMetadata = true });
        return configuration;
    }

    // The caller can ask for less than the printer limit, not for more. ImageSharp reads 0 as
    // "any size", so a value below 1 would switch the limit off.
    private static int PrintLimit(int? requested, int defaultValue, int limit, string name)
    {
        var value = requested ?? defaultValue;
        if (value < 1)
            throw new PrintContentException($"imageOptions.{name} {value} must be at least 1");

        return Math.Min(value, limit);
    }

    private static async Task<byte[]> ResizeToPngAsync(byte[] source, IImageFormat format, Size max, bool preserveAspectRatio)
    {
        using var image = Decode(format, source, static bytes => Image.Load(PngAndJpegOnly, bytes));

        if (image.Width > max.Width || image.Height > max.Height)
        {
            if (preserveAspectRatio)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = max,
                    Mode = ResizeMode.Max
                }));
            }
            else
            {
                image.Mutate(x => x.Resize(max.Width, max.Height));
            }
        }

        using var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    // Format and dimensions from the header: no pixel buffer yet.
    private static (IImageFormat Format, Size Size) CheckHeader(byte[] imageBytes)
    {
        // The format comes from the bytes. A declared MIME type in a data URI is ignored.
        var format = Image.DetectFormat(imageBytes);
        if (format is not (PngFormat or JpegFormat))
            throw new PrintContentException($"Image rejected: format not supported ({Facts(format, imageBytes.Length)}). Send a PNG or JPEG.");

        var info = Decode(format, imageBytes, static bytes => Image.Identify(PngAndJpegOnly, bytes, out _))
            ?? throw Damaged(format, imageBytes.Length);
        if (PixelsOverLimit(info.Width, info.Height))
        {
            throw new PrintContentException(
                $"Image rejected: {info.Width}x{info.Height} pixels is over the limit of {MaxSidePixels} per side " +
                $"and {MaxPixels} in total ({Facts(format, imageBytes.Length)}).");
        }

        return (format, new Size(info.Width, info.Height));
    }

    private static T Decode<T>(IImageFormat format, byte[] imageBytes, Func<byte[], T> read)
    {
        try
        {
            return read(imageBytes);
        }
        // The decoders wrap the allocator's refusal in InvalidImageContentException.
        catch (Exception ex) when (ex is InvalidMemoryOperationException || ex.InnerException is InvalidMemoryOperationException)
        {
            throw new PrintContentException(
                $"Image rejected: decoding needs more than {AllocationLimitMegabytes} MB ({Facts(format, imageBytes.Length)}).", ex);
        }
        // Damaged files also surface as NullReference, IndexOutOfRange and NotSupported from the decoders.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Damaged(format, imageBytes.Length, ex);
        }
    }

    internal static bool PixelsOverLimit(int width, int height)
        => width > MaxSidePixels || height > MaxSidePixels || (long)width * height > MaxPixels;

    private static byte[] DecodeBase64(string content)
    {
        var start = content.StartsWith("data:", StringComparison.Ordinal) || content.StartsWith("base64,", StringComparison.Ordinal)
            ? content.IndexOf(',') + 1
            : 0;

        // Checked on the text, before any allocation: n base64 characters hold at most 3n/4 bytes.
        var length = content.Length - start;
        if (length > MaxBase64Length)
        {
            throw new PrintContentException(
                $"Image rejected: {length} base64 characters is over the limit of {MaxBase64Length} ({MaxImageBytes} bytes).");
        }

        // From the span: no second copy of a 22 MB string.
        var bytes = new byte[length / 4 * 3];
        if (!Convert.TryFromBase64Chars(content.AsSpan(start), bytes, out var written))
            throw new PrintContentException($"Image rejected: not valid base64 ({content.Length} characters).");

        return written == bytes.Length ? bytes : bytes[..written];
    }

    // No log here: PrinterService logs the failure once, with this message. Facts only, never the image content.
    private static PrintContentException Damaged(IImageFormat format, int byteCount, Exception? inner = null)
        => new($"Image rejected: file is damaged ({Facts(format, byteCount)}). Send a PNG or JPEG.", inner);

    private static string Facts(IImageFormat? format, int byteCount)
        => $"format {format?.Name ?? "unknown"}, {byteCount} bytes";
}
