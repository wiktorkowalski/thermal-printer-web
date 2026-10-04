import { useCallback, useEffect, useRef, useState } from "react";
import type { PrintJobSummary } from "@/types/printer";
import { printerApi, type PrintError } from "@/lib/api";
import { TRAY_EVENT, dropLegacyTray } from "@/lib/tray";

/**
 * loading: the first read runs. ready: `jobs` is the list (it can be empty).
 * off: the server keeps no journal (503 `journal-off`). error: the read failed; `refresh` tries again.
 */
export type TrayStatus = "loading" | "ready" | "off" | "error";

export interface TrayState {
  status: TrayStatus;
  /** Newest first: the jobs that printed, from every device. */
  jobs: PrintJobSummary[];
  hasMore: boolean;
  loadingMore: boolean;
  /** The last read of an older page failed; `loadMore` tries again. */
  moreFailed: boolean;
  refresh: () => void;
  loadMore: () => void;
}

interface Page {
  status: TrayStatus;
  jobs: PrintJobSummary[];
  next: string | null;
}

// The server stores a job after it answers the print call. A read right after a print can miss the row.
const AFTER_PRINT_MS = 800;

export function useTray(): TrayState {
  const [page, setPage] = useState<Page>({ status: "loading", jobs: [], next: null });
  const [loadingMore, setLoadingMore] = useState(false);
  const [moreFailed, setMoreFailed] = useState(false);
  // Each read of the first page starts a new generation; an answer of an older one is dropped.
  const generation = useRef(0);

  const refresh = useCallback(() => {
    const mine = ++generation.current;
    setLoadingMore(false);
    setMoreFailed(false);
    printerApi.listJobs().then(
      (list) => {
        if (mine === generation.current) setPage({ status: "ready", jobs: list.jobs, next: list.next });
      },
      (error: PrintError) => {
        if (mine === generation.current) setPage({ status: error.type === "journal-off" ? "off" : "error", jobs: [], next: null });
      },
    );
  }, []);

  const { next } = page;
  const loadMore = useCallback(() => {
    if (!next || loadingMore) return;
    // A new generation: the answer of a first-page read that still runs must not replace the list under this page.
    const mine = ++generation.current;
    setLoadingMore(true);
    setMoreFailed(false);
    printerApi.listJobs(next).then(
      (list) => {
        if (mine !== generation.current) return;
        setLoadingMore(false);
        // The next page starts below the last id on screen: no row comes twice.
        setPage((current) => ({ status: "ready", jobs: [...current.jobs, ...list.jobs], next: list.next }));
      },
      () => {
        // The rows on screen stay; the button is there for another try.
        if (mine !== generation.current) return;
        setLoadingMore(false);
        setMoreFailed(true);
      },
    );
  }, [next, loadingMore]);

  useEffect(() => {
    dropLegacyTray();
    refresh();
    let timer: number | undefined;
    const onPrint = () => {
      window.clearTimeout(timer);
      timer = window.setTimeout(refresh, AFTER_PRINT_MS);
    };
    window.addEventListener(TRAY_EVENT, onPrint);
    return () => {
      window.clearTimeout(timer);
      window.removeEventListener(TRAY_EVENT, onPrint);
    };
  }, [refresh]);

  return { status: page.status, jobs: page.jobs, hasMore: page.next !== null, loadingMore, moreFailed, refresh, loadMore };
}
