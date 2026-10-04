import { Component, useEffect, useRef, useState, type ReactNode } from "react";
import { Printer, RefreshCw, X } from "lucide-react";
import { cn, when } from "@/lib/utils";
import { loadTrayCopy } from "@/lib/tray";
import type { TrayState } from "@/hooks/use-tray";
import { usePrintJob } from "@/hooks/use-print-job";
import type { PrinterStatusState } from "@/hooks/use-printer-status";
import { isBlocked } from "@/lib/printer-light";
import type { PrintContent } from "@/types/printer";
import { PaperGutters } from "./paper/paper-context";
import { PaperDocument } from "./paper/paper-document";
import { Toast, noteClass, pillButtonClass } from "./print-chrome";

/** A stored job is caller data from any device: a block that does not render must not take the tray down. */
class PreviewBoundary extends Component<{ fallback: ReactNode; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}

function NoPreview({ children }: { children: ReactNode }) {
  return <div className="paper-sheet flex h-24 items-center justify-center font-sans text-[10px] text-paper-faint">{children}</div>;
}

/** A tiny copy of the printed strip, torn off at both ends. The blocks come from the server, one read per job. */
function Thumbnail({ id, index }: { id: string; index: number }) {
  const [copy, setCopy] = useState<PrintContent[] | "failed" | null>(null);

  useEffect(() => {
    let current = true;
    loadTrayCopy(id).then(
      (loaded) => current && setCopy(loaded),
      () => current && setCopy("failed"),
    );
    return () => {
      current = false;
    };
  }, [id]);

  const noPreview = <NoPreview>no preview</NoPreview>;
  return (
    <div
      className="w-[124px] shrink-0 drop-shadow-[0_6px_10px_var(--paper-shadow)]"
      style={{ transform: `rotate(${[-2, 1.5, -1, 2][index % 4]}deg)` }}
      aria-hidden="true"
    >
      <div className="paper-tear-top" />
      <div className="paper-font max-h-[170px] overflow-hidden [mask-image:linear-gradient(black_75%,transparent)]" style={{ fontSize: "4.3px" }}>
        {copy === null ? (
          <NoPreview>…</NoPreview>
        ) : copy === "failed" || copy.length === 0 ? (
          noPreview
        ) : (
          <PreviewBoundary fallback={noPreview}>
            <PaperGutters.Provider value={false}>
              <PaperDocument content={copy} feedLines={0} autoCut={false} />
            </PaperGutters.Provider>
          </PreviewBoundary>
        )}
      </div>
    </div>
  );
}

export function TrayDrawer({ open, onClose, printer, tray }: { open: boolean; onClose: () => void; printer: PrinterStatusState; tray: TrayState }) {
  const job = usePrintJob(printer);
  const closeRef = useRef<HTMLButtonElement>(null);
  const { status, jobs, refresh } = tray;

  useEffect(() => {
    if (!open) return;
    closeRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  // Other devices print too: the list is read again each time the tray opens.
  useEffect(() => {
    if (open) refresh();
  }, [open, refresh]);

  if (!open) return <Toast notice={job.notice} onDismiss={job.dismiss} />;

  return (
    <div className="fixed inset-0 z-50">
      <button type="button" aria-label="Close tray" tabIndex={-1} onClick={onClose} className="absolute inset-0 bg-black/30" />
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
            <span className="text-xs text-ink-2">every device · newest first</span>
          </div>
          <button ref={closeRef} type="button" onClick={onClose} aria-label="Close tray" className="flex size-11 items-center justify-center rounded-full hover:bg-well">
            <X className="size-5" aria-hidden="true" />
          </button>
        </header>

        <ol className="flex grow flex-col gap-6 overflow-y-auto px-6 py-6" aria-busy={status === "loading"}>
          {status === "loading" && <li className={noteClass}>Reading the tray…</li>}
          {status === "off" && <li className={noteClass}>The server keeps no print history. Prints still go out; the tray stays empty.</li>}
          {status === "error" && (
            <li className={cn(noteClass, "flex flex-col items-start gap-3")} role="alert">
              The tray did not load. The server did not answer, or it cannot read its print history at the moment.
              <button type="button" onClick={refresh} className={pillButtonClass}>
                <RefreshCw className="size-3.5" aria-hidden="true" />
                Try again
              </button>
            </li>
          )}
          {status === "ready" && jobs.length === 0 && (
            <li className={noteClass}>Nothing printed yet. Every print lands here, from every device, so you can print it again.</li>
          )}
          {jobs.map((entry, index) => {
            const title = entry.title || "Untitled";
            return (
              <li key={entry.id} className="flex items-start gap-4">
                <Thumbnail id={entry.id} index={index} />
                <div className="flex min-w-0 flex-col gap-1.5 pt-1.5 text-sm">
                  <span className="truncate font-semibold">{title}</span>
                  <span className="truncate font-mono text-xs text-ink-2">
                    {when(entry.createdAt)} · {entry.source || entry.transport}
                  </span>
                  {!entry.canReprint && <span className="text-xs text-ink-2">The server has no copy to print.</span>}
                  <div className="mt-1 flex gap-1.5">
                    <button
                      type="button"
                      disabled={!entry.canReprint || job.printing || isBlocked(printer)}
                      onClick={() => void job.reprint(entry.id, "web/tray")}
                      aria-label={`Reprint ${title}`}
                      className={pillButtonClass}
                    >
                      <Printer className="size-3.5" aria-hidden="true" />
                      Reprint
                    </button>
                  </div>
                </div>
              </li>
            );
          })}
        </ol>

        {tray.hasMore && (
          <footer className="border-t border-line px-6 py-4">
            <button type="button" onClick={tray.loadMore} disabled={tray.loadingMore} className="h-11 text-sm text-ink-2 hover:underline disabled:opacity-40">
              {tray.loadingMore ? "Reading…" : tray.moreFailed ? "The older prints did not load. Try again" : "Show older prints"}
            </button>
          </footer>
        )}
      </section>
      <Toast notice={job.notice} onDismiss={job.dismiss} />
    </div>
  );
}
