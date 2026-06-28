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
    public Encoding Encoding { get; set; } = Encoding.UTF8;
    public List<byte[]> Output { get; } = [];
    public bool HasCut { get; set; }

    public void Add(byte[] bytes) => Output.Add(bytes);
    public void AddRange(IEnumerable<byte[]> bytes) => Output.AddRange(bytes);
}

// One handler per ContentType: adding a block type = a new handler class plus
// its DI registration, no central switch to edit.
internal interface IBlockHandler
{
    ContentType Type { get; }
    Task HandleAsync(PrintContent item, BlockContext ctx);
}
