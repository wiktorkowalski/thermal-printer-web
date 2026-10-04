using Microsoft.Extensions.Options;
using ThermalPrinterWeb.Services;

namespace ThermalPrinterWeb.Tests.Support;

// For a PrinterService that a test builds by hand: no printer, so the test cannot reach the network.
internal static class NoPrinter
{
    public static IOptions<PrinterOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new PrinterOptions { Address = "" });
}
