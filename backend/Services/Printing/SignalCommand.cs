using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// The buzzer and the error light of the printer, for the beep call and for a Signal block.
// Nothing sends a signal by itself: only a beep call or a Signal block that the caller put in the document.
internal static class SignalCommand
{
    // ESC B n t and ESC C m t n: the count and the time are one byte each. 1 to 9 is the range of ESC B;
    // the manual gives ESC C 1 to 20, the service keeps one range for both.
    // Min and Max hold for the count and for the duration.
    internal const int Min = 1;
    internal const int Max = 9;

    // The manual: one step of the time is 50 ms. Not measured on this printer.
    internal const int DurationStepMs = 50;

    internal static readonly string ModeNames = BlockEnums.Names<SignalMode>();

    private const byte Esc = 0x1B;

    // Sound is ESC B n t, the buzzer command in use since the first beep: the bytes of a call with no mode do not change.
    // Light and SoundAndLight are ESC C m t n with n = 2 and 3 (read at the printer 2026-10-05: 1B 43 04 08 02 flashes the light, no sound).
    // The caller checks the range of the count and the duration, or clamps them, first.
    public static byte[] Build(SignalMode mode, int count, int duration)
    {
        // A number with no name would go to the printer as the n byte of ESC C.
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        return mode == SignalMode.Sound
            ? [Esc, 0x42, (byte)count, (byte)duration]
            : [Esc, 0x43, (byte)count, (byte)duration, (byte)mode];
    }

    // A name only, letter case aside: Enum.TryParse also takes "7" and "1,2". No text: Sound.
    public static bool TryParseMode(string? text, out SignalMode mode)
    {
        mode = SignalMode.Sound;
        if (string.IsNullOrEmpty(text))
            return true;

        foreach (var value in Enum.GetValues<SignalMode>())
        {
            if (string.Equals(text, value.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                mode = value;
                return true;
            }
        }

        return false;
    }
}
