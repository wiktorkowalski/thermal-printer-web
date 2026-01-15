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

export interface PrintContent {
  type: ContentType;
  content?: string;
  alignment?: Alignment;
  style?: PrintStyle[];
  barcodeOptions?: BarcodeOptions;
  qrCodeOptions?: QRCodeOptions;
  imageOptions?: ImageOptions;
  lines?: number;
  partialCut?: boolean;
  separatorChar?: string;
  separatorLength?: number;
}

export interface PrintOptions {
  codePage?: string;
  defaultLineSpacing?: number;
  autoCut?: boolean;
  feedLinesAfterPrint?: number;
}

export interface PrintRequest {
  name?: string;
  message?: string;
  imageBase64?: string;
  content?: PrintContent[];
  options?: PrintOptions;
  source?: string;
}
