import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { printerApi, type PrinterStatus } from "@/lib/api";

// The backend logs every status request at Information, so poll slowly and
// only while the tab is visible.
const POLL_MS = 30_000;

export interface PrinterStatusState {
  status: PrinterStatus | null;
  /** True when the status endpoint itself could not be reached. */
  failed: boolean;
  refresh: () => void;
}

export function usePrinterStatus(): PrinterStatusState {
  const [status, setStatus] = useState<PrinterStatus | null>(null);
  const [failed, setFailed] = useState(false);
  const inFlight = useRef(false);

  const refresh = useCallback(async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    try {
      setStatus(await printerApi.getStatus());
      setFailed(false);
    } catch {
      setFailed(true);
    } finally {
      inFlight.current = false;
    }
  }, []);

  useEffect(() => {
    let timer: number | undefined;
    const start = () => {
      window.clearInterval(timer);
      void refresh();
      timer = window.setInterval(() => void refresh(), POLL_MS);
    };
    const onVisibility = () => {
      if (document.hidden) window.clearInterval(timer);
      else start();
    };
    if (!document.hidden) start();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [refresh]);

  return useMemo(() => ({ status, failed, refresh: () => void refresh() }), [status, failed, refresh]);
}

export type StatusTone = "ready" | "busy" | "error" | "unknown";

/** Why the printer cannot print. "error": a cause with no text of its own. */
export type PrinterFault = "offline" | "paper" | "cover" | "cutter" | "unrecoverable" | "autoRecoverable" | "recoverable" | "error";

/**
 * The one cause to show for a status that is not ready. The four error flags are in the order of
 * `NotReadyReason` in the backend model; they are absent in the answer of an older server.
 */
export function printerFault(status: PrinterStatus): PrinterFault {
  if (!status.reachable) return "offline";
  if (status.paperOut) return "paper";
  if (status.coverOpen) return "cover";
  if (status.cutterError) return "cutter";
  if (status.unrecoverableError) return "unrecoverable";
  if (status.autoRecoverableError) return "autoRecoverable";
  if (status.recoverableError) return "recoverable";
  return "error";
}

const FAULT_TEXT: Record<Exclude<PrinterFault, "error">, { label: string; detail: string }> = {
  offline: { label: "Offline", detail: "Printer not reachable on the network" },
  paper: { label: "No paper", detail: "Load a new 80 mm roll" },
  cover: { label: "Cover open", detail: "Close the cover" },
  cutter: { label: "Cutter error", detail: "Clear the paper from the cutter" },
  unrecoverable: { label: "Printer fault", detail: "Switch the printer off and on" },
  autoRecoverable: { label: "Printer paused", detail: "Wait for the print head to cool down" },
  recoverable: { label: "Printer error", detail: "Clear the paper path" },
};

export function describeStatus(state: PrinterStatusState): { tone: StatusTone; label: string; detail: string } {
  const { status, failed } = state;
  if (failed) return { tone: "error", label: "No server", detail: "Cannot reach the print server" };
  if (!status) return { tone: "unknown", label: "Checking…", detail: "Asking the printer" };
  if (status.ready) return { tone: "ready", label: "Ready", detail: "Paper OK · cover closed" };
  const fault = printerFault(status);
  if (fault !== "error") return { tone: "error", ...FAULT_TEXT[fault] };
  return { tone: "error", label: "Not ready", detail: status.notReadyReason ?? "Printer reports an error" };
}
