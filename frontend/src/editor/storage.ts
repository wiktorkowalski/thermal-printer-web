import type { PrintRequest } from "@/types/printer";
import { DEFAULT_SETTINGS, fromPrintContent, toPrintContent, toPrintOptions, type EditorDocument } from "./document";

// Same key and shape as the old Template Builder, so saved templates survive.
const TEMPLATES_KEY = "thermal-printer-templates";

export interface SavedTemplate {
  id: string;
  name: string;
  content: PrintRequest;
  createdAt: string;
  updatedAt: string;
}

function read<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback;
  }
}

/** Returns false when the browser refuses the write (quota, private mode). */
function write(key: string, value: unknown): boolean {
  try {
    localStorage.setItem(key, JSON.stringify(value));
    return true;
  } catch {
    return false;
  }
}

export function listTemplates(): SavedTemplate[] {
  return read<SavedTemplate[]>(TEMPLATES_KEY, []).sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
}

export function documentToRequest(doc: EditorDocument, source: string): PrintRequest {
  return { content: toPrintContent(doc.blocks), options: toPrintOptions(doc.settings), source };
}

export function requestToDocument(request: PrintRequest): EditorDocument {
  return {
    blocks: fromPrintContent(request.content ?? []),
    settings: {
      // An imported file can hold any JSON value here; the code page list renders it.
      codePage: typeof request.options?.codePage === "string" ? request.options.codePage : DEFAULT_SETTINGS.codePage,
      autoCut: request.options?.autoCut ?? DEFAULT_SETTINGS.autoCut,
      feedLinesAfterPrint: request.options?.feedLinesAfterPrint ?? DEFAULT_SETTINGS.feedLinesAfterPrint,
    },
  };
}

export function saveTemplate(name: string, doc: EditorDocument): boolean {
  const templates = read<SavedTemplate[]>(TEMPLATES_KEY, []);
  const now = new Date().toISOString();
  const content = documentToRequest(doc, "web/template");
  const existing = templates.find((t) => t.name === name);
  const next = existing
    ? templates.map((t) => (t.name === name ? { ...t, content, updatedAt: now } : t))
    : [...templates, { id: crypto.randomUUID(), name, content, createdAt: now, updatedAt: now }];
  return write(TEMPLATES_KEY, next);
}

export function deleteTemplate(id: string): boolean {
  return write(TEMPLATES_KEY, read<SavedTemplate[]>(TEMPLATES_KEY, []).filter((t) => t.id !== id));
}

// Drafts keep work across reloads. Blocks carry screen-only fields too.
export interface Draft {
  name?: string;
  doc: EditorDocument;
}

export function loadDraft(key: string): Draft | null {
  return read<Draft | null>(key, null);
}

export function saveDraft(key: string, draft: Draft): boolean {
  return write(key, draft);
}
