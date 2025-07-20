using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using ESCPOS_NET;
using System.Net.Sockets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace ThermalPrinterWeb
{
    public interface IPrinterService
    {
        Task Print(string name, string message);
        Task Print(string name, string message, byte[] imageBytes);
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
    }
}
