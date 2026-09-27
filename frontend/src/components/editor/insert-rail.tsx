import type { ContentType } from "@/types/printer";
import { cn } from "@/lib/utils";
import { CODE_PAGES, type JobSettings } from "@/editor/document";
import { INSERT_ITEMS } from "@/editor/insert-items";
import { SectionLabel, Switch } from "./controls";

export function InsertList({ onInsert, compact = false }: { onInsert: (type: ContentType) => void; compact?: boolean }) {
  return (
    <div className={cn("flex flex-col gap-0.5", compact && "gap-1")}>
      {INSERT_ITEMS.map(({ type, label, key, icon: Icon }) => (
        <button
          key={type}
          type="button"
          onClick={() => onInsert(type)}
          aria-keyshortcuts={key}
          className="flex h-11 items-center gap-3 rounded-lg px-3 text-left text-sm text-ink hover:bg-well"
        >
          <Icon className="size-[18px]" aria-hidden="true" />
          <span className="grow">{label}</span>
          <kbd className="rounded border border-line px-1.5 font-mono text-[11px] text-ink-3">{key}</kbd>
        </button>
      ))}
    </div>
  );
}

export function JobSettingsPanel({ settings, onChange }: { settings: JobSettings; onChange: (patch: Partial<JobSettings>) => void }) {
  return (
    <div className="flex flex-col gap-3 px-3">
      <SectionLabel>Job settings</SectionLabel>
      <label className="flex items-center justify-between gap-3 text-sm">
        <span className="whitespace-nowrap">Code page</span>
        <select
          value={settings.codePage}
          onChange={(e) => onChange({ codePage: e.target.value })}
          className="h-9 w-28 rounded-md border border-line bg-surface px-2 font-mono text-[13px]"
        >
          {CODE_PAGES.map((cp) => (
            <option key={cp}>{cp}</option>
          ))}
        </select>
      </label>
      <label className="flex items-center justify-between gap-3 text-sm">
        <span className="whitespace-nowrap">Feed before cut</span>
        <span className="flex items-center gap-1 font-mono text-[13px]">
          <input
            type="number"
            min={0}
            max={10}
            value={settings.feedLinesAfterPrint}
            onChange={(e) => onChange({ feedLinesAfterPrint: Math.max(0, Math.min(10, Number(e.target.value) || 0)) })}
            className="h-9 w-14 rounded-md border border-line bg-surface px-2 text-right"
          />
          lines
        </span>
      </label>
      <div className="flex items-center justify-between gap-3 text-sm">
        <span>Auto-cut</span>
        <Switch label="Auto-cut" checked={settings.autoCut} onChange={(autoCut) => onChange({ autoCut })} />
      </div>
    </div>
  );
}
