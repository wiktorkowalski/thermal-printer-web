using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;
using PrintStyle = ThermalPrinterWeb.Models.PrintStyle;

namespace ThermalPrinterWeb.Tests.Support;

// Blocks of a print job and the context a block handler runs in.
internal static class TestBlocks
{
    // Caller content that no error text and no log line may repeat.
    public const string Secret = "SECRET-CALLER-CONTENT";

    public const string Pc852 = "PC852";

    // The start of every job with no options: ESC @, then ESC t 18 (PC852).
    public static readonly byte[] Reset = [0x1B, 0x40];
    public static readonly byte[] SelectPc852 = [0x1B, 0x74, 18];
    public static readonly byte[] Prelude = [.. Reset, .. SelectPc852];

    public static PrintContent Text(string content = "ok", params PrintStyle[] style)
        => new() { Type = ContentType.Text, Content = content, Style = style.Length == 0 ? null : [.. style] };

    // Text as the printer gets it in the default code page.
    public static byte[] Pc852Bytes(string text) => CodePages.GetEncoding(Pc852).GetBytes(text);

    // No code page: the context keeps its default encoding.
    public static BlockContext NewContext(string? codePage = null)
    {
        var ctx = new BlockContext(new EPSON(), null);
        if (codePage is not null)
            ctx.Encoding = CodePages.GetEncoding(codePage);
        return ctx;
    }

    // The real service with the production handler registration and no printer,
    // so a new block type is covered too.
    public static PrinterService NewService(ILogger<PrinterService>? logger = null) => new(
        logger ?? NullLogger<PrinterService>.Instance,
        new ServiceCollection().AddLogging().AddPrinterBlockHandlers().BuildServiceProvider().GetServices<IBlockHandler>(),
        NoPrinter.Options);

    // One job as the printer gets it.
    public static async Task<byte[]> JobBytesAsync(List<PrintContent> content, PrintOptions? options = null)
        => ByteSplicer.Combine([.. await NewService().BuildDocumentAsync(content, options)]);

    // Everything the handlers added, as the printer gets it.
    public static byte[] OutputBytes(BlockContext ctx) => ByteSplicer.Combine([.. ctx.Output]);

    public static PrintContent ImageBlock(string content, ImageOptions? options = null)
        => new() { Type = ContentType.Image, Content = content, ImageOptions = options };
}
