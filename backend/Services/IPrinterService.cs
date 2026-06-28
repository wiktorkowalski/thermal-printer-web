using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services;

public interface IPrinterService
{
    Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null);

    Task<PrinterStatus> GetStatusAsync();
}
