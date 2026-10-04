import axios, { AxiosError } from "axios";
import type {
  PrintRequest,
  PrintContent,
  PrintOptions,
  PrintResponse,
  PrintJobList,
  PrintJobDetail,
  PrintJobStats,
  PrintJobSearchResult,
  PapercutLedger,
} from "../types/printer";
import { BUSY_RETRY_AFTER_SECONDS } from "./printer-limits";

const API_BASE_URL = "/api";

const api = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    "Content-Type": "application/json",
  },
  timeout: 30000,
});

export interface PrintError {
  message: string;
  type: "network" | "printer" | "validation" | "busy" | "journal" | "journal-off" | "timeout" | "unknown";
  canRetry: boolean;
  details?: string;
  /** Set when the backend names the block: its index in the sent content (from 0) and the reason alone. */
  block?: { index: number; reason: string };
  /** How long the backend asks to wait before the next try. */
  retryAfterMs?: number;
}

// Used when a busy answer has no readable Retry-After header.
const DEFAULT_BUSY_WAIT_MS = BUSY_RETRY_AFTER_SECONDS * 1000;
const MAX_BUSY_WAIT_MS = 30000;

function retryAfterMs(header: unknown): number {
  const seconds = Number(header);
  return Number.isFinite(seconds) && seconds > 0 ? Math.min(seconds * 1000, MAX_BUSY_WAIT_MS) : DEFAULT_BUSY_WAIT_MS;
}

/** Splits "Block 2 (QRCode): reason" and "Block 2: reason". */
function parseBlock(reason: string | null | undefined): PrintError["block"] {
  const match = reason?.match(/^Block (\d+)(?: \([^)]*\))?: (.+)$/s);
  return match ? { index: Number(match[1]), reason: match[2] } : undefined;
}

function parseError(error: unknown): PrintError {
  if (axios.isAxiosError(error)) {
    const axiosError = error as AxiosError<Partial<PrintResponse>>;

    if (!axiosError.response) {
      if (axiosError.code === "ECONNABORTED" || axiosError.message.includes("timeout")) {
        return {
          message: "[ERROR] Request timeout",
          type: "timeout",
          canRetry: true,
          details: "The printer took too long to respond. Check printer connection.",
        };
      }
      return {
        message: "[ERROR] Network error",
        type: "network",
        canRetry: true,
        details: "Cannot reach printer server. Check your connection.",
      };
    }

    const status = axiosError.response.status;
    const data = axiosError.response.data;

    // The payload is at fault: the same job fails again, so no retry.
    if (status === 400 || data?.type === "validation") {
      return {
        message: "[ERROR] Print rejected",
        type: "validation",
        canRetry: false,
        details: data?.error || "Invalid print request data",
        block: parseBlock(data?.error),
      };
    }

    if (status === 413) {
      return {
        message: "[ERROR] Print too large",
        type: "validation",
        canRetry: false,
        details: "The job is over the size limit of the server. Remove an image or use a smaller one.",
      };
    }

    // The print journal is off, or cannot be read at the moment: the tray and the reprint do not work. Not a printer fault.
    if (data?.type === "journal-off" || data?.type === "journal") {
      return {
        message: "[ERROR] No print history",
        type: data.type,
        canRetry: false,
        details: data.type === "journal-off" ? "The server keeps no print history." : "The server cannot read its print history at the moment. Try again in a moment.",
      };
    }

    // The image decode queue is full, or another reprint runs. Nothing is wrong with the printer or the job.
    if (data?.type === "busy") {
      return {
        message: "[ERROR] Server busy",
        type: "busy",
        canRetry: true,
        details: "Other jobs are in the queue. Print again in a few seconds.",
        retryAfterMs: retryAfterMs(axiosError.response.headers["retry-after"]),
      };
    }

    if (status === 503) {
      return {
        message: "[ERROR] Printer unavailable",
        type: "printer",
        canRetry: true,
        details: data?.error || "Printer is offline or encountered an error",
      };
    }

    if (status >= 500) {
      return {
        message: "[ERROR] Server error",
        type: "printer",
        canRetry: true,
        details: data?.error || "Server error occurred",
      };
    }

    return {
      message: `[ERROR] HTTP ${status}`,
      type: "unknown",
      canRetry: status >= 500,
      details: data?.error || "An unknown error occurred",
    };
  }

  return {
    message: "[ERROR] Unknown error",
    type: "unknown",
    canRetry: false,
    details: error instanceof Error ? error.message : "An unexpected error occurred",
  };
}

async function withRetry<T>(fn: () => Promise<T>, maxRetries: number = 2): Promise<T> {
  let lastError: PrintError | null = null;

  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    try {
      return await fn();
    } catch (error) {
      lastError = parseError(error);
      if (!lastError.canRetry || attempt === maxRetries) {
        throw lastError;
      }
      const delay = lastError.retryAfterMs ?? Math.min(1000 * Math.pow(2, attempt), 5000);
      await new Promise((resolve) => setTimeout(resolve, delay));
    }
  }

  throw lastError;
}

/** Convert File to base64 string */
export async function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string;
      // Remove data:image/...;base64, prefix
      const base64 = result.split(",")[1];
      resolve(base64);
    };
    reader.onerror = reject;
    reader.readAsDataURL(file);
  });
}

export interface PrinterStatus {
  reachable: boolean;
  online: boolean;
  coverOpen: boolean;
  paperOut: boolean;
  paperLow: boolean;
  ready: boolean;
  notReadyReason: string | null;
}

/** One read of the statistics, the search or the ledger. The server runs few of these at a time: a busy answer gets one more try. */
async function journalQuery<T>(url: string, params: Record<string, string | number | undefined>, isValid: (data: T | undefined) => boolean): Promise<T> {
  return withRetry(async () => {
    const response = await api.get<T>(url, { params, timeout: 10000 });
    if (!isValid(response.data)) throw new Error("Unexpected journal response");
    return response.data;
  }, 1);
}

export const printerApi = {
  /**
   * Printer readiness. The backend answers 503 with the same body when the
   * printer is unreachable, so every status code is a valid answer here.
   */
  async getStatus(): Promise<PrinterStatus> {
    const response = await api.get<PrinterStatus>("/printer/status", {
      validateStatus: () => true,
      timeout: 10000,
    });
    if (!response.data || typeof response.data.ready !== "boolean") {
      throw parseError(new Error(`Unexpected status response (HTTP ${response.status})`));
    }
    return response.data;
  },

  /** Sound the buzzer without printing. */
  async beep(count: number = 1, duration: number = 1): Promise<void> {
    try {
      await api.post("/printer/beep", null, { params: { count, duration } });
    } catch (error) {
      throw parseError(error);
    }
  },

  /**
   * Single print endpoint - all JSON
   */
  async print(request: PrintRequest): Promise<void> {
    await withRetry(() => api.post("/printer", request));
  },

  /** One page of the print journal, newest first: the jobs that printed. `before` is `next` of the page before. */
  async listJobs(before?: string | null): Promise<PrintJobList> {
    try {
      const response = await api.get<PrintJobList>("/printer/jobs", {
        params: { printed: true, before: before ?? undefined },
        timeout: 10000,
      });
      if (!Array.isArray(response.data?.jobs)) throw new Error("Unexpected job list response");
      return response.data;
    } catch (error) {
      throw parseError(error);
    }
  },

  /** One job with its blocks, for the thumbnail. */
  async getJob(id: string): Promise<PrintJobDetail> {
    try {
      const response = await api.get<PrintJobDetail>(`/printer/jobs/${encodeURIComponent(id)}`, { timeout: 10000 });
      return response.data;
    } catch (error) {
      throw parseError(error);
    }
  },

  /** Counts and paper for the last `days` UTC days. A busy answer is tried once more. */
  async getStats(days: number): Promise<PrintJobStats> {
    return journalQuery<PrintJobStats>("/printer/jobs/stats", { days }, (data) => Array.isArray(data?.byDay));
  },

  /** The jobs whose printed text holds `query`, newest first. `before` is `next` of the answer before. */
  async searchJobs(query: string, before?: string | null): Promise<PrintJobSearchResult> {
    return journalQuery<PrintJobSearchResult>("/printer/jobs/search", { q: query, before: before ?? undefined }, (data) => Array.isArray(data?.hits));
  },

  /** The papercut strips, grouped by subject. */
  async getPapercuts(): Promise<PapercutLedger> {
    return journalQuery<PapercutLedger>("/printer/jobs/papercuts", {}, (data) => Array.isArray(data?.papercuts));
  },

  /** Prints a stored job again. The server builds it from the journal; one call is one print. */
  async reprint(id: string, source: string): Promise<void> {
    await withRetry(() => api.post(`/printer/jobs/${encodeURIComponent(id)}/reprint`, null, { params: { source } }));
  },

  /**
   * Simple print (name + message)
   */
  async printSimple(request: { name: string; message: string }): Promise<void> {
    return this.print(request);
  },

  /**
   * Print with optional image (converts to base64)
   */
  async printImage(name: string, message: string, imageFile?: File): Promise<void> {
    const imageBase64 = imageFile ? await fileToBase64(imageFile) : undefined;
    return this.print({ name, message, imageBase64 });
  },

  /**
   * Print custom template
   */
  async printCustom(request: { content: PrintContent[]; options?: PrintOptions }): Promise<void> {
    return this.print(request);
  },
};

export default api;
