import { NavLink } from "react-router-dom";
import { Bell, Moon, Sun } from "lucide-react";
import { cn } from "@/lib/utils";
import { useTheme } from "@/components/theme-provider";
import type { StatusTone } from "@/hooks/use-printer-status";

const MODES = [
  { to: "/", label: "Note", end: true },
  { to: "/template", label: "Template", end: false },
  { to: "/receipt", label: "Receipt", end: false },
];

const DOT: Record<StatusTone, string> = {
  ready: "bg-ok shadow-[0_0_0_3px_color-mix(in_srgb,var(--ok)_20%,transparent)]",
  busy: "bg-warn blink",
  error: "bg-danger",
  unknown: "bg-ink-3",
};

interface AppHeaderProps {
  tone: StatusTone;
  label: string;
  detail: string;
  onBeep: () => void;
}

export function ModeSwitch({ className }: { className?: string }) {
  return (
    <nav aria-label="Mode" className={cn("grid grid-cols-3 gap-1 rounded-full bg-well p-1 text-sm", className)}>
      {MODES.map((mode) => (
        <NavLink
          key={mode.to}
          to={mode.to}
          end={mode.end}
          className={({ isActive }) =>
            cn(
              "flex h-10 items-center justify-center rounded-full px-5 transition-colors",
              isActive ? "bg-ink text-on-ink" : "text-ink-2 hover:text-ink",
            )
          }
        >
          {mode.label}
        </NavLink>
      ))}
    </nav>
  );
}

export function AppHeader({ tone, label, detail, onBeep }: AppHeaderProps) {
  const { theme, setTheme } = useTheme();
  const isDark =
    theme === "dark" || (theme === "system" && typeof window !== "undefined" && window.matchMedia("(prefers-color-scheme: dark)").matches);

  return (
    <header className="sticky top-0 z-30 border-b border-line bg-bg/90 backdrop-blur">
      <div className="mx-auto flex h-[72px] max-w-[1600px] items-center justify-between gap-4 px-4 md:px-8">
        <div className="flex min-w-0 items-baseline gap-3">
          <span className="truncate font-serif text-2xl md:text-[30px]">Vittore’s Printer</span>
          <span className="hidden font-serif text-[17px] text-ink-3 italic xl:inline">80 mm, one roll at a time</span>
        </div>

        <ModeSwitch className="hidden md:grid" />

        <div className="flex items-center gap-2.5 text-[13px] text-ink-2">
          <div
            role="status"
            aria-live="polite"
            title={detail}
            className="flex h-11 items-center gap-2.5 rounded-full border border-line px-3.5"
          >
            <span className={cn("size-2 rounded-full", DOT[tone])} aria-hidden="true" />
            <span className="hidden font-medium text-ink sm:inline">V330M</span>
            <span>{label}</span>
            <span className="hidden lg:inline">· {detail}</span>
          </div>
          <button
            type="button"
            onClick={onBeep}
            aria-label="Beep the printer"
            title="Beep the printer"
            className="flex size-11 items-center justify-center rounded-full border border-line hover:bg-well"
          >
            <Bell className="size-[18px]" aria-hidden="true" />
          </button>
          <button
            type="button"
            onClick={() => setTheme(isDark ? "light" : "dark")}
            aria-label={isDark ? "Switch to light theme" : "Switch to dark theme"}
            className="hidden size-11 items-center justify-center rounded-full border border-line hover:bg-well sm:flex"
          >
            {isDark ? <Sun className="size-[18px]" aria-hidden="true" /> : <Moon className="size-[18px]" aria-hidden="true" />}
          </button>
        </div>
      </div>
      <div className="px-4 pb-3 md:hidden">
        <ModeSwitch />
      </div>
    </header>
  );
}
