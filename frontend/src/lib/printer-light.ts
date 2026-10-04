import type { PrintPhase } from "@/hooks/use-print-job";
import { describeStatus, printerFault, type PrinterFault, type PrinterStatusState, type StatusTone } from "@/hooks/use-printer-status";

const LIGHT_LABEL: Record<PrinterFault, string> = {
  offline: "OFFLINE",
  paper: "PAPER",
  cover: "COVER",
  cutter: "CUTTER",
  unrecoverable: "FAULT",
  autoRecoverable: "WAIT",
  recoverable: "ERROR",
  error: "ERROR",
};

/** True when the printer said it cannot print. A server hiccup does not block. */
export function isBlocked(printer: PrinterStatusState): boolean {
  return !!printer.status && !printer.failed && !printer.status.ready;
}

/** Front-panel light and label on the drawn printer. */
export function printerLight(printer: PrinterStatusState, phase: PrintPhase): { tone: StatusTone; label: string } {
  if (phase === "printing") return { tone: "busy", label: "DATA" };
  const { status } = printer;
  if (isBlocked(printer) && status) return { tone: "error", label: LIGHT_LABEL[printerFault(status)] };
  const { tone } = describeStatus(printer);
  return { tone, label: tone === "error" ? "NO LINK" : "POWER" };
}
