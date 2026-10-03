using ESCPOS_NET.Emitters;
using ESCPOS_NET.Emitters.BaseCommandValues;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

// Builds the GS ( k commands itself. ESCPOS_NET keeps only the low byte of each
// char and puts the char count, not the byte count, in the length prefix.
internal sealed class QRCodeBlockHandler : IBlockHandler
{
    private static readonly byte[] Function = [Cmd.GS, Barcodes.Set2DCode, Barcodes.PrintBarcode];

    public ContentType Type => ContentType.QRCode;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        if (string.IsNullOrEmpty(item.Content))
            return Task.CompletedTask;

        var opts = item.QRCodeOptions ?? new QRCodeOptions();
        var model = MapQRCodeModel(opts.Model);
        var data = ctx.EncodeQRCode(item.Content);

        var maxBytes = MaxDataBytes(model);
        if (data.Length > maxBytes)
            throw new PrintContentException(
                $"content is {data.Length} bytes as UTF-8; a {opts.Model} QR code holds at most {maxBytes}");

        // pL pH count the data plus the bytes of StoreQRCodeData.
        var storeLength = data.Length + Barcodes.StoreQRCodeData.Length;
        ctx.Add([
            .. Function, .. Barcodes.SelectQRCodeModel, (byte)model, Barcodes.AutoEnding,
            .. Function, .. Barcodes.SetQRCodeDotSize, (byte)MapQRCodeSize(opts.Size),
            .. Function, .. Barcodes.SetQRCodeCorrectionLevel, (byte)MapQRCodeCorrectionLevel(opts.CorrectionLevel),
            .. Function, (byte)(storeLength & 0xFF), (byte)(storeLength >> 8), .. Barcodes.StoreQRCodeData, .. data,
            .. Function, .. Barcodes.PrintQRCode
        ]);
        return Task.CompletedTask;
    }

    // Model 2: byte-mode capacity of version 40 at the lowest correction level.
    // Model 1 and Micro: the limits ESCPOS_NET applied, now counted in bytes.
    private static int MaxDataBytes(TwoDimensionCodeType model) => model switch
    {
        TwoDimensionCodeType.QRCODE_MODEL1 => 707,
        TwoDimensionCodeType.QRCODE_MICRO => 21,
        _ => 2953
    };

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
