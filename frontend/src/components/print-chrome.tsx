import type { ReactNode } from "react";
import { Printer, RefreshCw, X } from "lucide-react";
import { cn } from "@/lib/utils";
import type { Notice } from "@/hooks/use-print-job";
import { printerFault, type PrinterFault, type PrinterStatusState } from "@/hooks/use-printer-status";
import { isBlocked } from "@/lib/printer-light";

export function Toast({ notice, onDismiss }: { notice: Notice | null; onDismiss: () => void }) {
  if (!notice) return null;
  return (
    <div
      role={notice.tone === "error" ? "alert" : "status"}
      className={cn(
        "paper-feed fixed bottom-28 left-1/2 z-50 flex max-w-[min(480px,calc(100vw-32px))] -translate-x-1/2 items-start gap-3 rounded-xl px-4 py-3 text-sm shadow-[0_12px_30px_rgba(0,0,0,.18)] lg:bottom-10",
        notice.tone === "error" ? "bg-danger text-white" : "bg-ink text-on-ink",
      )}
    >
      <div className="flex flex-col gap-0.5">
        <span className="font-medium">{notice.message}</span>
        {notice.detail && <span className="opacity-85">{notice.detail}</span>}
      </div>
      <button type="button" onClick={onDismiss} aria-label="Dismiss" className="-mr-1 flex size-6 shrink-0 items-center justify-center rounded opacity-80 hover:opacity-100">
        <X className="size-4" aria-hidden="true" />
      </button>
    </div>
  );
}

interface PrintDockProps {
  printing: boolean;
  blocked?: boolean;
  label?: string;
  onPrint: () => void;
  /** Summary shown next to the button on wider screens. */
  meta?: ReactNode;
  /** Round buttons on the left on phones. */
  leading?: ReactNode;
}

export function PrintDock({ printing, blocked = false, label = "Print", onPrint, meta, leading }: PrintDockProps) {
  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-0 z-40 flex items-end justify-between gap-3 bg-gradient-to-t from-bg via-bg/90 to-transparent px-4 pt-8 pb-5 lg:inset-x-auto lg:right-8 lg:bottom-8 lg:bg-none lg:p-0">
      <div className="pointer-events-auto flex items-center gap-2 lg:hidden">{leading}</div>
      <div className="pointer-events-auto flex items-center gap-5">
        {meta && <div className="hidden flex-col items-end gap-0.5 text-[13px] text-ink-2 sm:flex">{meta}</div>}
        <button
          type="button"
          onClick={onPrint}
          disabled={printing || blocked}
          aria-busy={printing}
          title={blocked ? "The printer is not ready" : undefined}
          className={cn(
            "flex h-14 items-center gap-3 rounded-full px-8 text-[17px] font-semibold whitespace-nowrap transition-colors lg:h-[60px] lg:px-9",
            printing || blocked
              ? "border border-line-strong bg-well text-ink-2"
              : "bg-accent text-on-accent shadow-[0_10px_24px_color-mix(in_srgb,var(--accent)_30%,transparent)] hover:brightness-110",
          )}
        >
          {printing ? <span className="blink size-2.5 rounded-full bg-warn" aria-hidden="true" /> : <Printer className="size-5" aria-hidden="true" />}
          {printing ? "Printing…" : label}
        </button>
      </div>
    </div>
  );
}

/** A dashed box for a state with nothing to list: loading, empty, journal off, read fault. */
export const noteClass = "rounded-xl border border-dashed border-line-strong p-5 text-sm text-ink-2";

export const pillButtonClass =
  "flex h-9 items-center gap-1.5 rounded-full border border-line-strong px-3.5 text-[13px] text-ink hover:bg-well disabled:opacity-40";

export const roundButtonClass =
  "flex size-14 items-center justify-center rounded-full border border-line-strong bg-surface shadow-[0_4px_12px_rgba(60,45,20,.1)]";

const STEPS: Record<PrinterFault, { title: string; steps: string[] }> = {
  cutter: {
    title: "Cutter error",
    steps: ["Switch the printer off.", "Open the cover and take the paper out of the cutter.", "Close the cover and switch the printer on."],
  },
  unrecoverable: {
    title: "Printer fault",
    steps: ["Switch the printer off.", "Wait 10 seconds, then switch it on again.", "If the fault comes back, the printer needs a repair."],
  },
  autoRecoverable: {
    title: "Printer paused",
    steps: ["Wait a few minutes: the print head cools down.", "The light turns green by itself."],
  },
  recoverable: {
    title: "Printer error",
    steps: ["Open the cover and clear the paper path.", "Close the cover until it clicks.", "If the light stays red, switch the printer off and on again."],
  },
  paper: {
    title: "Out of paper",
    steps: ["Open the cover.", "Drop in a new 80 mm roll, paper end towards you.", "Close the cover. The light turns green."],
  },
  cover: {
    title: "Cover open",
    steps: ["Close the cover until it clicks.", "Wait for the light to turn green."],
  },
  offline: {
    title: "Printer offline",
    steps: ["Check that the printer is switched on.", "Check the network cable at the back.", "Wait about 20 seconds after power-on."],
  },
  error: {
    title: "Printer not ready",
    steps: ["Open and close the cover.", "Switch the printer off and on again."],
  },
};

/** Shown above the paper when the printer reports it cannot print. */
export function PrinterAlert({ printer }: { printer: PrinterStatusState }) {
  const { status } = printer;
  if (!isBlocked(printer) || !status) return null;
  const { title, steps } = STEPS[printerFault(status)];
  return (
    <section
      role="alert"
      className="mb-5 flex w-[min(540px,calc(100vw-20px))] flex-col gap-3 rounded-2xl border border-danger/40 bg-surface px-5 py-4 shadow-[0_8px_24px_rgba(0,0,0,.08)]"
    >
      <div className="flex items-center justify-between gap-3">
        <h2 className="font-serif text-[28px] leading-none">{title}</h2>
        <button
          type="button"
          onClick={printer.refresh}
          className="flex h-11 items-center gap-2 rounded-full border border-line-strong px-4 text-sm hover:bg-well"
        >
          <RefreshCw className="size-4" aria-hidden="true" />
          Check again
        </button>
      </div>
      <ol className="flex flex-col gap-1.5 text-[15px]">
        {steps.map((step, i) => (
          <li key={step} className="flex items-baseline gap-3">
            <span className="font-mono text-xs text-ink-3">{String(i + 1).padStart(2, "0")}</span>
            {step}
          </li>
        ))}
      </ol>
      <p className="text-[13px] text-ink-2">Your work stays here. Print again when the light is green.</p>
    </section>
  );
}

