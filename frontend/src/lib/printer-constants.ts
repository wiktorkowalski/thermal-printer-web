// Printer hardware constraints for 80mm thermal paper
// Tested values for this specific printer

export const PAPER_WIDTH_MM = 80;

// Characters per line for different style combinations
export const CHARS_PER_LINE = {
  normal: 48,           // Font A - tested
  fontB: 64,            // Font B (smaller) - tested
  doubleWidth: 24,      // Font A, double width
} as const;

// Default line width for separators, preview, etc.
export const DEFAULT_LINE_WIDTH = CHARS_PER_LINE.normal;

// Characters per line at a width multiplier of 1 to 8 (GS ! n). The printer prints whole
// characters only: Font A gives 48, 24, 16, 12, 9, 8, 6, 6 and Font B 64, 32, 21, 16, 12, 10, 9, 8.
// Tested on paper: Font A at 1, 2, 3, 4 and 8, Font B at 1 and 2.
// Same rule as backend/Services/Printing/PaperLength.cs Columns.
export function columnsPerLine(fontB: boolean, widthMultiplier: number): number {
  return Math.floor((fontB ? CHARS_PER_LINE.fontB : CHARS_PER_LINE.normal) / widthMultiplier);
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
