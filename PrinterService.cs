using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using ESCPOS_NET;
using System.Threading.Tasks;

namespace ThermalPrinterWeb
{
    public interface IPrinterService
    {
        Task Print(string name, string message);
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
                await printer.WriteAsync(ByteSplicer.Combine(e.PrintLine($"Name: {name}"), e.PrintLine($"Message: {message}"), e.PrintLine("--------------------------------")).ToArray());

                // Close printer connection
                _logger.LogInformation("Printing complete");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error printing");
            }
        }
    }
}
