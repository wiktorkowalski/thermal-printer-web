using System.Text;
using ESCPOS_NET.Emitters;

namespace ThermalPrinterWeb.Services.Printing;

// The ESC/POS code page command and the .NET text encoding must stay in sync or
// non-ASCII degrades to mojibake. DotNetCodePage null => no matching .NET
// encoding (text bytes fall back to UTF-8).
internal static class CodePages
{
    static CodePages()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly Dictionary<string, (CodePage PrinterCodePage, int? DotNetCodePage)> Map =
        new Dictionary<string, (CodePage, int?)>(StringComparer.OrdinalIgnoreCase)
        {
            ["PC437"] = (CodePage.PC437_USA_STANDARD_EUROPE_DEFAULT, 437),
            ["PC437_USA"] = (CodePage.PC437_USA_STANDARD_EUROPE_DEFAULT, 437),
            ["CP437"] = (CodePage.PC437_USA_STANDARD_EUROPE_DEFAULT, 437),
            ["KATAKANA"] = (CodePage.KATAKANA, null),
            ["PC850"] = (CodePage.PC850_MULTILINGUAL, 850),
            ["CP850"] = (CodePage.PC850_MULTILINGUAL, 850),
            ["PC858"] = (CodePage.PC858_EURO, 858),
            ["PC858_EURO"] = (CodePage.PC858_EURO, 858),
            ["CP858"] = (CodePage.PC858_EURO, 858),
            ["WPC1252"] = (CodePage.WPC1252, 1252),
            ["CP1252"] = (CodePage.WPC1252, 1252),
            ["WINDOWS-1252"] = (CodePage.WPC1252, 1252),
            // Polish / Central European
            ["PC852"] = (CodePage.PC852_LATIN2, 852),
            ["LATIN2"] = (CodePage.PC852_LATIN2, 852),
            ["CP852"] = (CodePage.PC852_LATIN2, 852),
            ["WPC1250"] = (CodePage.WPC1250_LATIN2, 1250),
            ["CP1250"] = (CodePage.WPC1250_LATIN2, 1250),
            ["WINDOWS-1250"] = (CodePage.WPC1250_LATIN2, 1250),
            ["ISO8859_2"] = (CodePage.ISO8859_2_LATIN2, 28592),
            ["ISO88592"] = (CodePage.ISO8859_2_LATIN2, 28592),
            ["ISO-8859-2"] = (CodePage.ISO8859_2_LATIN2, 28592),
        };

    public static CodePage? Resolve(string name)
        => Map.TryGetValue(name, out var entry) ? entry.PrinterCodePage : null;

    public static Encoding GetEncoding(string name)
        => Map.TryGetValue(name, out var entry) && entry.DotNetCodePage.HasValue
            ? Encoding.GetEncoding(entry.DotNetCodePage.Value)
            : Encoding.UTF8;
}
