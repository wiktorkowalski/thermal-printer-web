import axios, { AxiosError } from "axios";
import type { CustomPrintRequest, SimplePrintRequest } from "../types/printer";

// API base URL - will be proxied by Vite dev server
const API_BASE_URL = "/api";

const api = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    "Content-Type": "application/json",
  },
  timeout: 30000, // 30 second timeout
});

export interface PrintError {
  message: string;
  type: 'network' | 'printer' | 'validation' | 'timeout' | 'unknown';
  canRetry: boolean;
  details?: string;
}

function parseError(error: unknown): PrintError {
  if (axios.isAxiosError(error)) {
    const axiosError = error as AxiosError;

    // Network errors (no response from server)
    if (!axiosError.response) {
      if (axiosError.code === 'ECONNABORTED' || axiosError.message.includes('timeout')) {
        return {
          message: '[ERROR] Request timeout',
          type: 'timeout',
          canRetry: true,
          details: 'The printer took too long to respond. Check printer connection.'
        };
      }
      return {
        message: '[ERROR] Network error',
        type: 'network',
        canRetry: true,
        details: 'Cannot reach printer server. Check your connection.'
      };
    }

    // Server responded with error
    const status = axiosError.response.status;
    const data = axiosError.response.data as any;

    if (status === 400) {
      return {
        message: '[ERROR] Validation error',
        type: 'validation',
        canRetry: false,
        details: data?.message || 'Invalid print request data'
      };
    }

    if (status === 500) {
      return {
        message: '[ERROR] Printer error',
        type: 'printer',
        canRetry: true,
        details: data?.message || 'Printer is offline or encountered an error'
      };
    }

    if (status === 503) {
      return {
        message: '[ERROR] Printer unavailable',
        type: 'printer',
        canRetry: true,
        details: 'Printer is currently unavailable. It may be offline or busy.'
      };
    }

    return {
      message: `[ERROR] HTTP ${status}`,
      type: 'unknown',
      canRetry: status >= 500,
      details: data?.message || 'An unknown error occurred'
    };
  }

  // Non-Axios errors
  return {
    message: '[ERROR] Unknown error',
    type: 'unknown',
    canRetry: false,
    details: error instanceof Error ? error.message : 'An unexpected error occurred'
  };
}

async function withRetry<T>(
  fn: () => Promise<T>,
  maxRetries: number = 2
): Promise<T> {
  let lastError: PrintError | null = null;

  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    try {
      return await fn();
    } catch (error) {
      lastError = parseError(error);

      // Don't retry if error is not retryable
      if (!lastError.canRetry) {
        throw lastError;
      }

      // Don't retry on last attempt
      if (attempt === maxRetries) {
        throw lastError;
      }

      // Wait before retry (exponential backoff)
      const delay = Math.min(1000 * Math.pow(2, attempt), 5000);
      await new Promise(resolve => setTimeout(resolve, delay));
    }
  }

  throw lastError;
}

export const printerApi = {
  /**
   * Send a simple print request (name + message)
   */
  async printSimple(request: SimplePrintRequest): Promise<void> {
    try {
      await withRetry(() => api.post("/printer", request));
    } catch (error) {
      throw parseError(error);
    }
  },

  /**
   * Send a print request with an image
   */
  async printImage(
    name: string,
    message: string,
    imageFile?: File
  ): Promise<void> {
    const formData = new FormData();
    formData.append("name", name);
    formData.append("message", message);
    if (imageFile) {
      formData.append("image", imageFile);
    }

    try {
      await withRetry(() =>
        api.post("/printer/image", formData, {
          headers: {
            "Content-Type": "multipart/form-data",
          },
        })
      );
    } catch (error) {
      throw parseError(error);
    }
  },

  /**
   * Send a custom template print request
   */
  async printCustom(request: CustomPrintRequest): Promise<void> {
    try {
      await withRetry(() => api.post("/printer/custom", request));
    } catch (error) {
      throw parseError(error);
    }
  },
};

export default api;
