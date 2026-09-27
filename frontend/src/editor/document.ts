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
import { validateBarcode, validateQRCode, validateSeparator } from "@/lib/validation";

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

/** Printable content: drops screen-only fields and empty text blocks. */
export function toPrintContent(blocks: Block[]): PrintContent[] {
  return blocks
    .filter((b) => !(b.type === ContentType.Text && !(b.content ?? "").trim()))
    .filter((b) => !((b.type === ContentType.Image || b.type === ContentType.QRCode || b.type === ContentType.Barcode) && !b.content))
    .map((b) => {
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

/** Returns an error message for a block that would fail or print wrong, else null. */
export function blockError(block: Block): string | null {
  const strip = (message?: string) => (message ?? "").replace(/^\[ERROR\]\s*/, "");
  switch (block.type) {
    case ContentType.Barcode: {
      if (!block.content) return null;
      const result = validateBarcode(block.content, block.barcodeOptions?.type ?? BarcodeType.CODE128);
      return result.isValid ? null : strip(result.error);
    }
    case ContentType.QRCode: {
      if (!block.content) return null;
      const result = validateQRCode(block.content);
      return result.isValid ? null : strip(result.error);
    }
    case ContentType.Separator: {
      const result = validateSeparator(block.separatorChar ?? "-", block.separatorLength ?? CHARS_PER_LINE.normal);
      return result.isValid ? null : strip(result.error);
    }
    default:
      return null;
  }
}
