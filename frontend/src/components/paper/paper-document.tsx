import type { ReactNode } from "react";
import type { PrintContent } from "@/types/printer";
import { cn } from "@/lib/utils";
import { DEFAULT_ALIGNMENT } from "@/lib/paper";
import { PaperText } from "./paper-text";
import { BarcodeView, DitheredImage, QrView } from "./graphic-views";
import { CutRow, FeedSpace, PaperRow, TearEdge } from "./paper-strip";

/** Static rendering of one print block, without editing affordances. */
export function StaticBlock({ block }: { block: PrintContent }) {
  const align = (block.alignment ?? DEFAULT_ALIGNMENT).toLowerCase() as "left" | "center" | "right";
  switch (block.type) {
    case "Text":
      return <PaperText text={block.content ?? ""} style={block.style} alignment={block.alignment} />;
    case "Separator":
      return <PaperText text={(block.separatorChar || "=").slice(0, 1).repeat(block.separatorLength ?? 32)} style={block.style} alignment={block.alignment} />;
    case "Image":
      return block.content ? (
        <div style={{ textAlign: align }}>
          <DitheredImage base64={block.content} options={block.imageOptions} />
        </div>
      ) : null;
    case "QRCode":
      return block.content ? (
        <div style={{ textAlign: align }}>
          <QrView data={block.content} options={block.qrCodeOptions} />
        </div>
      ) : null;
    case "Barcode":
      return block.content ? (
        <div style={{ textAlign: align }}>
          <BarcodeView data={block.content} options={block.barcodeOptions} />
        </div>
      ) : null;
    case "LineFeed":
      return <FeedSpace lines={block.lines ?? 1} />;
    default:
      return null;
  }
}

export interface PaperGroup {
  id: string;
  start: number;
  end: number;
  label: string;
}

interface PaperDocumentProps {
  content: PrintContent[];
  feedLines: number;
  autoCut: boolean;
  /** Runs of blocks that select together, like a receipt line item. */
  groups?: PaperGroup[];
  selectedGroup?: string | null;
  onSelectGroup?: (id: string) => void;
  /** Rendered right after the last group. */
  afterGroups?: ReactNode;
  /** Right-gutter note on the cut line. */
  cutNote?: ReactNode;
}

/** A whole print job on paper, read-only apart from selectable groups. */
export function PaperDocument({ content, feedLines, autoCut, groups = [], selectedGroup, onSelectGroup, afterGroups, cutNote }: PaperDocumentProps) {
  const rows: ReactNode[] = [];
  const lastGroupEnd = groups.length ? groups[groups.length - 1].end : -1;
  const groupAt = new Map(groups.map((g) => [g.start, g]));

  for (let i = 0; i < content.length; ) {
    const group = groupAt.get(i);
    if (group) {
      const selected = group.id === selectedGroup;
      rows.push(
        <PaperRow
          key={`g-${group.id}`}
          left={
            <>
              <span className={cn("font-mono", selected ? "text-accent-text" : "text-ink-3")}>{group.label}</span>
              <button
                type="button"
                onClick={() => onSelectGroup?.(group.id)}
                aria-label={`Edit item ${group.label}`}
                aria-pressed={selected}
                className="flex w-5 self-stretch items-center justify-center"
              >
                {selected ? <span className="block h-full min-h-4 w-1 rounded-sm bg-accent" /> : <span className="grip" />}
              </button>
            </>
          }
          paperClassName="py-0.5"
        >
          <div
            onMouseDown={() => onSelectGroup?.(group.id)}
            className={cn(
              "cursor-pointer rounded-[2px] outline-offset-2",
              selected ? "bg-accent/8 outline-[1.5px] outline-dashed outline-accent" : "hover:bg-paper-rule/15",
            )}
          >
            {content.slice(group.start, group.end).map((block, j) => (
              <StaticBlock key={j} block={block} />
            ))}
          </div>
        </PaperRow>,
      );
      if (group.end === lastGroupEnd && afterGroups) rows.push(<div key="after-groups">{afterGroups}</div>);
      i = Math.max(group.end, i + 1);
      continue;
    }

    const block = content[i];
    if (block.type === "Cut") {
      rows.push(
        <div key={i}>
          <PaperRow paperClassName="py-0">
            <FeedSpace lines={feedLines} />
          </PaperRow>
          <CutRow right={i === content.length - 1 ? cutNote : undefined} />
          <TearEdge />
          {i < content.length - 1 && (
            <>
              <div className="h-5" />
              <TearEdge top />
            </>
          )}
        </div>,
      );
    } else if (block.type !== "CodePage") {
      rows.push(
        <PaperRow key={i} paperClassName="py-0.5">
          <StaticBlock block={block} />
        </PaperRow>,
      );
    }
    i += 1;
  }

  const endsWithCut = content.at(-1)?.type === "Cut";
  return (
    <>
      {rows}
      {!endsWithCut &&
        (autoCut ? (
          <>
            <PaperRow paperClassName="py-0">
              <FeedSpace lines={feedLines} />
            </PaperRow>
            <CutRow right={cutNote} />
            <TearEdge />
          </>
        ) : (
          <PaperRow paperClassName="h-24 [mask-image:linear-gradient(black,transparent)]" />
        ))}
    </>
  );
}
