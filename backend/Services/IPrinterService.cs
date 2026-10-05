using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services;

public interface IPrinterService
{
    Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null);

    Task<PrinterStatus> GetStatusAsync();

    // Sound: the buzzer, as before the mode came. Light and SoundAndLight use the error light.
    Task<bool> BeepAsync(int count, int duration, SignalMode mode = SignalMode.Sound);
}
