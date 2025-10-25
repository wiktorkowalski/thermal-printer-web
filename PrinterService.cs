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
    Task PrintCustomContent(List<CustomPrintContent> content);
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

    public async Task PrintCustomContent(List<CustomPrintContent> content)
    {
        try
        {
            _logger.LogInformation("Connecting to printer at {Address}", _printerAddress);
            var printer = new ImmediateNetworkPrinter(new ImmediateNetworkPrinterSettings() { ConnectionString = _printerAddress, PrinterName = "ThermalPrinter" });
            var e = new EPSON();
            var byteContent = new List<byte[]>();

            foreach (var item in content)
            {
                if (item.Alignment == CustomPrintAlignment.Left) byteContent.Add(e.LeftAlign());
                if (item.Alignment == CustomPrintAlignment.Center) byteContent.Add(e.CenterAlign());
                if (item.Alignment == CustomPrintAlignment.Right) byteContent.Add(e.RightAlign());
                switch (item.Type)
                {
                    case CustomPrintContentType.Text:
                        byteContent.Add(e.SetStyles(PrintStyle.Bold | PrintStyle.DoubleHeight | PrintStyle.DoubleWidth));
                        byteContent.Add(e.PrintLine(item.Content));
                        break;
                    case CustomPrintContentType.Image:
                        // Implement image printing logic here
                        break;
                    case CustomPrintContentType.Barcode:
                        byteContent.Add(e.PrintBarcode(BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL, item.Content));
                        break;
                    case CustomPrintContentType.Qrcode:
                        byteContent.Add(e.CenterAlign());
                        byteContent.Add(e.SetStyles(PrintStyle.DoubleHeight | PrintStyle.DoubleWidth | PrintStyle.Bold));
                        byteContent.Add(e.PrintQRCode(item.Content, type: TwoDimensionCodeType.QRCODE_MODEL2, size: Size2DCode.EXTRA));
                        break;
                    default:
                        _logger.LogWarning("Unsupported print content type: {Type}", item.Type);
                        break;
                }
            }
            // Print all collected content
            byteContent.Add(e.FullCutAfterFeed(10));
            await printer.WriteAsync(ByteSplicer.Combine(byteContent.ToArray()));

            // Close printer connection
            _logger.LogInformation("Printing complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing custom content");
        }
    }
}
