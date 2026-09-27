import { useEffect, useRef } from "react";
import { Printer, Trash2, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { clearTray, removeFromTray, type TrayEntry } from "@/lib/tray";
import { useTray } from "@/hooks/use-tray";
import { usePrintJob } from "@/hooks/use-print-job";
import type { PrinterStatusState } from "@/hooks/use-printer-status";
import { isBlocked } from "@/lib/printer-light";
import { PaperGutters } from "./paper/paper-context";
import { PaperDocument } from "./paper/paper-document";
import { Toast } from "./print-chrome";

function when(iso: string): string {
  const date = new Date(iso);
  const time = date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return date.toDateString() === new Date().toDateString() ? time : `${date.toLocaleDateString([], { day: "numeric", month: "short" })} ${time}`;
}

/** A tiny copy of the printed strip, torn off at both ends. */
function Thumbnail({ entry, index }: { entry: TrayEntry; index: number }) {
  const content = entry.request?.content ?? [];
  return (
    <div
      className="w-[124px] shrink-0 drop-shadow-[0_6px_10px_var(--paper-shadow)]"
      style={{ transform: `rotate(${[-2, 1.5, -1, 2][index % 4]}deg)` }}
      aria-hidden="true"
    >
      <div className="paper-tear-top" />
      <div className="paper-font max-h-[170px] overflow-hidden [mask-image:linear-gradient(black_75%,transparent)]" style={{ fontSize: "4.3px" }}>
        {content.length > 0 ? (
          <PaperGutters.Provider value={false}>
            <PaperDocument content={content} feedLines={0} autoCut={false} />
          </PaperGutters.Provider>
        ) : (
          <div className="paper-sheet flex h-24 items-center justify-center font-sans text-[10px] text-paper-faint">no copy</div>
        )}
      </div>
    </div>
  );
}

export function TrayDrawer({ open, onClose, printer }: { open: boolean; onClose: () => void; printer: PrinterStatusState }) {
  const entries = useTray();
  const job = usePrintJob(printer);
  const closeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    closeRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return <Toast notice={job.notice} onDismiss={job.dismiss} />;

  return (
    <div className="fixed inset-0 z-50">
      <button type="button" aria-label="Close tray" tabIndex={-1} onClick={onClose} className="absolute inset-0 bg-ink/25" />
      <section
        role="dialog"
        aria-modal="true"
        aria-labelledby="tray-title"
        className="paper-feed absolute inset-y-0 right-0 flex w-[min(400px,100vw)] flex-col bg-bg shadow-[-12px_0_30px_rgba(0,0,0,.18)]"
      >
        <header className="flex items-center justify-between border-b border-line px-6 py-4">
          <div className="flex items-baseline gap-3">
            <h2 id="tray-title" className="font-serif text-[30px] leading-none">
              Tray
            </h2>
            <span className="text-xs text-ink-2">this browser · last 20</span>
          </div>
          <button ref={closeRef} type="button" onClick={onClose} aria-label="Close tray" className="flex size-11 items-center justify-center rounded-full hover:bg-well">
            <X className="size-5" aria-hidden="true" />
          </button>
        </header>

        <ol className="flex grow flex-col gap-6 overflow-y-auto px-6 py-6">
          {entries.length === 0 && (
            <li className="rounded-xl border border-dashed border-line-strong p-5 text-sm text-ink-2">
              Nothing printed yet. Every print lands here, so you can print it again.
            </li>
          )}
          {entries.map((entry, index) => (
            <li key={entry.id} className="flex items-start gap-4">
              <Thumbnail entry={entry} index={index} />
              <div className="flex min-w-0 flex-col gap-1.5 pt-1.5 text-sm">
                <span className="truncate font-semibold">{entry.title}</span>
                <span className="font-mono text-xs text-ink-2">
                  {when(entry.printedAt)} · {entry.mode}
                </span>
                {!entry.request && <span className="text-xs text-ink-2">Too large to keep a copy.</span>}
                <div className="mt-1 flex gap-1.5">
                  <button
                    type="button"
                    disabled={!entry.request || job.printing || isBlocked(printer)}
                    onClick={() => entry.request && void job.print(entry.request, entry.title, entry.mode)}
                    className={cn(
                      "flex h-9 items-center gap-1.5 rounded-full border border-line-strong px-3.5 text-[13px] hover:bg-well disabled:opacity-40",
                    )}
                  >
                    <Printer className="size-3.5" aria-hidden="true" />
                    Reprint
                  </button>
                  <button
                    type="button"
                    onClick={() => removeFromTray(entry.id)}
                    aria-label={`Remove ${entry.title} from the tray`}
                    className="flex size-9 items-center justify-center rounded-full text-ink-2 hover:bg-well"
                  >
                    <Trash2 className="size-3.5" aria-hidden="true" />
                  </button>
                </div>
              </div>
            </li>
          ))}
        </ol>

        {entries.length > 0 && (
          <footer className="border-t border-line px-6 py-4">
            <button type="button" onClick={clearTray} className="h-11 text-sm text-danger-text hover:underline">
              Empty the tray
            </button>
          </footer>
        )}
      </section>
      <Toast notice={job.notice} onDismiss={job.dismiss} />
    </div>
  );
}
