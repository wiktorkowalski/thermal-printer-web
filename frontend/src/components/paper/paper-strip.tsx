import { useContext, type ReactNode } from "react";
import { Scissors } from "lucide-react";
import { cn } from "@/lib/utils";
import { CHARS_PER_LINE } from "@/lib/printer-constants";
import { MARGIN_CH, PAPER_WIDTH_CH } from "@/lib/paper";
import { PaperGutters } from "./paper-context";

export type LightTone = "ready" | "busy" | "error" | "unknown";

const LIGHT: Record<LightTone, string> = {
  ready: "#5fd08a",
  busy: "#f0b43c",
  error: "#ff6a4a",
  unknown: "#8a8375",
};

/** The printer's front edge. The paper comes out of the slot at its bottom. */
export function PrinterBody({ tone, label }: { tone: LightTone; label: string }) {
  const light = LIGHT[tone];
  return (
    <div
      className="relative z-10 flex h-[58px] w-[min(540px,calc(100vw-20px))] items-center justify-between rounded-t-2xl rounded-b-lg px-5"
      style={{
        background: "linear-gradient(180deg, #3a352d, #1f1c18)",
        boxShadow: "0 10px 22px rgba(40,30,15,.28), inset 0 1px 0 rgba(255,255,255,.08)",
      }}
    >
      <div className="flex items-center gap-2">
        <span
          className={cn("size-[7px] rounded-full", tone === "busy" || tone === "error" ? "blink" : undefined)}
          style={{ background: light, boxShadow: `0 0 8px ${light}` }}
        />
        <span className="font-mono text-[10px] tracking-[2px] text-[#b9b09d]">{label}</span>
      </div>
      <span className="hidden font-mono text-[10px] tracking-[3px] text-[#8a8375] sm:inline">VRETTI · V330M</span>
      <span
        className="size-[26px] rounded-full bg-[#2b2721]"
        style={{ boxShadow: "inset 0 1px 2px rgba(0,0,0,.6), 0 1px 0 rgba(255,255,255,.06)" }}
      />
      <span className="absolute inset-x-[50px] bottom-[6px] h-[5px] rounded-[3px] bg-[#0b0a08] shadow-[inset_0_2px_2px_rgba(0,0,0,.8)]" />
    </div>
  );
}

const ROW_GRID = "grid grid-cols-[0_auto_0] md:grid-cols-[112px_auto_112px]";
const BARE_GRID = "grid grid-cols-[0_auto_0]";

function useRowGrid() {
  return useContext(PaperGutters) ? ROW_GRID : BARE_GRID;
}

interface PaperRowProps {
  left?: ReactNode;
  right?: ReactNode;
  children?: ReactNode;
  className?: string;
  paperClassName?: string;
  /** Pads the content to the 72 mm printable area. */
  printable?: boolean;
  overlay?: ReactNode;
}

/** One horizontal band of the strip: gutter · paper · gutter. */
export function PaperRow({ left, right, children, className, paperClassName, printable = true, overlay }: PaperRowProps) {
  const grid = useRowGrid();
  const gutter = grid === ROW_GRID;
  return (
    <div className={cn(grid, "relative", className)}>
      <div className={cn("hidden items-center justify-end gap-2 pr-3.5 font-sans text-[11px] leading-tight text-ink-3", gutter && "md:flex")}>{left}</div>
      <div
        className={cn("paper-sheet relative", paperClassName)}
        style={{ width: `${PAPER_WIDTH_CH}ch`, paddingInline: printable ? `${MARGIN_CH}ch` : undefined }}
      >
        {children}
        {overlay}
      </div>
      <div className={cn("hidden flex-col justify-center gap-0.5 pl-3.5 font-mono text-[11px] text-ink-3", gutter && "md:flex")}>{right}</div>
    </div>
  );
}

/**
 * Leading paper plus the column ruler: a tick per character, a long tick every
 * 12. The leading paper leaves room for the text toolbar above the first block.
 */
export function PaperRuler() {
  return (
    <PaperRow paperClassName="paper-curl pt-[68px] pb-1" right={<span className="pt-[62px]">{CHARS_PER_LINE.normal} col</span>}>
      <div className="relative h-1.5" aria-hidden="true">
        <div className="paper-ruler absolute inset-0 opacity-70" />
        <div className="paper-ruler-major absolute inset-x-0 -top-[3px] h-[9px]" />
      </div>
      <div className="flex justify-between font-mono text-[9px] text-paper-faint" aria-hidden="true">
        <span>0</span>
        <span>12</span>
        <span>24</span>
        <span>36</span>
        <span>48</span>
      </div>
    </PaperRow>
  );
}

/** Blank paper for `lines` line feeds. */
export function FeedSpace({ lines }: { lines: number }) {
  return <div style={{ height: `${lines * 2.5}ch` }} />;
}

export function CutRow({ left, right }: { left?: ReactNode; right?: ReactNode }) {
  return (
    <PaperRow
      left={
        left ?? (
          <>
            <span className="font-semibold tracking-[1px]">CUT</span>
            <Scissors className="size-4" aria-hidden="true" />
          </>
        )
      }
      right={right}
      paperClassName="flex h-[18px] items-center"
    >
      <div className="grow border-t-[1.5px] border-dashed border-paper-faint" />
    </PaperRow>
  );
}

export function TearEdge({ top = false }: { top?: boolean }) {
  const grid = useRowGrid();
  return (
    <div className={grid} aria-hidden="true">
      <div />
      <div className={top ? "paper-tear-top" : "paper-tear"} style={{ width: `${PAPER_WIDTH_CH}ch` }} />
      <div />
    </div>
  );
}
