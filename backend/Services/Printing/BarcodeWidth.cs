using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Services.Printing;

// The printed width of a barcode, in dots. The printer drops a barcode that is wider than the paper:
// no bars, no caption, no error (read from paper, issue #44). So a wider one is rejected before the print.
// The width never counts more than the printer prints: no quiet zone, and the narrowest bars where
// the printer decides. So only a barcode that is wider for certain is rejected.
// Same numbers as BARCODE_MODULES in frontend/src/lib/printer-limits.ts and barcodeWidthDots in frontend/src/lib/paper.ts.
// Not known: whether the printer also needs room for a quiet zone. Every barcode that printed was 492 dots or less,
// every one that was dropped 580 or more. So a barcode of 493 to 576 dots passes here and can still be dropped.
internal static class BarcodeWidth
{
    internal const int MaxDots = ImageBlockHandler.HeadWidth;

    // GS w n: n is the width of one module in dots. ESCPOS_NET sends 3, 4 and 5 (measured on paper).
    internal const int ThinDots = 3;
    internal const int DefaultDots = 4;
    internal const int ThickDots = 5;

    // A block with "width": null sends no GS w. The printer keeps the module of the last GS w of the job (BlockContext.BarModuleDots).
    // With none before it the printer uses its own value, which is not measured: 2 is the smallest value of the command.
    internal const int UnsetDots = 2;

    // CODE128 in code set B, as ESCPOS_NET sends it: start, one symbol per character, check symbol (11 modules each), stop (13).
    // Exact: 61.5 mm measured for 8 characters at 4 dots (123 modules).
    internal const int Code128ModulesPerCharacter = 11;
    internal const int Code128FixedModules = 35;

    // Fixed by the symbology: guard bars and digits.
    private const int Ean13Modules = 95;
    private const int Ean8Modules = 67;
    private const int UpcEModules = 51;

    // The symbologies with narrow and wide bars. The printer picks the ratio (2 to 3); these count 2.
    // CODE39: 9 bars and spaces, 3 of them wide, and one gap. The printer adds the start and the stop character.
    private const int Code39ModulesPerCharacter = 13;
    // ITF: 5 bars or spaces per digit, 2 of them wide; start and stop are 8 modules.
    private const int ItfModulesPerDigit = 7;
    private const int ItfFixedModules = 8;
    // CODABAR: 7 bars and spaces, at least 2 of them wide, and one gap.
    private const int CodabarModulesPerCharacter = 10;

    // Null: the block sends no GS w.
    internal static int? ModuleDots(BarWidth? width) => width switch
    {
        null => null,
        BarWidth.Thin => ThinDots,
        BarWidth.Thick => ThickDots,
        _ => DefaultDots
    };

    // Null: no width for this type. GS1-128 and GS1 DataBar print no bars on this printer.
    internal static int? Modules(BarcodeType type, ReadOnlySpan<char> content) => type switch
    {
        BarcodeType.CODE128 => Code128ModulesPerCharacter * content.Length + Code128FixedModules,
        BarcodeType.UPC_A or BarcodeType.EAN13 => Ean13Modules,
        BarcodeType.EAN8 => Ean8Modules,
        BarcodeType.UPC_E => UpcEModules,
        BarcodeType.CODE39 => Code39ModulesPerCharacter * (content.Length + (HasCode39Guard(content) ? 0 : 2)) - 1,
        BarcodeType.ITF => ItfModulesPerDigit * content.Length + ItfFixedModules,
        BarcodeType.CODABAR => CodabarModulesPerCharacter * content.Length - 1,
        _ => null
    };

    internal static int? Dots(BarcodeType type, ReadOnlySpan<char> content, int moduleDots)
        => Modules(type, content) * moduleDots;

    // The caller sent a start or stop character: count no more of them.
    private static bool HasCode39Guard(ReadOnlySpan<char> content)
        => content.Length > 0 && (content[0] == '*' || content[^1] == '*');
}
