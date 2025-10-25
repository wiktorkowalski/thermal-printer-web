using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using ESCPOS_NET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ThermalPrinterWeb.Controllers;

namespace ThermalPrinterWeb;

public interface IPrinterService
{
    Task Print(string name, string message);
    Task Print(string name, string message, byte[] imageBytes);
    Task PrintCustomContent(List<CustomPrintContent> content, PrintOptions? options = null);
}

public class PrinterService : IPrinterService
{
    private readonly ILogger<PrinterService> _logger;
    private readonly string _printerAddress = "192.168.123.100:9100";

    public PrinterService(ILogger<PrinterService> logger)
    {
        _logger = logger;
    }

    public async Task Print(string name, string message)
    {
        try
        {
            _logger.LogInformation("Connecting to printer at {Address}", _printerAddress);
            // Initialize printer
            var printer = new ImmediateNetworkPrinter(new ImmediateNetworkPrinterSettings() { ConnectionString = _printerAddress, PrinterName = "ThermalPrinter" });
            var e = new EPSON();
            // Print data
            await printer.WriteAsync(ByteSplicer.Combine(
                e.SetStyles(PrintStyle.DoubleWidth | PrintStyle.DoubleHeight),
                e.CenterAlign(),
                e.PrintLine("========================"),
                e.PrintLine(name),
                e.PrintLine("========================"),
                e.PrintLine(message),
                e.PrintLine("========================"),
                e.PrintLine(""),
                e.PrintLine(""),
                e.PrintLine(""),
                e.FullCutAfterFeed(10)
            ).ToArray());

            // Close printer connection
            _logger.LogInformation("Printing complete");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error printing");
        }
    }

    public async Task Print(string name, string message, byte[] imageBytes)
    {
        try
        {
            _logger.LogInformation("Connecting to printer at {Address}", _printerAddress);
            var printer = new ImmediateNetworkPrinter(new ImmediateNetworkPrinterSettings() { ConnectionString = _printerAddress, PrinterName = "ThermalPrinter" });
            var e = new EPSON();

            // Convert image to PNG
            using var image = Image.Load(imageBytes);
            _logger.LogInformation("{Metadata}", image.Metadata.GetPngMetadata().TextData);
            using var ms = new MemoryStream();
            image.Mutate(c => c.Resize(500, 500));
            image.SaveAsPng(ms);
            var encodedImageBytes = ms.ToArray();

            // Print data
            await printer.WriteAsync(ByteSplicer.Combine(
                e.PrintLine("================================"),
                e.SetStyles(PrintStyle.DoubleWidth | PrintStyle.DoubleHeight),
                e.CenterAlign(),
                e.PrintLine(name),
                e.PrintLine("================================"),
                e.PrintLine(message),
                e.PrintLine("================================"),
                e.PrintImage(encodedImageBytes, true, isLegacy: true),
                e.PrintLine("================================"),
                e.PrintLine(""),
                e.PrintLine(""),
                e.PrintLine(""),
                e.FullCutAfterFeed(10)
            ).ToArray());

            // Close printer connection
            _logger.LogInformation("Printing complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing with image");
        }
    }

    public async Task PrintCustomContent(List<CustomPrintContent> content, PrintOptions? options = null)
    {
        try
        {
            _logger.LogInformation("Connecting to printer at {Address}", _printerAddress);
            var printer = new ImmediateNetworkPrinter(new ImmediateNetworkPrinterSettings() { ConnectionString = _printerAddress, PrinterName = "ThermalPrinter" });
            var e = new EPSON();
            var byteContent = new List<byte[]>();

            // Apply global options
            if (options?.CodePage != null)
            {
                var codePage = MapCodePage(options.CodePage);
                if (codePage.HasValue)
                {
                    byteContent.Add(e.CodePage(codePage.Value));
                }
            }

            if (options?.DefaultLineSpacing != null)
            {
                byteContent.Add(e.SetLineSpacingInDots(options.DefaultLineSpacing.Value));
            }

            foreach (var item in content)
            {
                // Apply alignment
                byteContent.Add(item.Alignment switch
                {
                    CustomPrintAlignment.Left => e.LeftAlign(),
                    CustomPrintAlignment.Right => e.RightAlign(),
                    _ => e.CenterAlign()
                });

                switch (item.Type)
                {
                    case CustomPrintContentType.Text:
                        var style = MapPrintStyles(item.Style);
                        byteContent.Add(e.SetStyles(style));
                        byteContent.Add(e.PrintLine(item.Content ?? string.Empty));
                        byteContent.Add(e.SetStyles(PrintStyle.None)); // Reset styles
                        break;

                    case CustomPrintContentType.Image:
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

                    case CustomPrintContentType.Barcode:
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var barcodeOpts = item.BarcodeOptions ?? new BarcodeOptions();

                            // Set barcode configuration
                            if (barcodeOpts.HeightInDots.HasValue)
                                byteContent.Add(e.SetBarcodeHeightInDots(barcodeOpts.HeightInDots.Value));

                            if (barcodeOpts.Width.HasValue)
                                byteContent.Add(e.SetBarWidth(MapBarWidth(barcodeOpts.Width.Value)));

                            if (barcodeOpts.LabelPosition.HasValue)
                                byteContent.Add(e.SetBarLabelPosition(MapBarLabelPosition(barcodeOpts.LabelPosition.Value)));

                            if (barcodeOpts.UseFontB.HasValue)
                                byteContent.Add(e.SetBarLabelFontB(barcodeOpts.UseFontB.Value));

                            var barcodeType = MapBarcodeType(barcodeOpts.Type);
                            byteContent.Add(e.PrintBarcode(barcodeType, item.Content));
                        }
                        break;

                    case CustomPrintContentType.QRCode:
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var qrOpts = item.QRCodeOptions ?? new QRCodeOptions();
                            var qrType = MapQRCodeModel(qrOpts.Model);
                            var qrSize = MapQRCodeSize(qrOpts.Size);
                            var qrCorrection = MapQRCodeCorrectionLevel(qrOpts.CorrectionLevel);

                            byteContent.Add(e.PrintQRCode(item.Content, type: qrType, size: qrSize, correction: qrCorrection));
                        }
                        break;

                    case CustomPrintContentType.LineFeed:
                        var lines = item.Lines ?? 1;
                        for (int i = 0; i < lines; i++)
                        {
                            byteContent.Add(e.PrintLine(string.Empty));
                        }
                        break;

                    case CustomPrintContentType.Cut:
                        var feedLines = options?.FeedLinesAfterPrint ?? 3;
                        if (item.PartialCut == true)
                        {
                            byteContent.Add(e.PartialCutAfterFeed(feedLines));
                        }
                        else
                        {
                            byteContent.Add(e.FullCutAfterFeed(feedLines));
                        }
                        break;

                    case CustomPrintContentType.Separator:
                        var separatorChar = item.SeparatorChar ?? "=";
                        var separatorLength = item.SeparatorLength ?? 32;
                        var separator = new string(separatorChar[0], separatorLength);
                        byteContent.Add(e.PrintLine(separator));
                        break;

                    case CustomPrintContentType.CodePage:
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var cp = MapCodePage(item.Content);
                            if (cp.HasValue)
                            {
                                byteContent.Add(e.CodePage(cp.Value));
                            }
                        }
                        break;

                    default:
                        _logger.LogWarning("Unsupported print content type: {Type}", item.Type);
                        break;
                }
            }

            // Auto-cut if enabled
            if (options?.AutoCut != false)
            {
                var feedLines = options?.FeedLinesAfterPrint ?? 3;
                byteContent.Add(e.FullCutAfterFeed(feedLines));
            }

            await printer.WriteAsync(ByteSplicer.Combine(byteContent.ToArray()));
            _logger.LogInformation("Printing complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing custom content");
            throw;
        }
    }

    private async Task<byte[]?> ProcessImageContent(string content, ImageOptions? options)
    {
        try
        {
            byte[] imageBytes;

            // Handle base64 encoded images
            if (content.StartsWith("data:image"))
            {
                var base64Data = content.Split(',')[1];
                imageBytes = Convert.FromBase64String(base64Data);
            }
            else if (content.StartsWith("base64,"))
            {
                imageBytes = Convert.FromBase64String(content.Substring(7));
            }
            else
            {
                // Assume it's already base64
                imageBytes = Convert.FromBase64String(content);
            }

            var opts = options ?? new ImageOptions();
            using var image = Image.Load(imageBytes);

            // Resize if needed
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing image content");
            return null;
        }
    }

    private PrintStyle MapPrintStyles(List<CustomPrintStyle>? styles)
    {
        if (styles == null || styles.Count == 0)
            return PrintStyle.None;

        PrintStyle result = PrintStyle.None;
        foreach (var style in styles)
        {
            result |= style switch
            {
                CustomPrintStyle.Bold => PrintStyle.Bold,
                CustomPrintStyle.Italic => PrintStyle.Italic,
                CustomPrintStyle.Underline => PrintStyle.Underline,
                CustomPrintStyle.DoubleHeight => PrintStyle.DoubleHeight,
                CustomPrintStyle.DoubleWidth => PrintStyle.DoubleWidth,
                CustomPrintStyle.FontB => PrintStyle.FontB,
                _ => PrintStyle.None
            };
        }
        return result;
    }

    private ESCPOS_NET.Emitters.BarcodeType MapBarcodeType(Controllers.BarcodeType type)
    {
        return type switch
        {
            Controllers.BarcodeType.UPC_A => ESCPOS_NET.Emitters.BarcodeType.UPC_A,
            Controllers.BarcodeType.UPC_E => ESCPOS_NET.Emitters.BarcodeType.UPC_E,
            Controllers.BarcodeType.EAN13 => ESCPOS_NET.Emitters.BarcodeType.JAN13_EAN13,
            Controllers.BarcodeType.EAN8 => ESCPOS_NET.Emitters.BarcodeType.JAN8_EAN8,
            Controllers.BarcodeType.CODE39 => ESCPOS_NET.Emitters.BarcodeType.CODE39,
            Controllers.BarcodeType.ITF => ESCPOS_NET.Emitters.BarcodeType.ITF,
            Controllers.BarcodeType.CODABAR => ESCPOS_NET.Emitters.BarcodeType.CODABAR_NW_7,
            Controllers.BarcodeType.CODE128 => ESCPOS_NET.Emitters.BarcodeType.CODE128,
            Controllers.BarcodeType.GS1_128 => ESCPOS_NET.Emitters.BarcodeType.GS1_128,
            Controllers.BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL => ESCPOS_NET.Emitters.BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL,
            _ => ESCPOS_NET.Emitters.BarcodeType.CODE128
        };
    }

    private ESCPOS_NET.Emitters.BarWidth MapBarWidth(Controllers.BarWidth width)
    {
        return width switch
        {
            Controllers.BarWidth.Thin => ESCPOS_NET.Emitters.BarWidth.Thin,
            Controllers.BarWidth.Thick => ESCPOS_NET.Emitters.BarWidth.Thick,
            _ => ESCPOS_NET.Emitters.BarWidth.Default
        };
    }

    private ESCPOS_NET.Emitters.BarLabelPrintPosition MapBarLabelPosition(Controllers.BarLabelPosition position)
    {
        return position switch
        {
            Controllers.BarLabelPosition.None => ESCPOS_NET.Emitters.BarLabelPrintPosition.None,
            Controllers.BarLabelPosition.Above => ESCPOS_NET.Emitters.BarLabelPrintPosition.Above,
            Controllers.BarLabelPosition.Below => ESCPOS_NET.Emitters.BarLabelPrintPosition.Below,
            Controllers.BarLabelPosition.Both => ESCPOS_NET.Emitters.BarLabelPrintPosition.Both,
            _ => ESCPOS_NET.Emitters.BarLabelPrintPosition.Below
        };
    }

    private TwoDimensionCodeType MapQRCodeModel(Controllers.QRCodeModel model)
    {
        return model switch
        {
            Controllers.QRCodeModel.Model1 => TwoDimensionCodeType.QRCODE_MODEL1,
            Controllers.QRCodeModel.Micro => TwoDimensionCodeType.QRCODE_MICRO,
            _ => TwoDimensionCodeType.QRCODE_MODEL2
        };
    }

    private Size2DCode MapQRCodeSize(Controllers.QRCodeSize size)
    {
        return size switch
        {
            Controllers.QRCodeSize.Large => Size2DCode.LARGE,
            Controllers.QRCodeSize.ExtraLarge => Size2DCode.EXTRA,
            _ => Size2DCode.NORMAL
        };
    }

    private CorrectionLevel2DCode MapQRCodeCorrectionLevel(Controllers.QRCodeCorrectionLevel level)
    {
        return level switch
        {
            Controllers.QRCodeCorrectionLevel.Percent15 => CorrectionLevel2DCode.PERCENT_15,
            Controllers.QRCodeCorrectionLevel.Percent25 => CorrectionLevel2DCode.PERCENT_25,
            Controllers.QRCodeCorrectionLevel.Percent30 => CorrectionLevel2DCode.PERCENT_30,
            _ => CorrectionLevel2DCode.PERCENT_7
        };
    }

    private CodePage? MapCodePage(string codePageName)
    {
        return codePageName.ToUpper() switch
        {
            "PC437" or "PC437_USA" => CodePage.PC437_USA_STANDARD_EUROPE_DEFAULT,
            "PC858" or "PC858_EURO" => CodePage.PC858_EURO,
            "KATAKANA" => CodePage.KATAKANA,
            "PC850" => CodePage.PC850_MULTILINGUAL,
            "WPC1252" => CodePage.WPC1252,
            _ => null
        };
    }
}
