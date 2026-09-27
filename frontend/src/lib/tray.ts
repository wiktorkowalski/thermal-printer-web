import type { PrintRequest } from "@/types/printer";

// Recent prints, kept in this browser only. Entries whose request is too big
// for localStorage (large images) keep their title but lose the reprint.
const KEY = "thermal-printer-tray";
const MAX_ENTRIES = 20;
const MAX_REQUEST_CHARS = 1_500_000;
export const TRAY_EVENT = "thermal-printer-tray-change";

export interface TrayEntry {
  id: string;
  printedAt: string;
  title: string;
  mode: string;
  request: PrintRequest | null;
}

export function listTray(): TrayEntry[] {
  try {
    return JSON.parse(localStorage.getItem(KEY) ?? "[]") as TrayEntry[];
  } catch {
    return [];
  }
}

function writeTray(entries: TrayEntry[]) {
  // Drop the oldest entries until the browser accepts the write.
  let next = entries;
  while (next.length > 0) {
    try {
      localStorage.setItem(KEY, JSON.stringify(next));
      break;
    } catch {
      next = next.slice(0, -1);
    }
  }
  if (next.length === 0) {
    try {
      localStorage.removeItem(KEY);
    } catch {
      // Storage unavailable: the tray just stays empty.
    }
  }
  window.dispatchEvent(new Event(TRAY_EVENT));
}

export function addToTray(title: string, mode: string, request: PrintRequest) {
  const keep = JSON.stringify(request).length <= MAX_REQUEST_CHARS;
  const entry: TrayEntry = {
    id: crypto.randomUUID(),
    printedAt: new Date().toISOString(),
    title: title.trim() || "Untitled",
    mode,
    request: keep ? request : null,
  };
  writeTray([entry, ...listTray()].slice(0, MAX_ENTRIES));
}

export function removeFromTray(id: string) {
  writeTray(listTray().filter((e) => e.id !== id));
}

export function clearTray() {
  writeTray([]);
}
