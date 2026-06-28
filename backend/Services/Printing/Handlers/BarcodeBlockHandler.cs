using ThermalPrinterWeb.Models;
using EscBarcodeType = ESCPOS_NET.Emitters.BarcodeType;
using EscBarWidth = ESCPOS_NET.Emitters.BarWidth;
using EscBarLabelPosition = ESCPOS_NET.Emitters.BarLabelPrintPosition;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class BarcodeBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.Barcode;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        if (string.IsNullOrEmpty(item.Content))
            return Task.CompletedTask;

        var e = ctx.Emitter;
        var opts = item.BarcodeOptions ?? new BarcodeOptions();

        if (opts.HeightInDots.HasValue)
            ctx.Add(e.SetBarcodeHeightInDots(opts.HeightInDots.Value));
        if (opts.Width.HasValue)
            ctx.Add(e.SetBarWidth(MapBarWidth(opts.Width.Value)));
        if (opts.LabelPosition.HasValue)
            ctx.Add(e.SetBarLabelPosition(MapBarLabelPosition(opts.LabelPosition.Value)));
        if (opts.UseFontB.HasValue)
            ctx.Add(e.SetBarLabelFontB(opts.UseFontB.Value));

        ctx.Add(e.PrintBarcode(MapBarcodeType(opts.Type), item.Content!));
        return Task.CompletedTask;
    }

    private static EscBarcodeType MapBarcodeType(Models.BarcodeType type) => type switch
    {
        Models.BarcodeType.UPC_A => EscBarcodeType.UPC_A,
        Models.BarcodeType.UPC_E => EscBarcodeType.UPC_E,
        Models.BarcodeType.EAN13 => EscBarcodeType.JAN13_EAN13,
        Models.BarcodeType.EAN8 => EscBarcodeType.JAN8_EAN8,
        Models.BarcodeType.CODE39 => EscBarcodeType.CODE39,
        Models.BarcodeType.ITF => EscBarcodeType.ITF,
        Models.BarcodeType.CODABAR => EscBarcodeType.CODABAR_NW_7,
        Models.BarcodeType.CODE128 => EscBarcodeType.CODE128,
        Models.BarcodeType.GS1_128 => EscBarcodeType.GS1_128,
        Models.BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL => EscBarcodeType.GS1_DATABAR_OMNIDIRECTIONAL,
        _ => EscBarcodeType.CODE128
    };

    private static EscBarWidth MapBarWidth(Models.BarWidth width) => width switch
    {
        Models.BarWidth.Thin => EscBarWidth.Thin,
        Models.BarWidth.Thick => EscBarWidth.Thick,
        _ => EscBarWidth.Default
    };

    private static EscBarLabelPosition MapBarLabelPosition(Models.BarLabelPosition pos) => pos switch
    {
        Models.BarLabelPosition.None => EscBarLabelPosition.None,
        Models.BarLabelPosition.Above => EscBarLabelPosition.Above,
        Models.BarLabelPosition.Below => EscBarLabelPosition.Below,
        Models.BarLabelPosition.Both => EscBarLabelPosition.Both,
        _ => EscBarLabelPosition.Below
    };
}
