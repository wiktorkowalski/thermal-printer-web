import { useRef, useState } from "react";
import { Download, FolderOpen, Trash2, Upload } from "lucide-react";
import type { PrintRequest } from "@/types/printer";
import { cn } from "@/lib/utils";
import { validateTemplateName } from "@/lib/validation";
import { BLOCK_LABELS, toPrintContent, type Block, type EditorDocument } from "@/editor/document";
import { deleteTemplate, documentToRequest, listTemplates, requestToDocument, saveTemplate, type SavedTemplate } from "@/editor/storage";
import { SectionLabel, quietButtonClass } from "./controls";

interface TemplatePanelProps {
  name: string;
  dirty: boolean;
  doc: EditorDocument;
  selectedId: string | null;
  onRename: (name: string) => void;
  onSaved: () => void;
  onLoad: (name: string, doc: EditorDocument) => void;
  onSelect: (id: string) => void;
  onMoveTo: (id: string, index: number) => void;
  onNotice: (message: string, tone?: "ok" | "error") => void;
}

function summary(block: Block): string {
  switch (block.type) {
    case "Text":
    case "QRCode":
    case "Barcode":
      return (block.content ?? "").split("\n")[0].slice(0, 12) || "empty";
    case "Separator":
      return `${block.separatorChar ?? "-"} × ${block.separatorLength ?? 32}`;
    case "LineFeed":
      return `× ${block.lines ?? 1}`;
    case "Cut":
      return block.partialCut ? "partial" : "full";
    case "Image":
      return block.content ? "1-bit" : "empty";
    default:
      return "";
  }
}

export function TemplatePanel(props: TemplatePanelProps) {
  const { name, dirty, doc, selectedId, onRename, onSaved, onLoad, onSelect, onMoveTo, onNotice } = props;
  const [open, setOpen] = useState(false);
  const [templates, setTemplates] = useState<SavedTemplate[]>([]);
  const [dragId, setDragId] = useState<string | null>(null);
  const importRef = useRef<HTMLInputElement>(null);
  const selected = doc.blocks.find((b) => b.id === selectedId);

  const save = () => {
    const check = validateTemplateName(name);
    if (!check.isValid) return onNotice((check.error ?? "Invalid name").replace(/^\[ERROR\]\s*/, ""), "error");
    if (toPrintContent(doc.blocks).length === 0) return onNotice("Add some content before you save.", "error");
    if (!saveTemplate(name.trim(), doc)) return onNotice("The browser refused to store the template. Storage full?", "error");
    onSaved();
    onNotice(`Saved “${name.trim()}”.`);
  };

  const toggleOpen = () => {
    if (!open) setTemplates(listTemplates());
    setOpen(!open);
  };

  const exportJson = () => {
    const blob = new Blob([JSON.stringify(documentToRequest(doc, "web/template"), null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${(name.trim() || "template").replace(/[^\w-]+/g, "-")}.json`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const importJson = async (file: File | undefined) => {
    if (!file) return;
    try {
      const request = JSON.parse(await file.text()) as PrintRequest;
      if (!Array.isArray(request.content)) throw new Error("no content array");
      onLoad(file.name.replace(/\.json$/i, ""), requestToDocument(request));
      onNotice(`Imported ${request.content.length} blocks.`);
    } catch {
      onNotice("That file is not a template JSON.", "error");
    }
  };

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-2">
        <div className="flex items-baseline justify-between px-1">
          <SectionLabel>Template</SectionLabel>
          {dirty && (
            <span className="flex items-center gap-1.5 text-xs text-accent-text">
              <span className="size-1.5 rounded-full bg-accent" aria-hidden="true" />
              unsaved
            </span>
          )}
        </div>
        <label htmlFor="template-name" className="sr-only">
          Template name
        </label>
        <input
          id="template-name"
          value={name}
          placeholder="Untitled template"
          onChange={(e) => onRename(e.target.value)}
          className="border-b border-line bg-transparent px-1 py-1 font-serif text-[26px] outline-none placeholder:text-ink-3 focus:border-accent"
        />
        <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_44px_44px] gap-1.5">
          <button type="button" onClick={save} className="h-11 rounded-lg bg-ink px-2 text-[13px] text-on-ink hover:opacity-90">
            Save
          </button>
          <button type="button" onClick={toggleOpen} aria-expanded={open} className={cn(quietButtonClass, "px-2")}>
            <FolderOpen className="size-4" aria-hidden="true" />
            Open
          </button>
          <button type="button" onClick={exportJson} aria-label="Export JSON" title="Export JSON" className={cn(quietButtonClass, "px-0")}>
            <Download className="size-4" aria-hidden="true" />
          </button>
          <button type="button" onClick={() => importRef.current?.click()} aria-label="Import JSON" title="Import JSON" className={cn(quietButtonClass, "px-0")}>
            <Upload className="size-4" aria-hidden="true" />
          </button>
          <input ref={importRef} type="file" accept="application/json,.json" className="sr-only" tabIndex={-1} onChange={(e) => void importJson(e.target.files?.[0])} />
        </div>
        {open && (
          <ul className="flex flex-col gap-1 rounded-[10px] border border-line bg-surface p-1.5">
            {templates.length === 0 && <li className="px-2 py-2 text-[13px] text-ink-2">No saved templates yet.</li>}
            {templates.map((t) => (
              <li key={t.id} className="flex items-center gap-1">
                <button
                  type="button"
                  onClick={() => {
                    onLoad(t.name, requestToDocument(t.content));
                    setOpen(false);
                  }}
                  className="flex h-11 grow items-center justify-between gap-2 rounded-lg px-2.5 text-left text-sm hover:bg-well"
                >
                  <span className="truncate">{t.name}</span>
                  <span className="shrink-0 font-mono text-[11px] text-ink-3">{t.content.content?.length ?? 0} blocks</span>
                </button>
                <button
                  type="button"
                  aria-label={`Delete template ${t.name}`}
                  onClick={() => {
                    deleteTemplate(t.id);
                    setTemplates(listTemplates());
                  }}
                  className="flex size-11 items-center justify-center rounded-lg text-danger-text hover:bg-well"
                >
                  <Trash2 className="size-4" aria-hidden="true" />
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="flex flex-col gap-0.5">
        <SectionLabel className="px-1 pb-2">Blocks · {doc.blocks.length}</SectionLabel>
        <ol className="flex flex-col gap-0.5">
          {doc.blocks.map((block, index) => {
            const active = block.id === selectedId;
            return (
              <li
                key={block.id}
                draggable
                onDragStart={() => setDragId(block.id)}
                onDragOver={(e) => e.preventDefault()}
                onDrop={() => {
                  if (dragId) onMoveTo(dragId, index);
                  setDragId(null);
                }}
              >
                <button
                  type="button"
                  onClick={() => onSelect(block.id)}
                  aria-current={active ? "true" : undefined}
                  className={cn(
                    "flex h-10 w-full items-center gap-2.5 rounded-lg px-2.5 text-left text-sm",
                    active ? "bg-surface shadow-[inset_3px_0_0_var(--accent),0_1px_2px_rgba(0,0,0,.06)]" : "hover:bg-well",
                  )}
                >
                  <span className="grip shrink-0 cursor-grab" aria-hidden="true" />
                  <span className={cn("w-5 font-mono text-[11px]", active ? "text-accent-text" : "text-ink-3")}>{String(index + 1).padStart(2, "0")}</span>
                  <span className={cn("grow whitespace-nowrap", active && "font-semibold")}>{BLOCK_LABELS[block.type]}</span>
                  <span className="max-w-[90px] truncate font-mono text-[11px] text-ink-3">{summary(block)}</span>
                </button>
              </li>
            );
          })}
        </ol>
      </div>

      {selected && (
        <div className="flex flex-col gap-2">
          <SectionLabel className="px-1">Selected block · JSON</SectionLabel>
          <pre className="max-h-56 overflow-auto rounded-lg bg-ink p-3 font-mono text-[11px] leading-relaxed break-all whitespace-pre-wrap text-on-ink">
            {JSON.stringify(
              toPrintContent([selected])[0] ?? { type: selected.type },
              (key, value) => (key === "content" && typeof value === "string" && value.length > 80 ? `${value.slice(0, 40)}… (${value.length} chars)` : value),
              2,
            )}
          </pre>
        </div>
      )}
    </div>
  );
}
