import axios, { AxiosError } from "axios";
import type { PrintRequest, PrintContent, PrintOptions } from "../types/printer";

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
  type: "network" | "printer" | "validation" | "timeout" | "unknown";
  canRetry: boolean;
  details?: string;
}

function parseError(error: unknown): PrintError {
  if (axios.isAxiosError(error)) {
    const axiosError = error as AxiosError<{ error?: string; type?: string }>;

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

    if (status === 400) {
      return {
        message: "[ERROR] Validation error",
        type: "validation",
        canRetry: false,
        details: data?.error || "Invalid print request data",
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
      const delay = Math.min(1000 * Math.pow(2, attempt), 5000);
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

export const printerApi = {
  /**
   * Single print endpoint - all JSON
   */
  async print(request: PrintRequest): Promise<void> {
    await withRetry(() => api.post("/printer", request));
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
