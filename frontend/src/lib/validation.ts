export interface ValidationResult {
  isValid: boolean;
  error?: string;
}

// Image validation
export function validateImage(file: File | null): ValidationResult {
  if (!file) {
    return { isValid: true }; // Image is optional
  }

  const maxSize = 5 * 1024 * 1024; // 5MB
  const allowedTypes = ['image/jpeg', 'image/jpg', 'image/png', 'image/gif', 'image/webp'];

  if (!allowedTypes.includes(file.type)) {
    return {
      isValid: false,
      error: `[ERROR] Invalid file type. Allowed: ${allowedTypes.map(t => t.split('/')[1].toUpperCase()).join(', ')}`
    };
  }

  if (file.size > maxSize) {
    return {
      isValid: false,
      error: `[ERROR] File too large. Max size: ${maxSize / 1024 / 1024}MB`
    };
  }

  return { isValid: true };
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
    'CODE39': { pattern: /^[A-Z0-9\-\.\ \$\/\+\%]+$/, message: 'CODE39 allows A-Z, 0-9, and special chars: -. $/+%' },
    'CODE128': { pattern: /.+/, message: 'CODE128 accepts any ASCII characters' },
    'ITF': { pattern: /^\d+$/, message: 'ITF requires numeric digits only' },
    'CODABAR': { pattern: /^[A-D][0-9\-\$\:\/\.\+]+[A-D]$/, message: 'CODABAR must start/end with A-D' },
  };

  const rule = rules[type];
  if (rule && !rule.pattern.test(data)) {
    return {
      isValid: false,
      error: `[ERROR] ${rule.message}`
    };
  }

  return { isValid: true };
}

// QR Code validation
export function validateQRCode(content: string): ValidationResult {
  if (!content || content.trim().length === 0) {
    return {
      isValid: false,
      error: '[ERROR] QR code content is required'
    };
  }

  // QR codes have size limits based on version and error correction
  // We'll use a reasonable limit for common use cases
  const maxLength = 2953; // Limit for QR Version 40 with Low EC

  if (content.length > maxLength) {
    return {
      isValid: false,
      error: `[ERROR] QR content too long. Max ${maxLength} characters`
    };
  }

  return { isValid: true };
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

  if (length < 1 || length > 48) {
    return {
      isValid: false,
      error: '[ERROR] Separator length must be between 1 and 48'
    };
  }

  return { isValid: true };
}
