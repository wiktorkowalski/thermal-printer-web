import {
  Alignment,
  BarLabelPosition,
  BarWidth,
  BarcodeType,
  ContentType,
  QRCodeCorrectionLevel,
  QRCodeModel,
  QRCodeSize,
  type PrintContent,
  type PrintOptions,
  type PrintStyle,
} from "@/types/printer";
import { CHARS_PER_LINE } from "@/lib/printer-constants";
import {
  BARCODE_MAX_HEIGHT_DOTS,
  BARCODE_MIN_HEIGHT_DOTS,
  IMAGE_MIN_PRINT_SIZE,
  LINE_FEED_MAX_LINES,
  MAX_BLOCKS,
  MAX_FEED_BEFORE_CUT,
  MAX_IMAGE_BLOCKS,
  MAX_LINE_SPACING,
  MAX_PAPER_DOTS,
  MAX_REQUEST_BYTES,
} from "@/lib/printer-limits";
import { DOTS_PER_MM } from "@/lib/paper";
import {
  errorText,
  validateBarcode,
  validateImageContent,
  validateQRCode,
  validateSeparator,
  validateText,
  type ValidationResult,
} from "@/lib/validation";

export type Block = PrintContent & {
  id: string;
  /** Screen-only hint for empty text blocks. Never sent to the printer. */
  placeholder?: string;
};

export interface JobSettings {
  codePage: string;
  autoCut: boolean;
  feedLinesAfterPrint: number;
}

export interface EditorDocument {
  blocks: Block[];
  settings: JobSettings;
}

export interface EditorState {
  doc: EditorDocument;
  selectedId: string | null;
}

export const DEFAULT_SETTINGS: JobSettings = {
  codePage: "PC852",
  autoCut: true,
  feedLinesAfterPrint: 3,
};

export const CODE_PAGES = ["PC852", "PC437", "PC850", "PC858", "WPC1250", "WPC1252", "ISO8859_2", "KATAKANA"];

export const BLOCK_LABELS: Record<ContentType, string> = {
  Text: "Text",
  Image: "Image",
  QRCode: "QR code",
  Barcode: "Barcode",
  Separator: "Separator",
  LineFeed: "Line feed",
  Cut: "Cut",
  CodePage: "Code page",
};

let counter = 0;
export function newId(): string {
  counter += 1;
  return `b${Date.now().toString(36)}${counter}`;
}

export function createBlock(type: ContentType): Block {
  const base: Block = { id: newId(), type };
  switch (type) {
    case ContentType.Text:
      return { ...base, content: "", alignment: Alignment.Left, style: [] };
    case ContentType.Image:
      return { ...base, content: "", alignment: Alignment.Center, imageOptions: { maxWidth: 576, preserveAspectRatio: true } };
    case ContentType.QRCode:
      return {
        ...base,
        content: "",
        alignment: Alignment.Center,
        qrCodeOptions: { model: QRCodeModel.Model2, size: QRCodeSize.Large, correctionLevel: QRCodeCorrectionLevel.Percent15 },
      };
    case ContentType.Barcode:
      return {
        ...base,
        content: "",
        alignment: Alignment.Center,
        barcodeOptions: { type: BarcodeType.CODE128, heightInDots: 80, width: BarWidth.Default, labelPosition: BarLabelPosition.Below },
      };
    case ContentType.Separator:
      return { ...base, separatorChar: "-", separatorLength: CHARS_PER_LINE.normal, style: [] };
    case ContentType.LineFeed:
      return { ...base, lines: 1 };
    case ContentType.Cut:
      return { ...base, partialCut: false };
    default:
      return base;
  }
}

export function noteDocument(): EditorDocument {
  return {
    settings: { ...DEFAULT_SETTINGS },
    blocks: [
      {
        ...createBlock(ContentType.Text),
        alignment: Alignment.Center,
        style: ["Bold", "DoubleWidth", "DoubleHeight"],
        placeholder: "Title",
      },
      { ...createBlock(ContentType.Separator) },
      { ...createBlock(ContentType.Text), placeholder: "Write something…" },
    ],
  };
}

export function emptyDocument(): EditorDocument {
  return { settings: { ...DEFAULT_SETTINGS }, blocks: [] };
}

// ---------------------------------------------------------------------------
// Reducer

export type EditorAction =
  | { type: "load"; doc: EditorDocument }
  | { type: "select"; id: string | null }
  | { type: "insert"; block: Block; afterId?: string | null }
  | { type: "update"; id: string; patch: Partial<Block> }
  | { type: "toggleStyle"; id: string; style: PrintStyle }
  | { type: "remove"; id: string }
  | { type: "duplicate"; id: string }
  | { type: "move"; id: string; delta: number }
  | { type: "moveTo"; id: string; index: number }
  | { type: "settings"; patch: Partial<JobSettings> };

export function editorReducer(state: EditorState, action: EditorAction): EditorState {
  const { blocks } = state.doc;
  const withBlocks = (next: Block[], selectedId = state.selectedId): EditorState => ({
    doc: { ...state.doc, blocks: next },
    selectedId,
  });

  switch (action.type) {
    case "load":
      return { doc: action.doc, selectedId: null };
    case "select":
      return { ...state, selectedId: action.id };
    case "insert": {
      const anchor = action.afterId ?? state.selectedId;
      const index = anchor ? blocks.findIndex((b) => b.id === anchor) : -1;
      const next = [...blocks];
      next.splice(index === -1 ? next.length : index + 1, 0, action.block);
      return withBlocks(next, action.block.id);
    }
    case "update":
      return withBlocks(blocks.map((b) => (b.id === action.id ? { ...b, ...action.patch } : b)));
    case "toggleStyle":
      return withBlocks(
        blocks.map((b) => {
          if (b.id !== action.id) return b;
          const style = b.style ?? [];
          return {
            ...b,
            style: style.includes(action.style) ? style.filter((s) => s !== action.style) : [...style, action.style],
          };
        }),
      );
    case "remove": {
      const index = blocks.findIndex((b) => b.id === action.id);
      const next = blocks.filter((b) => b.id !== action.id);
      const neighbour = next[Math.min(index, next.length - 1)];
      return withBlocks(next, state.selectedId === action.id ? (neighbour?.id ?? null) : state.selectedId);
    }
    case "duplicate": {
      const index = blocks.findIndex((b) => b.id === action.id);
      if (index === -1) return state;
      const copy = { ...blocks[index], id: newId() };
      const next = [...blocks];
      next.splice(index + 1, 0, copy);
      return withBlocks(next, copy.id);
    }
    case "move": {
      const index = blocks.findIndex((b) => b.id === action.id);
      return editorReducer(state, { type: "moveTo", id: action.id, index: index + action.delta });
    }
    case "moveTo": {
      const from = blocks.findIndex((b) => b.id === action.id);
      const to = Math.max(0, Math.min(blocks.length - 1, action.index));
      if (from === -1 || from === to) return state;
      const next = [...blocks];
      const [block] = next.splice(from, 1);
      next.splice(to, 0, block);
      return withBlocks(next);
    }
    case "settings":
      return { ...state, doc: { ...state.doc, settings: { ...state.doc.settings, ...action.patch } } };
  }
}

// ---------------------------------------------------------------------------
// Conversion and validation

/** False for a block that is not sent: an empty text block, or an image or code with no data yet. */
export function isPrintable(block: Block): boolean {
  if (block.type === ContentType.Text) return !!(block.content ?? "").trim();
  if (block.type === ContentType.Image || block.type === ContentType.QRCode || block.type === ContentType.Barcode) return !!block.content;
  return true;
}

/** Printable content: drops screen-only fields and the blocks that are not sent. */
export function toPrintContent(blocks: Block[]): PrintContent[] {
  return blocks.filter(isPrintable).map((b) => {
    const { id, placeholder, ...content } = b;
    void id;
    void placeholder;
    return content;
  });
}

export function toPrintOptions(settings: JobSettings): PrintOptions {
  return { codePage: settings.codePage, autoCut: settings.autoCut, feedLinesAfterPrint: settings.feedLinesAfterPrint };
}

export function fromPrintContent(content: PrintContent[]): Block[] {
  return content.map((c) => ({ ...c, id: newId() }));
}

const inRange = (value: number, min: number, max: number) => value >= min && value <= max;

/**
 * Returns an error message for a block that would fail or print wrong, else null.
 * Values come from saved drafts and templates too, so any number can be out of range.
 */
export function blockError(block: PrintContent): string | null {
  const message = (result: ValidationResult) => (result.isValid ? null : errorText(result));
  // An imported or hand-edited JSON can hold any value here.
  if (block.content != null && typeof block.content !== "string") return "Content must be text";
  switch (block.type) {
    case ContentType.Text:
      return message(validateText(block.content ?? ""));
    case ContentType.Barcode: {
      if (!block.content) return null;
      const height = block.barcodeOptions?.heightInDots;
      if (height != null && !inRange(height, BARCODE_MIN_HEIGHT_DOTS, BARCODE_MAX_HEIGHT_DOTS)) {
        return `Barcode height must be between ${BARCODE_MIN_HEIGHT_DOTS} and ${BARCODE_MAX_HEIGHT_DOTS} dots`;
      }
      return message(validateBarcode(block.content, block.barcodeOptions?.type ?? BarcodeType.CODE128));
    }
    case ContentType.QRCode: {
      if (!block.content) return null;
      return message(validateQRCode(block.content, block.qrCodeOptions?.model));
    }
    case ContentType.Image: {
      if (!block.content) return null;
      const { maxWidth, maxHeight } = block.imageOptions ?? {};
      if ((maxWidth != null && !(maxWidth >= IMAGE_MIN_PRINT_SIZE)) || (maxHeight != null && !(maxHeight >= IMAGE_MIN_PRINT_SIZE))) {
        return `Image max width and height must be at least ${IMAGE_MIN_PRINT_SIZE} dot`;
      }
      return message(validateImageContent(block.content));
    }
    case ContentType.Separator:
      return message(validateSeparator(block.separatorChar ?? "-", block.separatorLength ?? CHARS_PER_LINE.normal));
    case ContentType.LineFeed:
      return (block.lines ?? 1) > LINE_FEED_MAX_LINES ? `Too many lines: ${block.lines}. Max ${LINE_FEED_MAX_LINES} per line feed` : null;
    default:
      return null;
  }
}

/**
 * Returns an error message for a job the backend rejects as a whole, else null.
 * `content` is what is sent; `paperDots` comes from estimatePaperDots.
 */
export function documentError(content: PrintContent[], options: PrintOptions, paperDots: number): string | null {
  if (content.length > MAX_BLOCKS) return `Too many blocks: ${content.length}. Max ${MAX_BLOCKS} per print`;

  const images = content.filter((b) => b.type === ContentType.Image && b.content).length;
  if (images > MAX_IMAGE_BLOCKS) return `Too many images: ${images}. Max ${MAX_IMAGE_BLOCKS} per print`;

  if (!inRange(options.feedLinesAfterPrint ?? 0, 0, MAX_FEED_BEFORE_CUT)) return `Feed before cut must be between 0 and ${MAX_FEED_BEFORE_CUT}`;
  if (!inRange(options.defaultLineSpacing ?? 0, 0, MAX_LINE_SPACING)) return `Line spacing must be between 0 and ${MAX_LINE_SPACING}`;

  if (paperDots > MAX_PAPER_DOTS) {
    return `Too long: ≈ ${(paperDots / DOTS_PER_MM / 1000).toFixed(1)} m of paper. Max ${MAX_PAPER_DOTS / DOTS_PER_MM / 1000} m per print`;
  }

  // Images are base64 text, so the characters are close to the bytes on the wire.
  const bytes = content.reduce((sum, b) => sum + (b.content?.length ?? 0), 0);
  if (bytes > MAX_REQUEST_BYTES) {
    return `Too large to send: ≈ ${Math.round(bytes / 1_000_000)} MB. Max ${MAX_REQUEST_BYTES / 1_000_000} MB per print`;
  }

  return null;
}

/**
 * Why the job cannot be sent, else null: the first invalid block, or the document.
 * `locate` gets the index of the block in `content`. It can show the block and
 * return its name on screen, such as "Block 3".
 */
export function contentError(
  content: PrintContent[],
  options: PrintOptions,
  paperDots: number,
  locate: (index: number) => string | undefined,
): string | null {
  for (const [index, block] of content.entries()) {
    const message = blockError(block);
    if (!message) continue;
    const label = locate(index);
    return label ? `${label}: ${message}` : message;
  }
  return documentError(content, options, paperDots);
}
