import { useCallback, useEffect, useState } from "react";
import type { PrintRequest } from "@/types/printer";
import { printerApi, type PrintError } from "@/lib/api";
import { addToTray } from "@/lib/tray";
import type { PrinterStatusState } from "./use-printer-status";

export interface Notice {
  message: string;
  detail?: string;
  tone: "ok" | "error";
}

/** idle → printing → torn (strip leaves for the tray) → idle */
export type PrintPhase = "idle" | "printing" | "torn";

const TEAR_MS = 900;

export function usePrintJob(printer: PrinterStatusState) {
  const [phase, setPhase] = useState<PrintPhase>("idle");
  const [notice, setNotice] = useState<Notice | null>(null);

  const notify = useCallback((message: string, tone: Notice["tone"] = "ok", detail?: string) => setNotice({ message, tone, detail }), []);
  const dismiss = useCallback(() => setNotice(null), []);

  useEffect(() => {
    if (!notice) return;
    const timer = window.setTimeout(() => setNotice(null), notice.tone === "error" ? 8000 : 4000);
    return () => window.clearTimeout(timer);
  }, [notice]);

  useEffect(() => {
    if (phase !== "torn") return;
    const timer = window.setTimeout(() => setPhase("idle"), TEAR_MS);
    return () => window.clearTimeout(timer);
  }, [phase]);

  const { refresh } = printer;
  const print = useCallback(
    async (request: PrintRequest, title: string, mode: string): Promise<boolean> => {
      setPhase("printing");
      try {
        await printerApi.print(request);
        addToTray(title, mode, request);
        setPhase("torn");
        notify(`Printed · ${new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`, "ok", "Copy kept in the tray.");
        return true;
      } catch (err) {
        const error = err as PrintError;
        setPhase("idle");
        notify(error.message?.replace(/^\[ERROR\]\s*/, "") || "Print failed", "error", error.details);
        return false;
      } finally {
        refresh();
      }
    },
    [notify, refresh],
  );

  return { phase, printing: phase === "printing", notice, notify, dismiss, print };
}
