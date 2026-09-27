import { AlignCenter, AlignLeft, AlignRight } from "lucide-react";
import { Alignment, type PrintStyle } from "@/types/printer";
import { cn } from "@/lib/utils";
import type { Block } from "@/editor/document";

interface FloatingToolbarProps {
  block: Block;
  onToggleStyle: (style: PrintStyle) => void;
  onAlign: (alignment: Alignment) => void;
}

const STYLE_BUTTONS: { style: PrintStyle; label: string; name: string; className?: string }[] = [
  { style: "Bold", label: "B", name: "Bold", className: "font-bold" },
  { style: "Underline", label: "U", name: "Underline", className: "underline" },
  { style: "DoubleWidth", label: "2W", name: "Double width" },
  { style: "DoubleHeight", label: "2H", name: "Double height" },
];

const ALIGN_BUTTONS = [
  { value: Alignment.Left, name: "Align left", icon: AlignLeft },
  { value: Alignment.Center, name: "Align center", icon: AlignCenter },
  { value: Alignment.Right, name: "Align right", icon: AlignRight },
];

const button = "flex size-11 sm:size-10 shrink-0 items-center justify-center rounded-lg text-[13px] text-on-ink transition-colors hover:bg-on-ink/10";

/** Quick text styling, pinned to the selected text block on the paper. */
export function FloatingToolbar({ block, onToggleStyle, onAlign }: FloatingToolbarProps) {
  const has = (style: PrintStyle) => block.style?.includes(style) ?? false;
  return (
    <div
      role="toolbar"
      aria-label="Text style"
      // Keep focus in the textarea while clicking the toolbar.
      onMouseDown={(e) => e.preventDefault()}
      className={cn(
        "absolute bottom-full left-0 z-30 mb-3 flex max-w-[calc(100vw-20px)] sm:max-w-full items-center gap-0.5 overflow-x-auto rounded-xl bg-ink p-1 font-sans shadow-[0_12px_28px_rgba(0,0,0,.24)]",
      )}
    >
      {STYLE_BUTTONS.map(({ style, label, name, className }) => (
        <button key={style} type="button" aria-label={name} aria-pressed={has(style)} onClick={() => onToggleStyle(style)} className={cn(button, className, has(style) && "bg-on-ink/15")}>
          {label}
        </button>
      ))}
      <button type="button" aria-label="Reverse" aria-pressed={has("ReverseMode")} onClick={() => onToggleStyle("ReverseMode")} className={cn(button, has("ReverseMode") && "bg-on-ink/15")}>
        <span className="bg-on-ink px-1 text-[11px] font-bold text-ink">R</span>
      </button>
      <span className="mx-1 sm:mx-0.5 h-[22px] w-px bg-on-ink/20" aria-hidden="true" />
      {ALIGN_BUTTONS.map(({ value, name, icon: Icon }) => (
        <button
          key={value}
          type="button"
          aria-label={name}
          aria-pressed={(block.alignment ?? Alignment.Center) === value}
          onClick={() => onAlign(value)}
          className={cn(button, (block.alignment ?? Alignment.Center) === value && "bg-on-ink/15")}
        >
          <Icon className="size-4" aria-hidden="true" />
        </button>
      ))}
      <span className="mx-0.5 hidden h-[22px] w-px bg-on-ink/20 sm:block" aria-hidden="true" />
      <button type="button" aria-pressed={has("FontB")} onClick={() => onToggleStyle("FontB")} className={cn(button, "hidden w-auto px-3 whitespace-nowrap sm:flex", has("FontB") && "bg-on-ink/15")}>
        Font B
      </button>
    </div>
  );
}
