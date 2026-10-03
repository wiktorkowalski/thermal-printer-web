import type { ReactNode } from "react";
import { AlignCenter, AlignLeft, AlignRight, ArrowDown, ArrowUp, Copy, Trash2 } from "lucide-react";
import {
  Alignment,
  BarLabelPosition,
  BarWidth,
  BarcodeType,
  QRCodeCorrectionLevel,
  QRCodeModel,
  QRCodeSize,
  type PrintStyle,
} from "@/types/printer";
import { cn } from "@/lib/utils";
import { CHARS_PER_LINE } from "@/lib/printer-constants";
import { BARCODE_MAX_HEIGHT_DOTS, EDITOR_LINE_FEED_MAX_LINES } from "@/lib/printer-limits";
import { HEAD_DOTS, LINE_DOTS, longestLine, textMetrics } from "@/lib/paper";
import { BLOCK_LABELS, blockError, type Block } from "@/editor/document";
import { SectionLabel, Segmented, Switch, ToggleChip, fieldLabelClass, inputClass, quietButtonClass } from "./controls";

export interface InspectorProps {
  block: Block;
  index: number;
  count: number;
  onUpdate: (patch: Partial<Block>) => void;
  onToggleStyle: (style: PrintStyle) => void;
  onDuplicate: () => void;
  onRemove: () => void;
  onMove: (delta: number) => void;
}

const TEXT_STYLES: { style: PrintStyle; label: string; className?: string }[] = [
  { style: "Bold", label: "Bold", className: "font-bold" },
  { style: "Underline", label: "Underline", className: "underline" },
  { style: "Italic", label: "Italic", className: "italic" },
  { style: "FontB", label: "Font B · 64" },
  { style: "DoubleWidth", label: "Double width" },
  { style: "DoubleHeight", label: "Double height" },
  { style: "ReverseMode", label: "Reverse" },
  { style: "UpsideDownMode", label: "Upside-down" },
];

const BARCODE_TYPES: { value: BarcodeType; label: string }[] = [
  { value: BarcodeType.CODE128, label: "CODE128" },
  { value: BarcodeType.CODE39, label: "CODE39" },
  { value: BarcodeType.EAN13, label: "EAN-13" },
  { value: BarcodeType.EAN8, label: "EAN-8" },
  { value: BarcodeType.UPC_A, label: "UPC-A" },
  { value: BarcodeType.UPC_E, label: "UPC-E" },
  { value: BarcodeType.ITF, label: "ITF" },
  { value: BarcodeType.CODABAR, label: "CODABAR" },
  { value: BarcodeType.GS1_128, label: "GS1-128" },
  { value: BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL, label: "GS1 DataBar" },
];

export function AlignmentControl({ value, onChange }: { value?: Alignment; onChange: (value: Alignment) => void }) {
  return (
    <Segmented
      label="Alignment"
      value={value ?? Alignment.Center}
      onChange={onChange}
      options={[
        { value: Alignment.Left, label: <AlignLeft className="mx-auto size-4" aria-label="Left" /> },
        { value: Alignment.Center, label: <AlignCenter className="mx-auto size-4" aria-label="Center" /> },
        { value: Alignment.Right, label: <AlignRight className="mx-auto size-4" aria-label="Right" /> },
      ]}
    />
  );
}

function Field({ label, htmlFor, children, hint }: { label: string; htmlFor?: string; children: ReactNode; hint?: ReactNode }) {
  return (
    <div className="flex flex-col gap-2">
      <label htmlFor={htmlFor} className={fieldLabelClass}>
        {label}
      </label>
      {children}
      {hint}
    </div>
  );
}

function FitMeter({ used, max }: { used: number; max: number }) {
  const ratio = Math.min(1, used / max);
  const wraps = used > max;
  return (
    <div className="flex flex-col gap-2 rounded-[10px] border border-line px-4 py-3.5 text-[13px]">
      <div className="flex justify-between">
        <span className="text-ink-2">Longest line</span>
        <span className="font-mono">
          {used} / {max}
        </span>
      </div>
      <div className="relative h-1.5 rounded-full bg-well">
        <div className={cn("absolute inset-y-0 left-0 rounded-full", wraps ? "bg-warn" : "bg-ok")} style={{ width: `${ratio * 100}%` }} />
      </div>
      <div className="text-ink-2">{wraps ? "Too long. The printer breaks the line mid-word." : "Every line fits. No wrap."}</div>
    </div>
  );
}

export function Inspector({ block, index, count, onUpdate, onToggleStyle, onDuplicate, onRemove, onMove }: InspectorProps) {
  const error = blockError(block);
  const id = `insp-${block.id}`;

  let body: ReactNode = null;
  switch (block.type) {
    case "Text": {
      const m = textMetrics(block.style);
      body = (
        <>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Style</span>
            <div className="grid grid-cols-2 gap-1.5">
              {TEXT_STYLES.map(({ style, label, className }) => (
                <ToggleChip key={style} pressed={block.style?.includes(style) ?? false} onClick={() => onToggleStyle(style)} className={className}>
                  {label}
                </ToggleChip>
              ))}
            </div>
          </div>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Alignment</span>
            <AlignmentControl value={block.alignment} onChange={(alignment) => onUpdate({ alignment })} />
          </div>
          <FitMeter used={longestLine(block.content ?? "")} max={m.maxChars} />
        </>
      );
      break;
    }
    case "Separator":
      body = (
        <>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Character" htmlFor={`${id}-char`}>
              <input
                id={`${id}-char`}
                maxLength={1}
                value={block.separatorChar ?? "-"}
                onChange={(e) => onUpdate({ separatorChar: e.target.value.slice(-1) || "-" })}
                className={cn(inputClass, "font-mono")}
              />
            </Field>
            <Field label="Length" htmlFor={`${id}-len`}>
              <input
                id={`${id}-len`}
                type="number"
                min={1}
                max={textMetrics(block.style).maxChars}
                value={block.separatorLength ?? CHARS_PER_LINE.normal}
                onChange={(e) => onUpdate({ separatorLength: Number(e.target.value) || 1 })}
                className={cn(inputClass, "font-mono")}
              />
            </Field>
          </div>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Alignment</span>
            <AlignmentControl value={block.alignment} onChange={(alignment) => onUpdate({ alignment })} />
          </div>
        </>
      );
      break;
    case "Image":
      body = (
        <>
          <Field
            label="Max width"
            htmlFor={`${id}-w`}
            hint={<span className="font-mono text-xs text-ink-2">{block.imageOptions?.maxWidth ?? HEAD_DOTS} dots · {Math.round((block.imageOptions?.maxWidth ?? HEAD_DOTS) / 8)} mm</span>}
          >
            <input
              id={`${id}-w`}
              type="range"
              min={96}
              max={HEAD_DOTS}
              step={8}
              value={block.imageOptions?.maxWidth ?? HEAD_DOTS}
              onChange={(e) => onUpdate({ imageOptions: { ...block.imageOptions, maxWidth: Number(e.target.value), preserveAspectRatio: true } })}
              className="accent-accent"
            />
          </Field>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Alignment</span>
            <AlignmentControl value={block.alignment} onChange={(alignment) => onUpdate({ alignment })} />
          </div>
          {block.content && (
            <button type="button" onClick={() => onUpdate({ content: "" })} className={quietButtonClass}>
              Replace image
            </button>
          )}
          <p className="text-[13px] text-ink-2">Preview shows the 1-bit dither. Paste an image with ⌘V while this block is selected.</p>
        </>
      );
      break;
    case "QRCode":
      body = (
        <>
          <Field label="Data" htmlFor={`${id}-data`} hint={error && <span className="text-xs text-danger-text">{error}</span>}>
            <textarea
              id={`${id}-data`}
              rows={3}
              value={block.content ?? ""}
              placeholder="https://… or any text"
              onChange={(e) => onUpdate({ content: e.target.value })}
              className={cn(inputClass, "h-auto py-2 font-mono text-sm")}
            />
          </Field>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Module size</span>
            <Segmented
              label="Module size"
              value={block.qrCodeOptions?.size ?? QRCodeSize.Normal}
              onChange={(size) => onUpdate({ qrCodeOptions: { ...qr(block), size } })}
              options={[
                { value: QRCodeSize.Normal, label: "Normal" },
                { value: QRCodeSize.Large, label: "Large" },
                { value: QRCodeSize.ExtraLarge, label: "XL" },
              ]}
            />
          </div>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Error correction</span>
            <Segmented
              label="Error correction"
              value={block.qrCodeOptions?.correctionLevel ?? QRCodeCorrectionLevel.Percent7}
              onChange={(correctionLevel) => onUpdate({ qrCodeOptions: { ...qr(block), correctionLevel } })}
              options={[
                { value: QRCodeCorrectionLevel.Percent7, label: "7%" },
                { value: QRCodeCorrectionLevel.Percent15, label: "15%" },
                { value: QRCodeCorrectionLevel.Percent25, label: "25%" },
                { value: QRCodeCorrectionLevel.Percent30, label: "30%" },
              ]}
            />
          </div>
          <Field label="Model" htmlFor={`${id}-model`}>
            <select
              id={`${id}-model`}
              value={block.qrCodeOptions?.model ?? QRCodeModel.Model2}
              onChange={(e) => onUpdate({ qrCodeOptions: { ...qr(block), model: e.target.value as QRCodeModel } })}
              className={inputClass}
            >
              <option value={QRCodeModel.Model2}>Model 2</option>
              <option value={QRCodeModel.Model1}>Model 1</option>
              <option value={QRCodeModel.Micro}>Micro</option>
            </select>
          </Field>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Alignment</span>
            <AlignmentControl value={block.alignment} onChange={(alignment) => onUpdate({ alignment })} />
          </div>
        </>
      );
      break;
    case "Barcode": {
      const options = block.barcodeOptions ?? { type: BarcodeType.CODE128 };
      const height = options.heightInDots ?? 162;
      body = (
        <>
          <Field
            label="Data"
            htmlFor={`${id}-data`}
            hint={
              block.content ? (
                error ? (
                  <span className="text-xs text-danger-text">{error}</span>
                ) : (
                  <span className="text-xs text-ok-text">
                    Valid for {BARCODE_TYPES.find((t) => t.value === options.type)?.label} · {block.content.length} characters
                  </span>
                )
              ) : null
            }
          >
            <input
              id={`${id}-data`}
              value={block.content ?? ""}
              onChange={(e) => onUpdate({ content: e.target.value })}
              className={cn(inputClass, "font-mono")}
            />
          </Field>
          <Field label="Symbology" htmlFor={`${id}-type`}>
            <select
              id={`${id}-type`}
              value={options.type}
              onChange={(e) => onUpdate({ barcodeOptions: { ...options, type: e.target.value as BarcodeType } })}
              className={inputClass}
            >
              {BARCODE_TYPES.map((t) => (
                <option key={t.value} value={t.value}>
                  {t.label}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Height" htmlFor={`${id}-h`} hint={<span className="font-mono text-xs text-ink-2">{height} dots · {(height / 8).toFixed(1)} mm</span>}>
            <input
              id={`${id}-h`}
              type="range"
              min={16}
              max={BARCODE_MAX_HEIGHT_DOTS}
              value={height}
              onChange={(e) => onUpdate({ barcodeOptions: { ...options, heightInDots: Number(e.target.value) } })}
              className="accent-accent"
            />
          </Field>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Bar width</span>
            <Segmented
              label="Bar width"
              value={options.width ?? BarWidth.Default}
              onChange={(width) => onUpdate({ barcodeOptions: { ...options, width } })}
              options={[
                { value: BarWidth.Thin, label: "Thin" },
                { value: BarWidth.Default, label: "Default" },
                { value: BarWidth.Thick, label: "Thick" },
              ]}
            />
          </div>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Human-readable label</span>
            <Segmented
              label="Human-readable label"
              value={options.labelPosition ?? BarLabelPosition.Below}
              onChange={(labelPosition) => onUpdate({ barcodeOptions: { ...options, labelPosition } })}
              options={[
                { value: BarLabelPosition.None, label: "None" },
                { value: BarLabelPosition.Above, label: "Above" },
                { value: BarLabelPosition.Below, label: "Below" },
                { value: BarLabelPosition.Both, label: "Both" },
              ]}
            />
            <div className="flex h-11 items-center justify-between text-sm">
              <span>Label in Font B</span>
              <Switch label="Label in Font B" checked={options.useFontB ?? false} onChange={(useFontB) => onUpdate({ barcodeOptions: { ...options, useFontB } })} />
            </div>
          </div>
          <div className="flex flex-col gap-2.5">
            <span className={fieldLabelClass}>Alignment</span>
            <AlignmentControl value={block.alignment} onChange={(alignment) => onUpdate({ alignment })} />
          </div>
        </>
      );
      break;
    }
    case "LineFeed":
      body = (
        <Field label="Lines" htmlFor={`${id}-lines`} hint={<span className="font-mono text-xs text-ink-2">≈ {(((block.lines ?? 1) * LINE_DOTS) / 8).toFixed(1)} mm</span>}>
          <input
            id={`${id}-lines`}
            type="number"
            min={1}
            max={EDITOR_LINE_FEED_MAX_LINES}
            value={block.lines ?? 1}
            onChange={(e) => onUpdate({ lines: Math.max(1, Math.min(EDITOR_LINE_FEED_MAX_LINES, Number(e.target.value) || 1)) })}
            className={cn(inputClass, "font-mono")}
          />
        </Field>
      );
      break;
    case "Cut":
      body = (
        <>
          <Segmented
            label="Cut type"
            value={block.partialCut ? "partial" : "full"}
            onChange={(v) => onUpdate({ partialCut: v === "partial" })}
            options={[
              { value: "full", label: "Full cut" },
              { value: "partial", label: "Partial cut" },
            ]}
          />
          <p className="text-[13px] text-ink-2">The printer feeds the job's “feed before cut” lines, then cuts. A partial cut leaves a small bridge.</p>
        </>
      );
      break;
  }

  return (
    <div className="flex flex-col gap-5">
      <div className="flex items-baseline justify-between">
        <SectionLabel>Block {String(index + 1).padStart(2, "0")}</SectionLabel>
        <span className="font-serif text-[22px]">{BLOCK_LABELS[block.type]}</span>
      </div>
      {/* QR code and barcode show their error under the data field. */}
      {error && block.type !== "QRCode" && block.type !== "Barcode" && (
        <p role="alert" className="text-xs text-danger-text">
          {error}
        </p>
      )}
      {body}
      <div className="grid grid-cols-2 gap-2">
        <button type="button" onClick={() => onMove(-1)} disabled={index === 0} className={quietButtonClass}>
          <ArrowUp className="size-4" aria-hidden="true" />
          Move up
        </button>
        <button type="button" onClick={() => onMove(1)} disabled={index === count - 1} className={quietButtonClass}>
          <ArrowDown className="size-4" aria-hidden="true" />
          Move down
        </button>
        <button type="button" onClick={onDuplicate} className={quietButtonClass}>
          <Copy className="size-4" aria-hidden="true" />
          Duplicate
        </button>
        <button type="button" onClick={onRemove} className={cn(quietButtonClass, "text-danger-text")}>
          <Trash2 className="size-4" aria-hidden="true" />
          Delete
        </button>
      </div>
    </div>
  );
}

function qr(block: Block) {
  return block.qrCodeOptions ?? { model: QRCodeModel.Model2, size: QRCodeSize.Normal, correctionLevel: QRCodeCorrectionLevel.Percent7 };
}
