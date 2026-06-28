using ESCPOS_NET.Emitters;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class QRCodeBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.QRCode;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        if (string.IsNullOrEmpty(item.Content))
            return Task.CompletedTask;

        var opts = item.QRCodeOptions ?? new QRCodeOptions();
        ctx.Add(ctx.Emitter.PrintQRCode(
            item.Content!,
            type: MapQRCodeModel(opts.Model),
            size: MapQRCodeSize(opts.Size),
            correction: MapQRCodeCorrectionLevel(opts.CorrectionLevel)
        ));
        return Task.CompletedTask;
    }

    private static TwoDimensionCodeType MapQRCodeModel(Models.QRCodeModel model) => model switch
    {
        Models.QRCodeModel.Model1 => TwoDimensionCodeType.QRCODE_MODEL1,
        Models.QRCodeModel.Micro => TwoDimensionCodeType.QRCODE_MICRO,
        _ => TwoDimensionCodeType.QRCODE_MODEL2
    };

    private static Size2DCode MapQRCodeSize(Models.QRCodeSize size) => size switch
    {
        Models.QRCodeSize.Large => Size2DCode.LARGE,
        Models.QRCodeSize.ExtraLarge => Size2DCode.EXTRA,
        _ => Size2DCode.NORMAL
    };

    private static CorrectionLevel2DCode MapQRCodeCorrectionLevel(Models.QRCodeCorrectionLevel level) => level switch
    {
        Models.QRCodeCorrectionLevel.Percent15 => CorrectionLevel2DCode.PERCENT_15,
        Models.QRCodeCorrectionLevel.Percent25 => CorrectionLevel2DCode.PERCENT_25,
        Models.QRCodeCorrectionLevel.Percent30 => CorrectionLevel2DCode.PERCENT_30,
        _ => CorrectionLevel2DCode.PERCENT_7
    };
}
