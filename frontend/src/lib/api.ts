import axios from "axios";
import type { CustomPrintRequest, SimplePrintRequest } from "../types/printer";

// API base URL - will be proxied by Vite dev server
const API_BASE_URL = "/api";

const api = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    "Content-Type": "application/json",
  },
});

export const printerApi = {
  /**
   * Send a simple print request (name + message)
   */
  async printSimple(request: SimplePrintRequest): Promise<void> {
    await api.post("/printer", request);
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

    await api.post("/printer/image", formData, {
      headers: {
        "Content-Type": "multipart/form-data",
      },
    });
  },

  /**
   * Send a custom template print request
   */
  async printCustom(request: CustomPrintRequest): Promise<void> {
    await api.post("/printer/custom", request);
  },
};

export default api;
