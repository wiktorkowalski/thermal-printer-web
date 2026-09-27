import type { ReactNode } from "react";
import { X } from "lucide-react";

/** Bottom sheet for phones; hidden from the large-screen layout. */
export function Sheet({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return (
    <div className="fixed inset-0 z-50 lg:hidden">
      <button type="button" aria-label="Close" onClick={onClose} className="absolute inset-0 bg-black/35" />
      <section
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="absolute inset-x-0 bottom-0 max-h-[80vh] overflow-y-auto rounded-t-[22px] bg-surface px-4 pt-2.5 pb-8 shadow-[0_-12px_30px_rgba(0,0,0,.18)]"
      >
        <div className="mx-auto mb-3 h-[5px] w-10 rounded-full bg-line-strong" aria-hidden="true" />
        <div className="mb-3 flex items-center justify-between">
          <span className="font-serif text-2xl">{title}</span>
          <button type="button" onClick={onClose} aria-label="Close" className="flex size-11 items-center justify-center rounded-full hover:bg-well">
            <X className="size-5" aria-hidden="true" />
          </button>
        </div>
        {children}
      </section>
    </div>
  );
}
