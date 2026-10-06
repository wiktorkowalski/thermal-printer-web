// TypeScript types matching C# backend models

export const ContentType = {
  Text: "Text",
  Image: "Image",
  Barcode: "Barcode",
  QRCode: "QRCode",
  LineFeed: "LineFeed",
  Cut: "Cut",
  Separator: "Separator",
  CodePage: "CodePage",
  Signal: "Signal",
} as const;
export type ContentType = (typeof ContentType)[keyof typeof ContentType];

export const Alignment = {
  Left: "Left",
  Center: "Center",
  Right: "Right",
} as const;
export type Alignment = (typeof Alignment)[keyof typeof Alignment];

export const PrintStyle = {
  Normal: "Normal",
  Bold: "Bold",
  Italic: "Italic",
  Underline: "Underline",
  DoubleHeight: "DoubleHeight",
  DoubleWidth: "DoubleWidth",
  FontB: "FontB",
  ReverseMode: "ReverseMode",
  UpsideDownMode: "UpsideDownMode",
} as const;
export type PrintStyle = (typeof PrintStyle)[keyof typeof PrintStyle];

export const BarcodeType = {
  UPC_A: "UPC_A",
  UPC_E: "UPC_E",
  EAN13: "EAN13",
  EAN8: "EAN8",
  CODE39: "CODE39",
  CODE128: "CODE128",
  ITF: "ITF",
  CODABAR: "CODABAR",
  GS1_128: "GS1_128",
  GS1_DATABAR_OMNIDIRECTIONAL: "GS1_DATABAR_OMNIDIRECTIONAL",
} as const;
export type BarcodeType = (typeof BarcodeType)[keyof typeof BarcodeType];

export const BarWidth = {
  Thin: "Thin",
  Default: "Default",
  Thick: "Thick",
} as const;
export type BarWidth = (typeof BarWidth)[keyof typeof BarWidth];

export const BarLabelPosition = {
  None: "None",
  Above: "Above",
  Below: "Below",
  Both: "Both",
} as const;
export type BarLabelPosition = (typeof BarLabelPosition)[keyof typeof BarLabelPosition];

export const QRCodeModel = {
  Model1: "Model1",
  Model2: "Model2",
  Micro: "Micro",
} as const;
export type QRCodeModel = (typeof QRCodeModel)[keyof typeof QRCodeModel];

export const QRCodeSize = {
  Normal: "Normal",
  Large: "Large",
  ExtraLarge: "ExtraLarge",
} as const;
export type QRCodeSize = (typeof QRCodeSize)[keyof typeof QRCodeSize];

export const QRCodeCorrectionLevel = {
  Percent7: "Percent7",
  Percent15: "Percent15",
  Percent25: "Percent25",
  Percent30: "Percent30",
} as const;
export type QRCodeCorrectionLevel = (typeof QRCodeCorrectionLevel)[keyof typeof QRCodeCorrectionLevel];

/** What a Signal block or a beep call does: the buzzer, the error light, or both. */
export const SignalMode = {
  Sound: "Sound",
  Light: "Light",
  SoundAndLight: "SoundAndLight",
} as const;
export type SignalMode = (typeof SignalMode)[keyof typeof SignalMode];

export interface BarcodeOptions {
  type: BarcodeType;
  heightInDots?: number;
  width?: BarWidth;
  labelPosition?: BarLabelPosition;
  useFontB?: boolean;
}

export interface QRCodeOptions {
  model: QRCodeModel;
  size: QRCodeSize;
  correctionLevel: QRCodeCorrectionLevel;
}

export interface ImageOptions {
  maxWidth?: number;
  maxHeight?: number;
  preserveAspectRatio?: boolean;
  useLegacyMode?: boolean;
}

/** Signal block. Count and duration are 1 to 9 each (lib/printer-limits.ts); a field that is left out is Sound, 1 and 1. */
export interface SignalOptions {
  mode?: SignalMode | null;
  count?: number | null;
  duration?: number | null;
}

/** Character size as multipliers, 1 to 8 each. An axis that is left out is 1. */
export interface TextSize {
  width?: number;
  height?: number;
}

export interface PrintContent {
  type: ContentType;
  content?: string;
  alignment?: Alignment;
  style?: PrintStyle[];
  /** Text and Separator. When present it replaces the DoubleWidth and DoubleHeight styles. */
  size?: TextSize | null;
  barcodeOptions?: BarcodeOptions;
  qrCodeOptions?: QRCodeOptions;
  imageOptions?: ImageOptions;
  lines?: number;
  partialCut?: boolean;
  separatorChar?: string;
  separatorLength?: number;
  /** Signal only. The web UI never adds a Signal block by itself: it shows one that a stored job or an imported template holds. */
  signalOptions?: SignalOptions | null;
  /**
   * Text only. True: the server breaks the lines at spaces. The web UI never sets it; it keeps the flag of a stored job
   * or an imported template, and the paper shows such a text with the breaks of the printer (in the middle of a word).
   */
  wrap?: boolean | null;
}

export interface PrintOptions {
  codePage?: string;
  defaultLineSpacing?: number;
  autoCut?: boolean;
  feedLinesAfterPrint?: number;
}

/**
 * Body of every answer from POST /api/printer. On failure `type` says whose fault it is:
 * "validation" (400, the payload), "printer" (503) or "busy" (503 with Retry-After).
 * The job endpoints also answer "journal-off" (503, the print journal is off) and
 * "journal" (503, the journal cannot be read at the moment).
 */
export interface PrintResponse {
  success: boolean;
  error?: string | null;
  type?: "validation" | "printer" | "busy" | "journal" | "journal-off" | null;
}

/**
 * One job of the print journal (GET /api/printer/jobs), matching backend/Models/PrintJobDtos.cs.
 * Every text here is caller text: render it as text only.
 */
export interface PrintJobSummary {
  id: string;
  /** UTC, ISO 8601. */
  createdAt: string;
  transport: string;
  source: string | null;
  result: string;
  error: string | null;
  title: string | null;
  blockCount: number | null;
  paperDots: number | null;
  reprintOf: string | null;
  canReprint: boolean;
}

export interface PrintJobList {
  jobs: PrintJobSummary[];
  /** The `before` value of the next page; null on the last page. */
  next: string | null;
}

/** GET /api/printer/jobs/{id}. An image block holds a hash (IMAGE_HASH_PREFIX in lib/printer-limits.ts), not the picture. */
export interface PrintJobDetail {
  job: PrintJobSummary;
  blocks: (PrintContent | null)[] | null;
  options: PrintOptions | null;
}

/** One hit of GET /api/printer/jobs/search. `snippet` is printed text around the match, on one line. */
export interface PrintJobSearchHit {
  job: PrintJobSummary;
  snippet: string;
}

export interface PrintJobSearchResult {
  hits: PrintJobSearchHit[];
  /** The `before` value that continues the search in the older jobs. A page can hold no hit and still have one. */
  next: string | null;
}

/** Paper is the paper of the jobs that printed, in printer dots. */
export interface PrintJobCounts {
  jobs: number;
  printed: number;
  reprints: number;
  paperDots: number;
}

export interface PrintJobDayStats {
  /** A UTC day, `yyyy-MM-dd`. */
  day: string;
  jobs: number;
  printed: number;
  paperDots: number;
}

export interface PrintJobSourceStats {
  /** Caller text; null stands for the jobs with no source. */
  source: string | null;
  jobs: number;
  printed: number;
  paperDots: number;
}

/** GET /api/printer/jobs/stats. `byDay` holds every day from `from` to `to`, oldest first. */
export interface PrintJobStats {
  from: string;
  to: string;
  totals: PrintJobCounts;
  byDay: PrintJobDayStats[];
  bySource: PrintJobSourceStats[];
  moreSources: boolean;
  byResult: { result: string; jobs: number }[];
}

/** One papercut: the strips with the same subject line. `subject` is printed text. */
export interface PapercutEntry {
  subject: string;
  count: number;
  firstAt: string;
  lastAt: string;
  lastJobId: string;
}

/** GET /api/printer/jobs/papercuts. `more` says that older strips or more subjects exist than the answer holds. */
export interface PapercutLedger {
  papercuts: PapercutEntry[];
  strips: number;
  more: boolean;
}

export interface PrintRequest {
  name?: string;
  message?: string;
  imageBase64?: string;
  content?: PrintContent[];
  options?: PrintOptions;
  source?: string;
}
