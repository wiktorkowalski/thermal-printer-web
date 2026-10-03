using System.Text;
using ESCPOS_NET.Emitters;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// Per-document state threaded through every handler. Encoding is settable
// because a CodePage block can switch it mid-document; HasCut lets the builder
// skip the trailing auto-cut.
internal sealed class BlockContext(EPSON emitter, PrintOptions? options)
{
    public EPSON Emitter { get; } = emitter;
    public PrintOptions? Options { get; } = options;
    public Encoding Encoding { private get; set; } = Encoding.UTF8;
    public List<byte[]> Output { get; } = [];
    public bool HasCut { get; set; }

    // Characters turned into '?' across the whole document.
    public int ReplacedCharacters { get; private set; }

    // The only ways caller text becomes printer bytes: handlers must not encode on their own.
    public byte[] EncodeText(string text)
    {
        var bytes = PrinterSafeText.Encode(text, Encoding, out var replaced);
        ReplacedCharacters += replaced;
        return bytes;
    }

    public string CleanBarcode(string content)
    {
        var cleaned = PrinterSafeText.CleanBarcode(content, out var replaced);
        ReplacedCharacters += replaced;
        return cleaned;
    }

    public string CleanQRCode(string content)
    {
        var cleaned = PrinterSafeText.CleanQRCode(content, out var replaced);
        ReplacedCharacters += replaced;
        return cleaned;
    }

    public void Add(byte[] bytes) => Output.Add(bytes);
    public void AddRange(IEnumerable<byte[]> bytes) => Output.AddRange(bytes);
}

// One handler per ContentType: adding a block type = a new handler class plus
// its DI registration, no central switch to edit.
// Every exception from HandleAsync becomes a 400 for the caller. Throw
// PrintContentException to name the cause; any other type gets a generic reason.
internal interface IBlockHandler
{
    ContentType Type { get; }
    Task HandleAsync(PrintContent item, BlockContext ctx);
}
