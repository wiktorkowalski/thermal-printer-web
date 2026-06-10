using System.Text;
using ESCPOS_NET;
using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ThermalPrinterWeb.Models;
using EscPrintStyle = ESCPOS_NET.Emitters.PrintStyle;
using EscBarcodeType = ESCPOS_NET.Emitters.BarcodeType;
using EscBarWidth = ESCPOS_NET.Emitters.BarWidth;
using EscBarLabelPosition = ESCPOS_NET.Emitters.BarLabelPrintPosition;

namespace ThermalPrinterWeb.Services;

public class PrinterService : IPrinterService
{
    private readonly ILogger<PrinterService> _logger;
    private const string PrinterAddress = "192.168.123.100:9100";

    // The printer renders single-byte code pages only; raw UTF-8 prints as garbage
    // for anything outside ASCII, so default to Latin-2 (covers Polish) instead.
    private const string DefaultCodePage = "PC852";

    static PrinterService()
    {
        // Register code page encoding provider for Windows-1250, CP852, etc.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public PrinterService(ILogger<PrinterService> logger)
    {
        _logger = logger;
    }

    public async Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null)
    {
        try
        {
            _logger.LogInformation("Connecting to printer at {Address}", PrinterAddress);
            var printer = new ImmediateNetworkPrinter(new ImmediateNetworkPrinterSettings
            {
                ConnectionString = PrinterAddress,
                PrinterName = "ThermalPrinter"
            });
            var e = new EPSON();
            var byteContent = new List<byte[]>();
            var hasCutContent = false;

            // Determine text encoding based on code page
            var codePageName = options?.CodePage ?? DefaultCodePage;
            var textEncoding = Encoding.UTF8;
            var codePage = MapCodePage(codePageName);
            if (codePage.HasValue)
            {
                byteContent.Add(e.CodePage(codePage.Value));
                textEncoding = GetEncodingForCodePage(codePageName);
                _logger.LogDebug("Using code page {CodePage}", codePageName);
            }
            else
            {
                _logger.LogWarning("Unknown code page {CodePage}, printing raw UTF-8 bytes", codePageName);
            }

            if (options?.DefaultLineSpacing != null)
                byteContent.Add(e.SetLineSpacingInDots(options.DefaultLineSpacing.Value));

            foreach (var item in content)
            {
                byteContent.Add(item.Alignment switch
                {
                    Alignment.Left => e.LeftAlign(),
                    Alignment.Right => e.RightAlign(),
                    _ => e.CenterAlign()
                });

                switch (item.Type)
                {
                    case ContentType.Text:
                        byteContent.AddRange(BuildTextBytes(e, item, textEncoding));
                        break;

                    case ContentType.Image:
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var imageBytes = await ProcessImageContent(item.Content, item.ImageOptions);
                            if (imageBytes != null)
                            {
                                var legacy = item.ImageOptions?.UseLegacyMode ?? true;
                                byteContent.Add(e.PrintImage(imageBytes, true, isLegacy: legacy));
                            }
                        }
                        break;

                    case ContentType.Barcode:
                        if (!string.IsNullOrEmpty(item.Content))
                            byteContent.AddRange(BuildBarcodeBytes(e, item));
                        break;

                    case ContentType.QRCode:
                        if (!string.IsNullOrEmpty(item.Content))
                            byteContent.Add(BuildQRCodeBytes(e, item));
                        break;

                    case ContentType.LineFeed:
                        for (int i = 0; i < (item.Lines ?? 1); i++)
                            byteContent.Add(e.PrintLine(string.Empty));
                        break;

                    case ContentType.Cut:
                        hasCutContent = true;
                        var feedLines = options?.FeedLinesAfterPrint ?? 3;
                        byteContent.Add(item.PartialCut == true
                            ? e.PartialCutAfterFeed(feedLines)
                            : e.FullCutAfterFeed(feedLines));
                        break;

                    case ContentType.Separator:
                        var sep = new string((item.SeparatorChar ?? "=")[0], item.SeparatorLength ?? 32);
                        byteContent.AddRange(BuildStyledTextBytes(e, sep, item.Style, textEncoding));
                        break;

                    case ContentType.CodePage:
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var cp = MapCodePage(item.Content);
                            if (cp.HasValue)
                            {
                                byteContent.Add(e.CodePage(cp.Value));
                                textEncoding = GetEncodingForCodePage(item.Content);
                            }
                            else
                            {
                                _logger.LogWarning("Unknown code page {CodePage} in content, keeping current encoding", item.Content);
                            }
                        }
                        break;

                    default:
                        _logger.LogWarning("Unsupported content type: {Type}", item.Type);
                        break;
                }
            }

            if (options?.AutoCut != false && !hasCutContent)
            {
                var feedLines = options?.FeedLinesAfterPrint ?? 3;
                byteContent.Add(e.FullCutAfterFeed(feedLines));
            }

            await printer.WriteAsync(ByteSplicer.Combine(byteContent.ToArray()));
            _logger.LogInformation("Printing complete");
            return new PrintResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Print failed");
            return new PrintResult(false, ex.Message);
        }
    }

    private List<byte[]> BuildTextBytes(EPSON e, PrintContent item, Encoding encoding)
        => BuildStyledTextBytes(e, item.Content ?? string.Empty, item.Style, encoding);

    private List<byte[]> BuildStyledTextBytes(EPSON e, string text, List<Models.PrintStyle>? styles, Encoding encoding)
    {
        var bytes = new List<byte[]>();
        var hasReverse = styles?.Contains(Models.PrintStyle.ReverseMode) == true;
        var hasUpsideDown = styles?.Contains(Models.PrintStyle.UpsideDownMode) == true;

        if (hasReverse)
            bytes.Add(e.ReverseMode(true));
        if (hasUpsideDown)
            bytes.Add(e.UpsideDownMode(true));

        bytes.Add(e.SetStyles(MapPrintStyles(styles)));

        // Encode text using the specified code page encoding
        var textBytes = encoding.GetBytes(text);
        var newLine = new byte[] { 0x0A }; // LF
        bytes.Add([.. textBytes, .. newLine]);

        bytes.Add(e.SetStyles(EscPrintStyle.None));

        if (hasUpsideDown)
            bytes.Add(e.UpsideDownMode(false));
        if (hasReverse)
            bytes.Add(e.ReverseMode(false));

        return bytes;
    }

    private List<byte[]> BuildBarcodeBytes(EPSON e, PrintContent item)
    {
        var bytes = new List<byte[]>();
        var opts = item.BarcodeOptions ?? new BarcodeOptions();

        if (opts.HeightInDots.HasValue)
            bytes.Add(e.SetBarcodeHeightInDots(opts.HeightInDots.Value));
        if (opts.Width.HasValue)
            bytes.Add(e.SetBarWidth(MapBarWidth(opts.Width.Value)));
        if (opts.LabelPosition.HasValue)
            bytes.Add(e.SetBarLabelPosition(MapBarLabelPosition(opts.LabelPosition.Value)));
        if (opts.UseFontB.HasValue)
            bytes.Add(e.SetBarLabelFontB(opts.UseFontB.Value));

        bytes.Add(e.PrintBarcode(MapBarcodeType(opts.Type), item.Content!));
        return bytes;
    }

    private byte[] BuildQRCodeBytes(EPSON e, PrintContent item)
    {
        var opts = item.QRCodeOptions ?? new QRCodeOptions();
        return e.PrintQRCode(
            item.Content!,
            type: MapQRCodeModel(opts.Model),
            size: MapQRCodeSize(opts.Size),
            correction: MapQRCodeCorrectionLevel(opts.CorrectionLevel)
        );
    }

    private async Task<byte[]?> ProcessImageContent(string content, ImageOptions? options)
    {
        try
        {
            byte[] imageBytes;
            if (content.StartsWith("data:image"))
                imageBytes = Convert.FromBase64String(content.Split(',')[1]);
            else if (content.StartsWith("base64,"))
                imageBytes = Convert.FromBase64String(content[7..]);
            else
                imageBytes = Convert.FromBase64String(content);

            var opts = options ?? new ImageOptions();
            using var image = Image.Load(imageBytes);
            var maxWidth = opts.MaxWidth ?? 500;
            var maxHeight = opts.MaxHeight ?? 500;

            if (image.Width > maxWidth || image.Height > maxHeight)
            {
                if (opts.PreserveAspectRatio)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(maxWidth, maxHeight),
                        Mode = ResizeMode.Max
                    }));
                }
                else
                {
                    image.Mutate(x => x.Resize(maxWidth, maxHeight));
                }
            }

            using var ms = new MemoryStream();
            await image.SaveAsPngAsync(ms);
            return ms.ToArray();
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Invalid base64 image format. Content length: {Length}", content.Length);
            throw;
        }
        catch (UnknownImageFormatException ex)
        {
            _logger.LogError(ex, "Unsupported image format. Content prefix: {Prefix}", content[..Math.Min(50, content.Length)]);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process image. Content length: {Length}", content.Length);
            throw;
        }
    }

    private EscPrintStyle MapPrintStyles(List<Models.PrintStyle>? styles)
    {
        if (styles == null || styles.Count == 0)
            return EscPrintStyle.None;

        EscPrintStyle result = EscPrintStyle.None;
        foreach (var style in styles)
        {
            result |= style switch
            {
                Models.PrintStyle.Bold => EscPrintStyle.Bold,
                Models.PrintStyle.Italic => EscPrintStyle.Italic,
                Models.PrintStyle.Underline => EscPrintStyle.Underline,
                Models.PrintStyle.DoubleHeight => EscPrintStyle.DoubleHeight,
                Models.PrintStyle.DoubleWidth => EscPrintStyle.DoubleWidth,
                Models.PrintStyle.FontB => EscPrintStyle.FontB,
                _ => EscPrintStyle.None
            };
        }
        return result;
    }

    private EscBarcodeType MapBarcodeType(Models.BarcodeType type) => type switch
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

    private EscBarWidth MapBarWidth(Models.BarWidth width) => width switch
    {
        Models.BarWidth.Thin => EscBarWidth.Thin,
        Models.BarWidth.Thick => EscBarWidth.Thick,
        _ => EscBarWidth.Default
    };

    private EscBarLabelPosition MapBarLabelPosition(Models.BarLabelPosition pos) => pos switch
    {
        Models.BarLabelPosition.None => EscBarLabelPosition.None,
        Models.BarLabelPosition.Above => EscBarLabelPosition.Above,
        Models.BarLabelPosition.Below => EscBarLabelPosition.Below,
        Models.BarLabelPosition.Both => EscBarLabelPosition.Both,
        _ => EscBarLabelPosition.Below
    };

    private TwoDimensionCodeType MapQRCodeModel(Models.QRCodeModel model) => model switch
    {
        Models.QRCodeModel.Model1 => TwoDimensionCodeType.QRCODE_MODEL1,
        Models.QRCodeModel.Micro => TwoDimensionCodeType.QRCODE_MICRO,
        _ => TwoDimensionCodeType.QRCODE_MODEL2
    };

    private Size2DCode MapQRCodeSize(Models.QRCodeSize size) => size switch
    {
        Models.QRCodeSize.Large => Size2DCode.LARGE,
        Models.QRCodeSize.ExtraLarge => Size2DCode.EXTRA,
        _ => Size2DCode.NORMAL
    };

    private CorrectionLevel2DCode MapQRCodeCorrectionLevel(Models.QRCodeCorrectionLevel level) => level switch
    {
        Models.QRCodeCorrectionLevel.Percent15 => CorrectionLevel2DCode.PERCENT_15,
        Models.QRCodeCorrectionLevel.Percent25 => CorrectionLevel2DCode.PERCENT_25,
        Models.QRCodeCorrectionLevel.Percent30 => CorrectionLevel2DCode.PERCENT_30,
        _ => CorrectionLevel2DCode.PERCENT_7
    };

    // Single source for both the ESC/POS code page command and the .NET encoding
    // used to produce text bytes — the two must always stay in sync or output
    // degrades to mojibake. DotNetCodePage null means no matching .NET encoding
    // exists (text bytes fall back to UTF-8).
    private static readonly Dictionary<string, (CodePage PrinterCodePage, int? DotNetCodePage)> CodePageMap =
        new Dictionary<string, (CodePage PrinterCodePage, int? DotNetCodePage)>(StringComparer.OrdinalIgnoreCase)
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

    private static CodePage? MapCodePage(string name)
        => CodePageMap.TryGetValue(name, out var entry) ? entry.PrinterCodePage : null;

    private static Encoding GetEncodingForCodePage(string name)
        => CodePageMap.TryGetValue(name, out var entry) && entry.DotNetCodePage.HasValue
            ? Encoding.GetEncoding(entry.DotNetCodePage.Value)
            : Encoding.UTF8;
}
