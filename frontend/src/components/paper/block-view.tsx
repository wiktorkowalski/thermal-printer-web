import { useCallback, useState, type DragEvent, type ReactNode, type Ref } from "react";
import { ImagePlus } from "lucide-react";
import { cn } from "@/lib/utils";
import { fileToBase64 } from "@/lib/api";
import { validateImage } from "@/lib/validation";
import { HEAD_DOTS, countPrintedLines, longestLine, textMetrics } from "@/lib/paper";
import { BLOCK_LABELS, blockError, type Block } from "@/editor/document";
import { PaperText } from "./paper-text";
import { BarcodeView, DitheredImage, QrView, type BarcodeRender } from "./graphic-views";
import { CutRow, FeedSpace, PaperRow, TearEdge } from "./paper-strip";

const STYLE_TAGS: Record<string, string> = {
  Bold: "B",
  Underline: "U",
  Italic: "I",
  DoubleWidth: "2W",
  DoubleHeight: "2H",
  FontB: "FB",
  ReverseMode: "REV",
  UpsideDownMode: "UD",
};

export interface BlockViewProps {
  block: Block;
  index: number;
  selected: boolean;
  feedLines: number;
  onSelect: () => void;
  onUpdate: (patch: Partial<Block>) => void;
  onImageError: (message: string) => void;
  onImageSize: (heightDots: number) => void;
  textareaRef?: Ref<HTMLTextAreaElement>;
  toolbar?: ReactNode;
  dragProps: {
    onDragStart: (e: DragEvent) => void;
    onDragOver: (e: DragEvent) => void;
    onDrop: (e: DragEvent) => void;
    dropTarget: boolean;
  };
}

export function BlockView(props: BlockViewProps) {
  const { block, index, selected, feedLines, onSelect, onUpdate, textareaRef, toolbar, dragProps } = props;
  const [barcode, setBarcode] = useState<BarcodeRender | null>(null);
  const onBarcode = useCallback((r: BarcodeRender) => setBarcode(r), []);
  const { onImageSize } = props;
  const onSize = useCallback((size: { height: number }) => onImageSize(size.height), [onImageSize]);
  const label = BLOCK_LABELS[block.type];
  const error = blockError(block);
  const align = (block.alignment ?? "Left").toLowerCase() as "left" | "center" | "right";

  const handle = (
    <button
      type="button"
      draggable
      onDragStart={dragProps.onDragStart}
      onClick={onSelect}
      aria-label={`Select block ${index + 1}: ${label}`}
      aria-pressed={selected}
      className={cn(
        "flex h-11 w-5 shrink-0 cursor-grab items-center justify-center rounded active:cursor-grabbing",
        selected && "cursor-default",
      )}
    >
      {selected ? <span className="block h-full w-1 rounded-sm bg-accent" /> : <span className="grip" />}
    </button>
  );

  const tag = (
    <div className={cn("text-right", selected ? "text-accent-text" : "text-ink-3")}>
      <div className="font-semibold tracking-[1px] uppercase">{label}</div>
      {block.style && block.style.length > 0 && (
        <div className="font-mono text-[10px]">{block.style.map((s) => STYLE_TAGS[s] ?? s).join(" ")}</div>
      )}
    </div>
  );

  const rowProps = {
    left: (
      <>
        {tag}
        {handle}
      </>
    ),
    className: cn(dragProps.dropTarget && "before:absolute before:inset-x-0 before:-top-px before:z-20 before:h-0.5 before:bg-accent"),
  };

  const frame = (content: ReactNode, extra?: string) => (
    <div
      onMouseDown={selected ? undefined : onSelect}
      className={cn(
        "relative rounded-[2px] outline-offset-4",
        selected ? "bg-accent/5 outline-[1.5px] outline-dashed outline-accent" : "cursor-pointer hover:outline hover:outline-1 hover:outline-dashed hover:outline-paper-rule",
        extra,
      )}
      style={{ textAlign: align }}
    >
      {content}
    </div>
  );

  const drop = { onDragOver: dragProps.onDragOver, onDrop: dragProps.onDrop };

  if (block.type === "Cut") {
    return (
      <div {...drop}>
        <PaperRow {...rowProps} paperClassName="py-0">
          {frame(<FeedSpace lines={feedLines} />)}
        </PaperRow>
        <CutRow right={<span>{block.partialCut ? "partial" : "full"}</span>} />
        <TearEdge />
        <div className="h-5" />
        <TearEdge top />
      </div>
    );
  }

  let content: ReactNode;
  let right: ReactNode = null;

  switch (block.type) {
    case "Text": {
      const m = textMetrics(block.style);
      const text = block.content ?? "";
      const longest = longestLine(text);
      const lines = countPrintedLines(text, m.maxChars);
      const wraps = longest > m.maxChars;
      content = (
        <PaperText
          text={text}
          style={block.style}
          alignment={block.alignment}
          placeholder={block.placeholder}
          label={`Text block ${index + 1}`}
          textareaRef={selected ? textareaRef : undefined}
          onChange={(value) => onUpdate({ content: value })}
        />
      );
      right = (
        <>
          <span className={cn(wraps ? "text-warn" : selected && "text-accent-text", "font-medium")}>
            {longest}/{m.maxChars}
          </span>
          <span>{wraps ? "wraps" : `${lines} ${lines === 1 ? "line" : "lines"}`}</span>
        </>
      );
      break;
    }
    case "Separator": {
      const length = block.separatorLength ?? 32;
      content = <PaperText text={(block.separatorChar || "-").slice(0, 1).repeat(length)} style={block.style} />;
      right = <span>{length}/{textMetrics(block.style).maxChars}</span>;
      break;
    }
    case "Image":
      content = block.content ? (
        <DitheredImage base64={block.content} options={block.imageOptions} onSize={onSize} />
      ) : (
        <ImagePicker onPicked={(content) => onUpdate({ content })} onError={props.onImageError} />
      );
      right = block.content ? <span>1-bit</span> : null;
      break;
    case "QRCode":
      content = block.content ? (
        <QrView data={block.content} options={block.qrCodeOptions} />
      ) : (
        <span className="text-paper-faint">[QR · add data in the panel]</span>
      );
      right = (
        <>
          <span>{block.qrCodeOptions?.size ?? "Normal"}</span>
          <span>{block.qrCodeOptions?.correctionLevel?.replace("Percent", "") ?? "7"}%</span>
        </>
      );
      break;
    case "Barcode": {
      content = block.content ? (
        <BarcodeView data={block.content} options={block.barcodeOptions} onRender={onBarcode} />
      ) : (
        <span className="text-paper-faint">[barcode · add data in the panel]</span>
      );
      const tooWide = barcode?.valid && barcode.widthDots > HEAD_DOTS;
      right = block.content ? (
        <>
          <span className={cn(error || tooWide ? "text-warn" : "text-ok-text")}>
            {error ? "invalid" : tooWide ? "too wide" : "✓ valid"}
          </span>
          <span>{block.barcodeOptions?.heightInDots ?? 162} dots</span>
        </>
      ) : null;
      break;
    }
    case "LineFeed":
      content = <FeedSpace lines={block.lines ?? 1} />;
      right = <span>× {block.lines ?? 1}</span>;
      break;
    default:
      content = <span className="text-paper-faint">[{block.type}]</span>;
  }

  return (
    <div {...drop}>
      <PaperRow {...rowProps} right={right} paperClassName="py-1.5" overlay={selected ? toolbar : undefined}>
        {frame(content)}
      </PaperRow>
    </div>
  );
}

function ImagePicker({ onPicked, onError }: { onPicked: (base64: string) => void; onError: (message: string) => void }) {
  const pick = async (file: File | undefined) => {
    if (!file) return;
    const result = validateImage(file);
    if (!result.isValid) {
      onError((result.error ?? "Invalid image").replace(/^\[ERROR\]\s*/, ""));
      return;
    }
    onPicked(await fileToBase64(file));
  };
  return (
    <label
      className="flex cursor-pointer flex-col items-center gap-1 border border-dashed border-paper-rule px-4 py-5 text-paper-faint hover:border-paper-faint"
      onDragOver={(e) => {
        if (e.dataTransfer.types.includes("Files")) e.preventDefault();
      }}
      onDrop={(e) => {
        if (!e.dataTransfer.files.length) return;
        e.preventDefault();
        e.stopPropagation();
        void pick(e.dataTransfer.files[0]);
      }}
    >
      <ImagePlus className="size-5" aria-hidden="true" />
      <span className="font-sans text-sm">Choose, drop or paste an image</span>
      <span className="font-sans text-xs">Max 5 MB · printed 1-bit, up to 576 dots wide</span>
      <input type="file" accept="image/*" className="sr-only" onChange={(e) => void pick(e.target.files?.[0])} />
    </label>
  );
}
