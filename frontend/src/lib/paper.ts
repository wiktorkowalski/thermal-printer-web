import type { PrintContent, PrintStyle } from "@/types/printer";
import { getMaxChars, CHARS_PER_LINE } from "@/lib/printer-constants";
import { QR_MAX_MODULES, QR_MIN_MODULES, QR_MODULES_PER_BYTE } from "@/lib/printer-limits";
import { qrDataBytes } from "@/lib/validation";

// Vretti V330M geometry. The head is 576 dots wide (72 mm printable on 80 mm
// paper) at 8 dots/mm. Font A is 12x24 dots, so one `ch` on screen = 12 dots.
export const HEAD_DOTS = 576;
export const DOTS_PER_MM = 8;
export const DOTS_PER_CH = 12;
export const PAPER_WIDTH_CH = (CHARS_PER_LINE.normal * 80) / 72;
export const MARGIN_CH = (PAPER_WIDTH_CH - CHARS_PER_LINE.normal) / 2;

// Line pitch measured on paper (2026-09-27): 11 lines = 40 mm, so 29 dots.
// Double height adds a second 24-dot cell.
export const LINE_DOTS = 29;
const DOUBLE_HEIGHT_LINE_DOTS = LINE_DOTS + 24;

// ESCPOS_NET Size2DCode and BarWidth enum values = module width in dots.
export const QR_MODULE_DOTS = { Normal: 4, Large: 5, ExtraLarge: 6 } as const;
export const BAR_MODULE_DOTS = { Thin: 3, Default: 4, Thick: 5 } as const;
export const DEFAULT_BARCODE_HEIGHT_DOTS = 162;

// The cutter sits past the head, so every cut strip starts with blank paper.
// Measured 2026-09-27: 17 mm from the paper edge to a first "=" line, minus ~1.5 mm to its glyph.
export const CUTTER_OFFSET_DOTS = 124;

// PrinterService sends ESC a 1 (center) for blocks without an alignment.
export const DEFAULT_ALIGNMENT = "Center" as const;

export function dotsToCh(dots: number): string {
  return `${dots / DOTS_PER_CH}ch`;
}

export interface TextMetrics {
  maxChars: number;
  scaleX: number;
  scaleY: number;
  lineDots: number;
}

export function textMetrics(style: PrintStyle[] = []): TextMetrics {
  const doubleHeight = style.includes("DoubleHeight");
  const fontB = style.includes("FontB");
  const maxChars = getMaxChars(style);
  return {
    maxChars,
    scaleX: CHARS_PER_LINE.normal / maxChars,
    // Font B cells are 9x17 dots against Font A's 12x24.
    scaleY: (fontB ? 17 / 24 : 1) * (doubleHeight ? 2 : 1),
    lineDots: doubleHeight ? DOUBLE_HEIGHT_LINE_DOTS : LINE_DOTS,
  };
}

/** Printer hard-wraps mid-word at maxChars; an empty line still feeds one line. */
export function countPrintedLines(text: string, maxChars: number): number {
  return text
    .split("\n")
    .reduce((sum, line) => sum + Math.max(1, Math.ceil(line.length / maxChars)), 0);
}

export function longestLine(text: string): number {
  return text.split("\n").reduce((max, line) => Math.max(max, line.length), 0);
}

export interface JobOptions {
  autoCut: boolean;
  feedLinesAfterPrint: number;
}

// Same bound as backend/Services/Printing/PaperLength.cs QRCodeDots: the printer picks
// the QR version, so this is the largest side the data can need.
function qrModules(content: string): number {
  return Math.min(QR_MAX_MODULES, Math.ceil(Math.sqrt(QR_MIN_MODULES * QR_MIN_MODULES + qrDataBytes(content) * QR_MODULES_PER_BYTE)));
}

/**
 * Rough paper length for the whole job, in dots. Counts like the backend (PaperLength.cs)
 * except the cut: the feed and the cutter offset count as drawn on screen, so this is
 * about 26 mm above the backend for one auto-cut.
 */
export function estimatePaperDots(content: PrintContent[], options: JobOptions, imageDots = 0): number {
  let dots = imageDots;
  content.forEach((block) => {
    switch (block.type) {
      case "Text": {
        const m = textMetrics(block.style);
        dots += countPrintedLines(block.content ?? "", m.maxChars) * m.lineDots;
        break;
      }
      case "Separator": {
        const m = textMetrics(block.style);
        dots += Math.max(1, Math.ceil((block.separatorLength ?? 32) / m.maxChars)) * m.lineDots;
        break;
      }
      case "LineFeed":
        dots += (block.lines ?? 1) * LINE_DOTS;
        break;
      case "QRCode":
        dots += qrModules(block.content ?? "") * QR_MODULE_DOTS[block.qrCodeOptions?.size ?? "Normal"] + LINE_DOTS;
        break;
      case "Barcode": {
        const captionLines = block.barcodeOptions?.labelPosition === "Both" ? 2 : 1;
        dots += (block.barcodeOptions?.heightInDots ?? DEFAULT_BARCODE_HEIGHT_DOTS) + captionLines * LINE_DOTS;
        break;
      }
      case "Cut":
        dots += options.feedLinesAfterPrint * LINE_DOTS;
        break;
    }
  });
  const hasCut = content.some((b) => b.type === "Cut");
  if (options.autoCut && !hasCut) dots += options.feedLinesAfterPrint * LINE_DOTS;
  if (options.autoCut || hasCut) dots += CUTTER_OFFSET_DOTS;
  return dots;
}

/** The "≈ N mm" hint. */
export function dotsToMm(dots: number): number {
  return Math.round(dots / DOTS_PER_MM);
}
