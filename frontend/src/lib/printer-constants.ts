// Printer hardware constraints for 80mm thermal paper
// Tested values for this specific printer

export const PAPER_WIDTH_MM = 80;

// Characters per line for different style combinations
export const CHARS_PER_LINE = {
  normal: 48,           // Font A - tested
  fontB: 64,            // Font B (smaller) - tested
  doubleWidth: 24,      // Font A, double width
  fontBDoubleWidth: 32, // Font B, double width
} as const;

// Default line width for separators, preview, etc.
export const DEFAULT_LINE_WIDTH = CHARS_PER_LINE.normal;

// Calculate max chars based on active styles
export function getMaxChars(styles: string[] = []): number {
  const hasDoubleWidth = styles.includes('DoubleWidth');
  const hasFontB = styles.includes('FontB');

  if (hasFontB && hasDoubleWidth) return CHARS_PER_LINE.fontBDoubleWidth;
  if (hasDoubleWidth) return CHARS_PER_LINE.doubleWidth;
  if (hasFontB) return CHARS_PER_LINE.fontB;
  return CHARS_PER_LINE.normal;
}

// Get warning level based on text length vs max
export function getCharCountStatus(
  length: number,
  maxChars: number
): 'ok' | 'warning' | 'overflow' {
  if (length <= maxChars) return 'ok';
  if (length <= maxChars * 1.5) return 'warning';
  return 'overflow';
}

// Format char count display
export function formatCharCount(length: number, maxChars: number): string {
  return `${length}/${maxChars}`;
}
