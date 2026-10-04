import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Pencil, Plus } from "lucide-react";
import { ContentType } from "@/types/printer";
import { cn } from "@/lib/utils";
import { fileToBase64 } from "@/lib/api";
import { errorText, validateImageFile } from "@/lib/validation";
import { dotsToMm, estimatePaperDots } from "@/lib/paper";
import {
  contentError,
  createBlock,
  documentError,
  editorReducer,
  emptyDocument,
  isPrintable,
  noteDocument,
  toPrintContent,
  toPrintOptions,
  type EditorDocument,
  type EditorState,
} from "@/editor/document";
import { loadDraft, saveDraft } from "@/editor/storage";
import type { PrinterStatusState } from "@/hooks/use-printer-status";
import { usePrintJob } from "@/hooks/use-print-job";
import { useIsDesktop } from "@/hooks/use-is-desktop";
import { PrintDock, PrinterAlert, Toast, roundButtonClass } from "@/components/print-chrome";
import { Sheet } from "@/components/sheet";
import { isBlocked, printerLight } from "@/lib/printer-light";
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

export default function Editor({ mode, printer }: { mode: EditorMode; printer: PrinterStatusState }) {
  const navigate = useNavigate();
  const [initial] = useState(() => initialState(mode));
  const [state, dispatch] = useReducer(editorReducer, initial);
  const [name, setName] = useState(initial.name);
  const [savedKey, setSavedKey] = useState(() => contentKey(initial.doc));
  const job = usePrintJob(printer);
  const desktop = useIsDesktop();
  const { printing, notify } = job;
  const [sheet, setSheet] = useState<"insert" | "inspect" | null>(null);
  const [drag, setDrag] = useState<{ id: string; over: number | null } | null>(null);
  const [imageDots, setImageDots] = useState<Record<string, number>>({});
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const focusText = useRef(false);

  const { doc, selectedId } = state;
  const selectedIndex = doc.blocks.findIndex((b) => b.id === selectedId);
  const selected = selectedIndex >= 0 ? doc.blocks[selectedIndex] : null;
  const printable = useMemo(() => toPrintContent(doc.blocks), [doc.blocks]);
  const paperDots = estimatePaperDots(
    printable,
    doc.settings,
    doc.blocks.reduce((sum, b) => sum + (b.type === ContentType.Image && b.content ? (imageDots[b.id] ?? 0) : 0), 0),
  );
  const lengthMm = dotsToMm(paperDots);
  const options = useMemo(() => toPrintOptions(doc.settings), [doc.settings]);
  const docError = documentError(printable, options, paperDots);
  const light = printerLight(printer, job.phase);
  const blocked = isBlocked(printer);
  const lastIsCut = doc.blocks.at(-1)?.type === ContentType.Cut;

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
      const check = await validateImageFile(file);
      if (!check.isValid) return notify(errorText(check, "Invalid image"), "error");
      const content = await fileToBase64(file);
      if (selected?.type === ContentType.Image && !selected.content) {
        dispatch({ type: "update", id: selected.id, patch: { content } });
      } else {
        dispatch({ type: "insert", block: { ...createBlock(ContentType.Image), content } });
      }
    },
    [selected, notify],
  );

  const { print: sendJob } = job;
  const print = useCallback(async () => {
    if (printing) return;
    // Selects the block at an index of the sent content and returns its name on the paper.
    const sent = doc.blocks.filter(isPrintable);
    const locate = (index: number) => {
      const block = sent[index];
      if (!block) return undefined;
      dispatch({ type: "select", id: block.id });
      return `Block ${doc.blocks.indexOf(block) + 1}`;
    };
    const invalid = contentError(printable, options, paperDots, locate);
    if (invalid) return notify(invalid, "error");
    if (printable.length === 0) return notify("Nothing to print yet. Write something on the paper.", "error");
    await sendJob({ content: printable, options, source: `web/${mode}` }, locate);
  }, [doc, printable, options, paperDots, printing, mode, notify, sendJob]);

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
      {desktop && (
        <aside className="sticky top-[72px] flex h-[calc(100vh-72px)] flex-col gap-7 overflow-y-auto px-6 py-6">
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
      )}

      {/* Paper */}
      <main className="flex min-w-0 flex-col items-center px-2.5 pt-3 pb-40 lg:pt-3">
        <h1 className="sr-only">{mode === "note" ? "Note" : "Template"} editor</h1>
        <PrinterAlert printer={printer} />
        <PrinterBody tone={light.tone} label={light.label} />
        <div
          className={cn(
            "paper-font -mt-2 flex flex-col drop-shadow-[0_18px_24px_var(--paper-shadow)]",
            job.phase === "printing" && "strip-printing",
            job.phase === "torn" && "strip-torn",
          )}
          onDragEnd={() => setDrag(null)}
        >
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
      {desktop && (
        <aside className="sticky top-[72px] h-[calc(100vh-72px)] overflow-y-auto px-6 pt-6 pb-32">
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
      )}

      <PrintDock
        printing={printing}
        blocked={blocked}
        onPrint={() => void print()}
        meta={
          <>
            {saveHint && (
              <button type="button" onClick={saveAsTemplate} className="text-accent-text underline-offset-2 hover:underline">
                {saveHint}
              </button>
            )}
            {docError ? (
              <span role="alert" className="text-xs text-danger-text">
                {docError}
              </span>
            ) : (
              <span className="font-mono text-[11px]">
                ≈ {lengthMm} mm · ⌘↵ to print{doc.settings.autoCut ? " · auto-cut" : ""}
              </span>
            )}
          </>
        }
        leading={
          <>
            <button type="button" onClick={() => setSheet("insert")} aria-label="Insert block" className={roundButtonClass}>
              <Plus className="size-[22px]" aria-hidden="true" />
            </button>
            {selected && (
              <button type="button" onClick={() => setSheet("inspect")} aria-label="Edit selected block" className={roundButtonClass}>
                <Pencil className="size-5" aria-hidden="true" />
              </button>
            )}
          </>
        }
      />
      <Toast notice={job.notice} onDismiss={job.dismiss} />

      {/* Phone sheets */}
      {sheet && !desktop && (
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
