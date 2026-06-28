namespace ThermalPrinterWeb.Models;

// State read via ESC/POS real-time status (DLE EOT) — used over GS r because GS r
// stalls when the printer is offline/in error, exactly when status matters.
public record PrinterStatus(
    bool Reachable,
    bool Online,
    bool CoverOpen,
    bool PaperOut,
    bool PaperLow,
    string? Raw = null)
{
    public bool Ready => Reachable && Online && !CoverOpen && !PaperOut;

    // Prefer the specific, actionable cause: an open cover also reports offline, but
    // "cover open" tells the user what to actually do.
    public string? NotReadyReason =>
        !Reachable ? "printer unreachable"
        : CoverOpen ? "cover open"
        : PaperOut ? "paper out"
        : !Online ? "printer offline"
        : null;
}
