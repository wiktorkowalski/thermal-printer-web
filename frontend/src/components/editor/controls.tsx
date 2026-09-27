import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

export function SectionLabel({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("text-[11px] font-semibold tracking-[1.4px] text-ink-3 uppercase", className)}>{children}</div>;
}

interface SegmentedProps<T extends string> {
  label: string;
  value: T;
  options: { value: T; label: ReactNode; title?: string }[];
  onChange: (value: T) => void;
  className?: string;
}

export function Segmented<T extends string>({ label, value, options, onChange, className }: SegmentedProps<T>) {
  return (
    <div role="radiogroup" aria-label={label} className={cn("grid gap-[3px] rounded-[10px] bg-well p-[3px]", className)} style={{ gridTemplateColumns: `repeat(${options.length}, minmax(0, 1fr))` }}>
      {options.map((option) => {
        const active = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={active}
            title={option.title}
            onClick={() => onChange(option.value)}
            className={cn(
              "h-10 rounded-lg px-1 text-[13px] transition-colors",
              active ? "bg-surface text-ink shadow-[0_1px_2px_rgba(0,0,0,.08)]" : "text-ink-2 hover:text-ink",
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

export function ToggleChip({
  pressed,
  onClick,
  children,
  className,
}: {
  pressed: boolean;
  onClick: () => void;
  children: ReactNode;
  className?: string;
}) {
  return (
    <button
      type="button"
      aria-pressed={pressed}
      onClick={onClick}
      className={cn(
        "h-11 rounded-lg border text-[13px] transition-colors",
        pressed ? "border-ink bg-ink text-on-ink" : "border-line bg-surface text-ink hover:border-line-strong",
        className,
      )}
    >
      {children}
    </button>
  );
}

export function Switch({ checked, onChange, label }: { checked: boolean; onChange: (value: boolean) => void; label: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
      className={cn("relative h-6 w-10 rounded-full transition-colors", checked ? "bg-ink" : "bg-line-strong")}
    >
      <span className={cn("absolute top-[3px] size-[18px] rounded-full bg-surface transition-all", checked ? "left-[19px]" : "left-[3px]")} />
    </button>
  );
}

export const inputClass =
  "h-11 w-full rounded-lg border border-line bg-surface px-3 text-[15px] text-ink outline-none focus:border-accent";

export const fieldLabelClass = "text-[13px] text-ink-2";

export const quietButtonClass =
  "flex h-11 items-center justify-center gap-2 rounded-lg border border-line px-3 text-[13px] text-ink hover:bg-well disabled:opacity-40";
