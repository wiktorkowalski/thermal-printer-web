import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { Pencil, Plus, Printer, X } from "lucide-react";
import { ContentType } from "@/types/printer";
import { cn } from "@/lib/utils";
import { fileToBase64, printerApi, type PrintError } from "@/lib/api";
import { validateImage } from "@/lib/validation";
import { estimateLengthMm } from "@/lib/paper";
import {
  blockError,
  createBlock,
  editorReducer,
  emptyDocument,
  noteDocument,
  toPrintContent,
  type EditorDocument,
  type EditorState,
} from "@/editor/document";
import { documentToRequest, loadDraft, saveDraft } from "@/editor/storage";
import { describeStatus, type PrinterStatusState } from "@/hooks/use-printer-status";
import { BlockView } from "@/components/paper/block-view";
import { CutRow, FeedSpace, PaperRow, PaperRuler, PrinterBody, TearEdge } from "@/components/paper/paper-strip";
import { FloatingToolbar } from "@/components/editor/floating-toolbar";
import { InsertList, JobSettingsPanel } from "@/components/editor/insert-rail";
import { INSERT_ITEMS } from "@/editor/insert-items";
import { Inspector } from "@/components/editor/inspector";
import { TemplatePanel } from "@/components/editor/template-panel";
import { SectionLabel } from "@/components/editor/controls";

export type EditorMode = "note" | "template";

const draftKey = (mode: EditorMode) => `thermal-printer-draft-${mode}`;

function initialState(mode: EditorMode): EditorState & { name: string } {
  const draft = loadDraft(draftKey(mode));
  if (draft?.doc?.blocks) return { doc: draft.doc, selectedId: null, name: draft.name ?? "" };
  return { doc: mode === "note" ? noteDocument() : emptyDocument(), selectedId: null, name: "" };
}

function isTyping(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  return !!el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA" || el.tagName === "SELECT" || el.isContentEditable);
}

const contentKey = (doc: EditorDocument) => JSON.stringify(toPrintContent(doc.blocks));

interface Notice {
  message: string;
  detail?: string;
  tone: "ok" | "error";
}

export default function Editor({ mode, printer }: { mode: EditorMode; printer: PrinterStatusState }) {
  const navigate = useNavigate();
  const [initial] = useState(() => initialState(mode));
  const [state, dispatch] = useReducer(editorReducer, initial);
  const [name, setName] = useState(initial.name);
  const [savedKey, setSavedKey] = useState(() => contentKey(initial.doc));
  const [printing, setPrinting] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [sheet, setSheet] = useState<"insert" | "inspect" | null>(null);
  const [drag, setDrag] = useState<{ id: string; over: number | null } | null>(null);
  const [imageDots, setImageDots] = useState<Record<string, number>>({});
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const focusText = useRef(false);

  const { doc, selectedId } = state;
  const selectedIndex = doc.blocks.findIndex((b) => b.id === selectedId);
  const selected = selectedIndex >= 0 ? doc.blocks[selectedIndex] : null;
  const printable = useMemo(() => toPrintContent(doc.blocks), [doc.blocks]);
  const lengthMm = estimateLengthMm(
    printable,
    doc.settings,
    doc.blocks.reduce((sum, b) => sum + (b.type === ContentType.Image && b.content ? (imageDots[b.id] ?? 0) : 0), 0),
  );
  const status = describeStatus(printer);
  const lastIsCut = doc.blocks.at(-1)?.type === ContentType.Cut;

  const notify = useCallback((message: string, tone: Notice["tone"] = "ok", detail?: string) => setNotice({ message, tone, detail }), []);

  useEffect(() => {
    if (!notice) return;
    const timer = window.setTimeout(() => setNotice(null), notice.tone === "error" ? 8000 : 4000);
    return () => window.clearTimeout(timer);
  }, [notice]);

  // Keep the draft across reloads. Large images can exceed the quota; the
  // draft then simply stays at its last good version.
  useEffect(() => {
    const timer = window.setTimeout(() => saveDraft(draftKey(mode), { name, doc }), 400);
    return () => window.clearTimeout(timer);
  }, [doc, name, mode]);

  useEffect(() => {
    if (focusText.current && selected?.type === ContentType.Text) {
      focusText.current = false;
      textareaRef.current?.focus();
    }
  }, [selected]);

  const insert = useCallback((type: ContentType) => {
    focusText.current = type === ContentType.Text;
    dispatch({ type: "insert", block: createBlock(type) });
    setSheet(null);
  }, []);

  const insertImage = useCallback(
    async (file: File) => {
      const check = validateImage(file);
      if (!check.isValid) return notify((check.error ?? "Invalid image").replace(/^\[ERROR\]\s*/, ""), "error");
      const content = await fileToBase64(file);
      if (selected?.type === ContentType.Image && !selected.content) {
        dispatch({ type: "update", id: selected.id, patch: { content } });
      } else {
        dispatch({ type: "insert", block: { ...createBlock(ContentType.Image), content } });
      }
    },
    [selected, notify],
  );

  const print = useCallback(async () => {
    if (printing) return;
    const invalid = doc.blocks.find((b) => blockError(b));
    if (invalid) {
      dispatch({ type: "select", id: invalid.id });
      return notify(`Block ${doc.blocks.indexOf(invalid) + 1}: ${blockError(invalid)}`, "error");
    }
    if (printable.length === 0) return notify("Nothing to print yet. Write something on the paper.", "error");
    setPrinting(true);
    try {
      await printerApi.print(documentToRequest(doc, `web/${mode}`));
      notify(`Printed · ${new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`);
    } catch (err) {
      const error = err as PrintError;
      notify(error.message?.replace(/^\[ERROR\]\s*/, "") || "Print failed", "error", error.details);
    } finally {
      setPrinting(false);
      printer.refresh();
    }
  }, [doc, printable.length, printing, mode, notify, printer]);

  const saveHint = mode === "note" ? "Save as template" : null;

  // Keyboard: ⌘/Ctrl+Enter prints anywhere; single letters insert blocks
  // only when focus is not in a field.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === "Enter") {
        e.preventDefault();
        void print();
        return;
      }
      if (e.key === "Escape") {
        if (sheet) return setSheet(null);
        (document.activeElement as HTMLElement | null)?.blur();
        dispatch({ type: "select", id: null });
        return;
      }
      if (isTyping(e.target) || e.metaKey || e.ctrlKey || e.altKey) return;
      if ((e.key === "Delete" || e.key === "Backspace") && selectedId) {
        e.preventDefault();
        dispatch({ type: "remove", id: selectedId });
        return;
      }
      const item = INSERT_ITEMS.find((i) => i.key.toLowerCase() === e.key.toLowerCase());
      if (item) {
        e.preventDefault();
        insert(item.type);
      }
    };
    const onPaste = (e: ClipboardEvent) => {
      const file = Array.from(e.clipboardData?.files ?? []).find((f) => f.type.startsWith("image/"));
      if (!file) return;
      e.preventDefault();
      void insertImage(file);
    };
    window.addEventListener("keydown", onKey);
    window.addEventListener("paste", onPaste);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("paste", onPaste);
    };
  }, [print, insert, insertImage, selectedId, sheet]);

  const loadDocument = (newName: string, newDoc: EditorDocument) => {
    dispatch({ type: "load", doc: newDoc });
    setName(newName);
    setSavedKey(contentKey(newDoc));
  };

  const saveAsTemplate = () => {
    saveDraft(draftKey("template"), { name: "", doc });
    navigate("/template");
  };

  const inspector = selected && (
    <Inspector
      block={selected}
      index={selectedIndex}
      count={doc.blocks.length}
      onUpdate={(patch) => dispatch({ type: "update", id: selected.id, patch })}
      onToggleStyle={(style) => dispatch({ type: "toggleStyle", id: selected.id, style })}
      onDuplicate={() => dispatch({ type: "duplicate", id: selected.id })}
      onRemove={() => dispatch({ type: "remove", id: selected.id })}
      onMove={(delta) => dispatch({ type: "move", id: selected.id, delta })}
    />
  );

  return (
    <div className="stage-glow mx-auto grid max-w-[1600px] grid-cols-1 lg:grid-cols-[260px_minmax(0,1fr)_300px] xl:gap-4">
      {/* Left rail */}
      <aside className="sticky top-[72px] hidden h-[calc(100vh-72px)] flex-col gap-7 overflow-y-auto px-6 py-6 lg:flex">
        {mode === "template" && (
          <TemplatePanel
            name={name}
            dirty={contentKey(doc) !== savedKey}
            doc={doc}
            selectedId={selectedId}
            onRename={setName}
            onSaved={() => setSavedKey(contentKey(doc))}
            onLoad={loadDocument}
            onSelect={(id) => dispatch({ type: "select", id })}
            onMoveTo={(id, index) => dispatch({ type: "moveTo", id, index })}
            onNotice={notify}
          />
        )}
        <div className="flex flex-col gap-1">
          <SectionLabel className="px-3 pb-2">Insert block</SectionLabel>
          <InsertList onInsert={insert} />
        </div>
        <JobSettingsPanel settings={doc.settings} onChange={(patch) => dispatch({ type: "settings", patch })} />
      </aside>

      {/* Paper */}
      <main className="flex min-w-0 flex-col items-center px-2.5 pt-3 pb-40 lg:pt-3">
        <h1 className="sr-only">{mode === "note" ? "Note" : "Template"} editor</h1>
        <PrinterBody tone={printing ? "busy" : status.tone} label={printing ? "DATA" : status.tone === "error" ? status.label.toUpperCase() : "POWER"} />
        <div className="paper-font -mt-2 flex flex-col drop-shadow-[0_18px_24px_var(--paper-shadow)]" onDragEnd={() => setDrag(null)}>
          <PaperRuler />
          {doc.blocks.length === 0 && (
            <PaperRow paperClassName="py-10">
              <p className="text-center font-sans text-sm text-paper-faint">
                Empty paper. Insert a block from the left, or press <kbd className="font-mono">T</kbd> for text.
              </p>
            </PaperRow>
          )}
          {doc.blocks.map((block, index) => (
            <BlockView
              key={block.id}
              block={block}
              index={index}
              selected={block.id === selectedId}
              feedLines={doc.settings.feedLinesAfterPrint}
              textareaRef={textareaRef}
              onSelect={() => dispatch({ type: "select", id: block.id })}
              onUpdate={(patch) => dispatch({ type: "update", id: block.id, patch })}
              onImageError={(message) => notify(message, "error")}
              onImageSize={(height) => setImageDots((prev) => (prev[block.id] === height ? prev : { ...prev, [block.id]: height }))}
              toolbar={
                block.type === ContentType.Text ? (
                  <FloatingToolbar
                    block={block}
                    onToggleStyle={(style) => dispatch({ type: "toggleStyle", id: block.id, style })}
                    onAlign={(alignment) => dispatch({ type: "update", id: block.id, patch: { alignment } })}
                  />
                ) : undefined
              }
              dragProps={{
                onDragStart: (e) => {
                  e.dataTransfer.effectAllowed = "move";
                  setDrag({ id: block.id, over: null });
                },
                onDragOver: (e) => {
                  if (!drag) return;
                  e.preventDefault();
                  if (drag.over !== index) setDrag({ ...drag, over: index });
                },
                onDrop: (e) => {
                  if (!drag) return;
                  e.preventDefault();
                  dispatch({ type: "moveTo", id: drag.id, index });
                  setDrag(null);
                },
                dropTarget: !!drag && drag.over === index && drag.id !== block.id,
              }}
            />
          ))}
          {doc.settings.autoCut && !lastIsCut ? (
            <>
              <PaperRow
                left={<span className="text-right">feed × {doc.settings.feedLinesAfterPrint}</span>}
                paperClassName="py-0"
              >
                <FeedSpace lines={doc.settings.feedLinesAfterPrint} />
              </PaperRow>
              <CutRow right={<span>≈ {lengthMm} mm</span>} />
              <TearEdge />
            </>
          ) : (
            !lastIsCut && (
              <PaperRow right={<span>no cut</span>} paperClassName="h-24 [mask-image:linear-gradient(black,transparent)]">
                <span className="sr-only">Auto-cut is off. The paper stays in the printer.</span>
              </PaperRow>
            )
          )}
        </div>
      </main>

      {/* Right rail */}
      <aside className="sticky top-[72px] hidden h-[calc(100vh-72px)] overflow-y-auto px-6 py-6 lg:block">
        {inspector ?? (
          <div className="flex flex-col gap-3 rounded-xl border border-dashed border-line-strong p-5 text-[13px] text-ink-2">
            <SectionLabel>No block selected</SectionLabel>
            <p>Click a block on the paper to style it. Type directly on the paper to change text.</p>
            <p className="font-mono text-xs">
              T I Q B S L X insert · ⌘V pastes an image · Del removes · Esc deselects
            </p>
          </div>
        )}
      </aside>

      {/* Print dock */}
      <div className="pointer-events-none fixed inset-x-0 bottom-0 z-40 flex items-end justify-between gap-3 bg-gradient-to-t from-bg via-bg/90 to-transparent px-4 pt-8 pb-5 lg:inset-x-auto lg:right-8 lg:bottom-8 lg:bg-none lg:p-0">
        <div className="pointer-events-auto flex items-center gap-2 lg:hidden">
          <button
            type="button"
            onClick={() => setSheet("insert")}
            aria-label="Insert block"
            className="flex size-14 items-center justify-center rounded-full border border-line-strong bg-surface shadow-[0_4px_12px_rgba(60,45,20,.1)]"
          >
            <Plus className="size-[22px]" aria-hidden="true" />
          </button>
          {selected && (
            <button
              type="button"
              onClick={() => setSheet("inspect")}
              aria-label="Edit selected block"
              className="flex size-14 items-center justify-center rounded-full border border-line-strong bg-surface shadow-[0_4px_12px_rgba(60,45,20,.1)]"
            >
              <Pencil className="size-5" aria-hidden="true" />
            </button>
          )}
        </div>
        <div className="pointer-events-auto flex items-center gap-5">
          <div className="hidden flex-col items-end gap-0.5 text-[13px] text-ink-2 sm:flex">
            {saveHint && (
              <button type="button" onClick={saveAsTemplate} className="text-accent-text underline-offset-2 hover:underline">
                {saveHint}
              </button>
            )}
            <span className="font-mono text-[11px]">
              ≈ {lengthMm} mm · ⌘↵ to print{doc.settings.autoCut ? " · auto-cut" : ""}
            </span>
          </div>
          <button
            type="button"
            onClick={() => void print()}
            disabled={printing}
            aria-busy={printing}
            className={cn(
              "flex h-14 items-center gap-3 rounded-full px-8 text-[17px] font-semibold transition-colors lg:h-[60px] lg:px-9",
              printing
                ? "border border-line-strong bg-well text-ink"
                : "bg-accent text-on-accent shadow-[0_10px_24px_color-mix(in_srgb,var(--accent)_30%,transparent)] hover:brightness-110",
            )}
          >
            {printing ? <span className="blink size-2.5 rounded-full bg-warn" aria-hidden="true" /> : <Printer className="size-5" aria-hidden="true" />}
            {printing ? "Printing…" : "Print"}
          </button>
        </div>
      </div>

      {/* Toast */}
      {notice && (
        <div
          role={notice.tone === "error" ? "alert" : "status"}
          className={cn(
            "fixed bottom-28 left-1/2 z-50 flex max-w-[min(480px,calc(100vw-32px))] -translate-x-1/2 lg:bottom-10 items-start gap-3 rounded-xl px-4 py-3 text-sm shadow-[0_12px_30px_rgba(0,0,0,.18)]",
            notice.tone === "error" ? "bg-danger text-white" : "bg-ink text-on-ink",
          )}
        >
          <div className="flex flex-col gap-0.5">
            <span className="font-medium">{notice.message}</span>
            {notice.detail && <span className="opacity-85">{notice.detail}</span>}
          </div>
          <button type="button" onClick={() => setNotice(null)} aria-label="Dismiss" className="-mr-1 flex size-6 shrink-0 items-center justify-center rounded opacity-80 hover:opacity-100">
            <X className="size-4" aria-hidden="true" />
          </button>
        </div>
      )}

      {/* Phone sheets */}
      {sheet && (
        <Sheet title={sheet === "insert" ? "Insert block" : "Edit block"} onClose={() => setSheet(null)}>
          {sheet === "insert" ? (
            <>
              <InsertList onInsert={insert} />
              <div className="mt-4 border-t border-line pt-4">
                <JobSettingsPanel settings={doc.settings} onChange={(patch) => dispatch({ type: "settings", patch })} />
              </div>
            </>
          ) : (
            inspector
          )}
        </Sheet>
      )}
    </div>
  );
}

function Sheet({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return (
    <div className="fixed inset-0 z-50 lg:hidden">
      <button type="button" aria-label="Close" onClick={onClose} className="absolute inset-0 bg-ink/30" />
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
