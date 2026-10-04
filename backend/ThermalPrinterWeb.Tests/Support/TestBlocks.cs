using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Tests.Support;

// Blocks of a print job and the context a block handler runs in.
internal static class TestBlocks
{
    // Caller content that no error text and no log line may repeat.
    public const string Secret = "SECRET-CALLER-CONTENT";

    public const string Pc852 = "PC852";

    // No code page: the context keeps its default encoding.
    public static BlockContext NewContext(string? codePage = null)
    {
        var ctx = new BlockContext(new EPSON(), null);
        if (codePage is not null)
            ctx.Encoding = CodePages.GetEncoding(codePage);
        return ctx;
    }

    // Everything the handlers added, as the printer gets it.
    public static byte[] OutputBytes(BlockContext ctx) => ByteSplicer.Combine([.. ctx.Output]);

    public static PrintContent ImageBlock(string content, ImageOptions? options = null)
        => new() { Type = ContentType.Image, Content = content, ImageOptions = options };
}
