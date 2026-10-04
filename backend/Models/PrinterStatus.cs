using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

// State read via ESC/POS real-time status (DLE EOT) — used over GS r because GS r
// stalls when the printer is offline/in error, exactly when status matters.
// The four error flags come from DLE EOT 3. On this printer only the idle value (0x12, no flag)
// is seen on hardware: no error flag is verified.
public record PrinterStatus(
    bool Reachable,
    bool Online,
    bool CoverOpen,
    bool PaperOut,
    [property: Description("Always false on this printer: it has no paper near-end sensor.")]
    bool PaperLow,
    [property: Description("The status bytes as the printer sent them, in hex: n1, n2, n4, n3. 'n3=?' or 'n3=xx!': the error status is unknown.")]
    string? Raw = null,
    [property: Description("The auto-cutter reports an error. Not verified on hardware.")]
    bool CutterError = false,
    [property: Description("A fault that needs a power cycle. Not verified on hardware.")]
    bool UnrecoverableError = false,
    [property: Description("A fault that clears by itself, for example a hot print head. Not verified on hardware.")]
    bool AutoRecoverableError = false,
    [property: Description("A fault that clears when its cause is removed, for example a paper jam. Not verified on hardware.")]
    bool RecoverableError = false)
{
    private bool HasError => CutterError || UnrecoverableError || AutoRecoverableError || RecoverableError;

    public bool Ready => Reachable && Online && !CoverOpen && !PaperOut && !HasError;

    // Prefer the specific, actionable cause: an open cover also reports offline, but
    // "cover open" tells the user what to actually do.
    public string? NotReadyReason =>
        !Reachable ? "printer unreachable"
        : CoverOpen ? "cover open"
        : PaperOut ? "paper out"
        : CutterError ? "cutter error"
        : UnrecoverableError ? "unrecoverable error"
        : AutoRecoverableError ? "auto-recoverable error"
        : RecoverableError ? "recoverable error"
        : !Online ? "printer offline"
        : null;
}
