using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

// The [Description] texts repeat Min and Max of SignalCommand.
public sealed class SignalOptions
{
    [Description("What the printer does: Sound (the buzzer, default), Light (the error light flashes, no sound) or SoundAndLight.")]
    public SignalMode? Mode { get; set; } = SignalMode.Sound;
    [Description("Number of beeps or flashes, 1 to 9. Default 1.")]
    public int? Count { get; set; } = 1;
    [Description("Length of each beep or flash, 1 to 9; one step is about 50 ms. Default 1.")]
    public int? Duration { get; set; } = 1;
}
