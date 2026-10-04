import type { PrintContent } from "@/types/printer";
import { printerApi } from "./api";

// The tray shows the print journal of the server (GET /api/printer/jobs).
// Nothing of it is kept in this browser.
export const TRAY_EVENT = "thermal-printer-tray-change";

/** Tells the tray that a job went to the printer: the tray reads the list again. */
export function trayChanged() {
  window.dispatchEvent(new Event(TRAY_EVENT));
}

// Before the server tray, the last 20 prints were stored under this key.
// The copies took storage space that the drafts need, and no screen shows them now.
const LEGACY_KEY = "thermal-printer-tray";

export function dropLegacyTray() {
  try {
    localStorage.removeItem(LEGACY_KEY);
  } catch {
    // Storage unavailable: nothing to remove.
  }
}

// The server writes null for a value that is not set; the paper components take undefined.
function withoutNulls(blocks: unknown): PrintContent[] {
  const parsed = JSON.parse(JSON.stringify(blocks ?? []), (_key, item: unknown) => (item === null ? undefined : item)) as unknown;
  return Array.isArray(parsed) ? parsed.filter((block): block is PrintContent => !!block) : [];
}

// A stored job does not change, so one read per job is enough.
const MAX_COPIES = 100;
const copies = new Map<string, Promise<PrintContent[]>>();

/** The blocks of a stored job, for its thumbnail. An image block holds a hash, not the picture. */
export function loadTrayCopy(id: string): Promise<PrintContent[]> {
  let copy = copies.get(id);
  if (!copy) {
    // The oldest entry goes first: a Map keeps the order of insertion.
    if (copies.size >= MAX_COPIES) copies.delete(copies.keys().next().value!);
    copy = printerApi.getJob(id).then((detail) => withoutNulls(detail.blocks));
    // A failed read is tried again the next time the thumbnail shows.
    copy.catch(() => copies.delete(id));
    copies.set(id, copy);
  }
  return copy;
}
