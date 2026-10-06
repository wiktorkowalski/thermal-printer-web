using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// JsonStringEnumConverter binds every integer, also one with no name ("alignment": 99 or "99").
// The mapping switches then pick a default, so the strip is not what the caller asked for.
// The block type has its own check: a type with no handler.
internal static class BlockEnums
{
    // Every options object is checked, also one the block type does not read.
    public static void Check(PrintContent item)
    {
        Check<Alignment>("alignment", item.Alignment);

        if (item.Style is { } styles)
        {
            for (var i = 0; i < styles.Count; i++)
                Check<PrintStyle>($"style[{i}]", styles[i]);
        }

        if (item.BarcodeOptions is { } barcode)
        {
            Check<BarcodeType>("barcodeOptions.type", barcode.Type);
            Check("barcodeOptions.width", barcode.Width);
            Check("barcodeOptions.labelPosition", barcode.LabelPosition);
        }

        if (item.QRCodeOptions is { } qrCode)
        {
            Check<QRCodeModel>("qrCodeOptions.model", qrCode.Model);
            Check<QRCodeSize>("qrCodeOptions.size", qrCode.Size);
            Check<QRCodeCorrectionLevel>("qrCodeOptions.correctionLevel", qrCode.CorrectionLevel);
        }

        if (item.SignalOptions is { } signal)
            Check("signalOptions.mode", signal.Mode);
    }

    private static void Check<T>(string field, T? value) where T : struct, Enum
    {
        if (value is not { } number || Enum.IsDefined(number))
            return;

        // The number is the only caller content in the text.
        throw new PrintContentException($"{field} {number:D} is not a valid value; use {Names<T>()}");
    }

    // "A, B or C": the names of an enum for an error text.
    internal static string Names<T>() where T : struct, Enum
    {
        var names = Enum.GetNames<T>();
        return $"{string.Join(", ", names[..^1])} or {names[^1]}";
    }
}
