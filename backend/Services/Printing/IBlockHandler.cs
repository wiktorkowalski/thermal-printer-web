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

    // The module of the last GS w of the job, in dots. The command holds until ESC @: a Barcode block with no width prints with it.
    public int? BarModuleDots { get; set; }

    // The job is a reprint of a stored job. PrinterService sets it from the journal trace of the request: no caller field sets it.
    // A reprint leaves out a barcode that is wider than the paper, as the printer did at the first print. A new print rejects it.
    public bool IsReprint { get; init; }

    // The Barcode blocks that a reprint left out.
    public int SkippedBarcodes { get; set; }

    // The empty lines at the end of the blocks so far (CutFeed.TrailingLinesAfter). PrinterService sets it after each block.
    public int TrailingFeedLines { get; set; }

    // Characters turned into '?' across the whole document.
    public int ReplacedCharacters { get; private set; }

    // Estimated paper for the document so far, in dots. See PaperLength.
    public int PaperDots { get; private set; }

    public int? LineSpacing => Options?.DefaultLineSpacing;

    // The only way printed text becomes printer bytes: handlers must not encode on
    // their own. Code content is the exception: its handler rejects what it cannot hold
    // (QR: a control character; barcode: all but printable ASCII) with no '?' in its place.
    public byte[] EncodeText(string text)
    {
        var bytes = PrinterSafeText.Encode(text, Encoding, out var replaced);
        ReplacedCharacters += replaced;
        return bytes;
    }

    // A handler calls this before it does the work for the block, so a job over the limit stops early.
    public void AddPaper(int dots)
    {
        PaperDots += dots;
        if (PaperDots > PaperLength.MaxDots)
        {
            throw new PrintContentException(
                $"the document is over the limit of {PaperLength.MaxDots} dots of paper ({PaperLength.MaxDots / PaperLength.DotsPerMetre} m)");
        }
    }

    public void Add(byte[] bytes) => Output.Add(bytes);
    public void AddRange(IEnumerable<byte[]> bytes) => Output.AddRange(bytes);
}

// One handler per ContentType: adding a block type = a new handler class plus
// its DI registration, no central switch to edit.
// An exception from HandleAsync becomes a 400 for the caller. Throw
// PrintContentException to name the cause; any other type gets a generic reason.
// PrintBusyException (server load) is the one case that becomes a 503.
internal interface IBlockHandler
{
    ContentType Type { get; }
    Task HandleAsync(PrintContent item, BlockContext ctx);
}
