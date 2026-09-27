import { useSyncExternalStore } from "react";
import { TRAY_EVENT, listTray, type TrayEntry } from "@/lib/tray";

let cachedRaw: string | null = null;
let cached: TrayEntry[] = [];

function snapshot(): TrayEntry[] {
  let raw: string | null = null;
  try {
    raw = localStorage.getItem("thermal-printer-tray");
  } catch {
    raw = null;
  }
  if (raw !== cachedRaw) {
    cachedRaw = raw;
    cached = listTray();
  }
  return cached;
}

function subscribe(onChange: () => void) {
  window.addEventListener(TRAY_EVENT, onChange);
  window.addEventListener("storage", onChange);
  return () => {
    window.removeEventListener(TRAY_EVENT, onChange);
    window.removeEventListener("storage", onChange);
  };
}

export function useTray(): TrayEntry[] {
  return useSyncExternalStore(subscribe, snapshot);
}
