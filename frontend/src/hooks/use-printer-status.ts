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

export function describeStatus(state: PrinterStatusState): { tone: StatusTone; label: string; detail: string } {
  const { status, failed } = state;
  if (failed) return { tone: "error", label: "No server", detail: "Cannot reach the print server" };
  if (!status) return { tone: "unknown", label: "Checking…", detail: "Asking the printer" };
  if (status.ready) return { tone: "ready", label: "Ready", detail: "Paper OK · cover closed" };
  if (!status.reachable) return { tone: "error", label: "Offline", detail: "Printer not reachable on the network" };
  if (status.paperOut) return { tone: "error", label: "No paper", detail: "Load a new 80 mm roll" };
  if (status.coverOpen) return { tone: "error", label: "Cover open", detail: "Close the cover" };
  return { tone: "error", label: "Not ready", detail: status.notReadyReason ?? "Printer reports an error" };
}
