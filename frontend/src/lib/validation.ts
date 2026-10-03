import {
  BARCODE_MAX_PAYLOAD,
  CODE128_MAX_PAYLOAD,
  EDITOR_IMAGE_MAX_BYTES,
  EDITOR_IMAGE_MAX_MB,
  IMAGE_MAX_BYTES,
  IMAGE_MAX_PIXELS,
  IMAGE_MAX_SIDE_PIXELS,
  QR_MAX_BYTES,
  SEPARATOR_MAX_LENGTH,
  TEXT_MAX_LENGTH,
  TEXT_MAX_LINES,
} from './printer-limits';

export interface ValidationResult {
  isValid: boolean;
  error?: string;
}

const VALID: ValidationResult = { isValid: true };

/** Validators prefix their text with [ERROR]; the UI shows it without. */
export function errorText(result: ValidationResult, fallback = 'Invalid value'): string {
  return (result.error ?? fallback).replace(/^\[ERROR\]\s*/, '');
}

// Image validation
const IMAGE_TYPES = ['image/png', 'image/jpeg'];
export const IMAGE_ACCEPT = IMAGE_TYPES.join(',');

function validateImage(file: File): ValidationResult {
  // The backend decodes PNG and JPEG only and checks the bytes itself.
  if (!IMAGE_TYPES.includes(file.type)) {
    return {
      isValid: false,
      error: '[ERROR] Invalid file type. Allowed: PNG, JPEG'
    };
  }

  if (file.size > EDITOR_IMAGE_MAX_BYTES) {
    return {
      isValid: false,
      error: `[ERROR] File too large. Max size: ${EDITOR_IMAGE_MAX_MB}MB`
    };
  }

  return VALID;
}

function imagePixels(file: File): Promise<{ width: number; height: number } | null> {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    const done = (size: { width: number; height: number } | null) => {
      URL.revokeObjectURL(url);
      resolve(size);
    };
    img.onload = () => done({ width: img.naturalWidth, height: img.naturalHeight });
    img.onerror = () => done(null);
    img.src = url;
  });
}

/** Type, size and pixel limits for a picked, dropped or pasted file. */
export async function validateImageFile(file: File): Promise<ValidationResult> {
  const result = validateImage(file);
  if (!result.isValid) return result;

  // A file the browser cannot read passes: the backend checks the bytes.
  const size = await imagePixels(file);
  if (size && (size.width > IMAGE_MAX_SIDE_PIXELS || size.height > IMAGE_MAX_SIDE_PIXELS || size.width * size.height > IMAGE_MAX_PIXELS)) {
    return {
      isValid: false,
      error: `[ERROR] Image too large: ${size.width}×${size.height} pixels. Max ${IMAGE_MAX_SIDE_PIXELS} per side and ${Math.floor(IMAGE_MAX_PIXELS / 1_000_000)} megapixels`
    };
  }

  return VALID;
}

// base64 of the PNG and JPEG signatures.
const IMAGE_SIGNATURES = ['iVBORw0KGgo', '/9j/'];
const IMAGE_MAX_BASE64_LENGTH = Math.ceil(IMAGE_MAX_BYTES / 3) * 4;

/** Image content already in a document: a draft, a saved template or an imported JSON. */
export function validateImageContent(content: string): ValidationResult {
  // The backend drops a "data:...," or "base64," prefix.
  const start = content.startsWith('data:') || content.startsWith('base64,') ? content.indexOf(',') + 1 : 0;

  if (!IMAGE_SIGNATURES.some((signature) => content.startsWith(signature, start))) {
    return {
      isValid: false,
      error: '[ERROR] Image is not a PNG or JPEG'
    };
  }

  if (content.length - start > IMAGE_MAX_BASE64_LENGTH) {
    return {
      isValid: false,
      error: `[ERROR] Image too large. Max size: ${IMAGE_MAX_BYTES / 1024 / 1024}MB`
    };
  }

  return VALID;
}

// Text validation
export function validateRequired(value: string, fieldName: string): ValidationResult {
  if (!value || value.trim().length === 0) {
    return {
      isValid: false,
      error: `[ERROR] ${fieldName} is required`
    };
  }
  return { isValid: true };
}

// Barcode validation
export function validateBarcode(data: string, type: string): ValidationResult {
  if (!data || data.trim().length === 0) {
    return {
      isValid: false,
      error: '[ERROR] Barcode data is required'
    };
  }

  // Basic validation based on barcode type
  const rules: Record<string, { pattern: RegExp; message: string }> = {
    'UPC_A': { pattern: /^\d{12}$/, message: 'UPC-A requires exactly 12 digits' },
    'UPC_E': { pattern: /^\d{8}$/, message: 'UPC-E requires exactly 8 digits' },
    'EAN13': { pattern: /^\d{13}$/, message: 'EAN13 requires exactly 13 digits' },
    'EAN8': { pattern: /^\d{8}$/, message: 'EAN8 requires exactly 8 digits' },
    'CODE39': { pattern: /^[A-Z0-9\-. $/+%]+$/, message: 'CODE39 allows A-Z, 0-9, and special chars: -. $/+%' },
    'CODE128': { pattern: /^[\x20-\x7E]+$/, message: 'CODE128 accepts printable ASCII only' },
    'ITF': { pattern: /^\d+$/, message: 'ITF requires numeric digits only' },
    'CODABAR': { pattern: /^[A-D][0-9\-$:/.+]+[A-D]$/, message: 'CODABAR must start/end with A-D' },
    'GS1_128': { pattern: /^[\x20-\x7E]+$/, message: 'GS1-128 accepts printable ASCII only' },
    'GS1_DATABAR_OMNIDIRECTIONAL': { pattern: /^\d{13}$/, message: 'GS1 DataBar requires exactly 13 digits (GTIN without check digit)' },
  };

  const rule = rules[type];
  if (rule && !rule.pattern.test(data)) {
    return {
      isValid: false,
      error: `[ERROR] ${rule.message}`
    };
  }

  if (data.length > BARCODE_MAX_PAYLOAD) {
    return {
      isValid: false,
      error: `[ERROR] A barcode holds at most ${BARCODE_MAX_PAYLOAD} characters`
    };
  }

  // The printer sends each '{' twice, so it takes two places.
  if (type === 'CODE128' && data.length + (data.split('{').length - 1) > CODE128_MAX_PAYLOAD) {
    return {
      isValid: false,
      error: `[ERROR] CODE128 holds at most ${CODE128_MAX_PAYLOAD} characters ("{" counts as 2)`
    };
  }

  return VALID;
}

const utf8 = new TextEncoder();

/** QR data size as the backend counts it: UTF-8 bytes, CRLF as one LF. "ą" is 2 bytes, an emoji is 4. */
export function qrDataBytes(content: string): number {
  return utf8.encode(String(content).replaceAll('\r\n', '\n')).length;
}

/** Index of the first control character the backend rejects in QR content, or -1. */
function qrControlCharIndex(content: string): number {
  for (let i = 0; i < content.length; i++) {
    const code = content.charCodeAt(i);
    // .NET char.IsControl: U+0000 to U+001F and U+007F to U+009F.
    const isControl = code <= 0x1f || (code >= 0x7f && code <= 0x9f);
    if (!isControl || code === 0x0a) continue;
    if (code === 0x0d && content.charCodeAt(i + 1) === 0x0a) continue;
    return i;
  }
  return -1;
}

// QR Code validation
export function validateQRCode(content: string, model: keyof typeof QR_MAX_BYTES = 'Model2'): ValidationResult {
  if (!content || content.trim().length === 0) {
    return {
      isValid: false,
      error: '[ERROR] QR code content is required'
    };
  }

  const control = qrControlCharIndex(content);
  if (control !== -1) {
    const code = content.charCodeAt(control);
    const name = code === 0x09 ? 'a tab' : `the control character U+${code.toString(16).toUpperCase().padStart(4, '0')}`;
    return {
      isValid: false,
      error: `[ERROR] QR content holds ${name} at position ${control + 1}. Only line breaks are allowed`
    };
  }

  const bytes = qrDataBytes(content);
  const maxBytes = QR_MAX_BYTES[model] ?? QR_MAX_BYTES.Model2;

  if (bytes > maxBytes) {
    return {
      isValid: false,
      error: `[ERROR] QR content too long: ${bytes} bytes. A ${model} code holds at most ${maxBytes}`
    };
  }

  return VALID;
}

/** Lines as the backend counts them: CRLF, LF, CR, FF, NEL, LS and PS each end a line. */
function countSourceLines(text: string): number {
  let lines = 1;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    // CRLF counts once, at its LF.
    if (code === 0x0d && text.charCodeAt(i + 1) === 0x0a) continue;
    if (code === 0x0a || code === 0x0c || code === 0x0d || code === 0x85 || code === 0x2028 || code === 0x2029) lines++;
  }
  return lines;
}

// Text block validation
export function validateText(content: string): ValidationResult {
  if (content.length > TEXT_MAX_LENGTH) {
    return {
      isValid: false,
      error: `[ERROR] Text too long: ${content.length} characters. Max ${TEXT_MAX_LENGTH} per block`
    };
  }

  const lines = countSourceLines(content);
  if (lines > TEXT_MAX_LINES) {
    return {
      isValid: false,
      error: `[ERROR] Too many lines: ${lines}. Max ${TEXT_MAX_LINES} per block`
    };
  }

  return VALID;
}

// Template name validation
export function validateTemplateName(name: string): ValidationResult {
  if (!name || name.trim().length === 0) {
    return {
      isValid: false,
      error: '[ERROR] Template name is required'
    };
  }

  if (name.length > 50) {
    return {
      isValid: false,
      error: '[ERROR] Template name too long (max 50 chars)'
    };
  }

  // Check for invalid characters
  if (!/^[a-zA-Z0-9\s\-_]+$/.test(name)) {
    return {
      isValid: false,
      error: '[ERROR] Template name can only contain letters, numbers, spaces, hyphens, and underscores'
    };
  }

  return { isValid: true };
}

// Separator validation
export function validateSeparator(char: string, length: number): ValidationResult {
  if (!char || char.length !== 1) {
    return {
      isValid: false,
      error: '[ERROR] Separator must be a single character'
    };
  }

  if (!(length >= 1 && length <= SEPARATOR_MAX_LENGTH)) {
    return {
      isValid: false,
      error: `[ERROR] Separator length must be between 1 and ${SEPARATOR_MAX_LENGTH}`
    };
  }

  return { isValid: true };
}
