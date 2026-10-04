import type { CSSProperties, Ref } from "react";
import type { Alignment, PrintStyle, TextSize } from "@/types/printer";
import { CHARS_PER_LINE } from "@/lib/printer-constants";
import { DEFAULT_ALIGNMENT, DOTS_PER_CH, countPrintedLines, textMetrics } from "@/lib/paper";

// How much of the unused width sits left of the line.
const SLACK_LEFT = { Left: 0, Center: 0.5, Right: 1 } as const;

interface PaperTextProps {
  text: string;
  style?: PrintStyle[];
  size?: TextSize | null;
  alignment?: Alignment;
  placeholder?: string;
  /** When set, the text is edited in place through a transparent textarea. */
  onChange?: (text: string) => void;
  textareaRef?: Ref<HTMLTextAreaElement>;
  label?: string;
}

/**
 * Renders one ESC/POS text block at 1:1 scale. The glyph box is laid out at
 * `maxChars` columns and then scaled by the glyph size, which is how a size,
 * DoubleWidth and Font B behave on the printer. A full line can be narrower
 * than the 48ch printable width (9 glyphs at 5x): the alignment places the box.
 */
export function PaperText({ text, style = [], size, alignment = DEFAULT_ALIGNMENT, placeholder, onChange, textareaRef, label }: PaperTextProps) {
  const m = textMetrics(style, size);
  const shown = text || placeholder || "";
  const lines = countPrintedLines(shown, m.maxChars);
  const lineCh = m.lineDots / DOTS_PER_CH;
  const reverse = style.includes("ReverseMode");
  const slackCh = CHARS_PER_LINE.normal - m.maxChars * m.scaleX;

  const glyphStyle: CSSProperties = {
    position: "absolute",
    top: 0,
    left: `${slackCh * (SLACK_LEFT[alignment] ?? SLACK_LEFT.Center)}ch`,
    width: `${m.maxChars}ch`,
    height: `${(lines * lineCh) / m.scaleY}ch`,
    transform: `scale(${m.scaleX}, ${m.scaleY})`,
    transformOrigin: "top left",
    lineHeight: `${lineCh / m.scaleY}ch`,
    textAlign: alignment.toLowerCase() as CSSProperties["textAlign"],
    whiteSpace: "pre-wrap",
    wordBreak: "break-all",
    overflowWrap: "anywhere",
    fontWeight: style.includes("Bold") ? 500 : 400,
    fontStyle: style.includes("Italic") ? "italic" : "normal",
    textDecoration: style.includes("Underline") ? "underline" : "none",
    textUnderlineOffset: "0.2em",
    fontFamily: "inherit",
    fontSize: "inherit",
    color: "inherit",
    letterSpacing: 0,
    margin: 0,
    padding: 0,
    border: 0,
  };

  const mirror = (
    <div aria-hidden={onChange ? true : undefined} style={glyphStyle}>
      {text ? (
        reverse ? (
          <span className="bg-paper-ink text-paper" style={{ boxDecorationBreak: "clone", WebkitBoxDecorationBreak: "clone" }}>
            {text}
          </span>
        ) : (
          text
        )
      ) : (
        !onChange && placeholder && <span className="text-paper-faint">{placeholder}</span>
      )}
      {/* A trailing newline needs a character to take up its line. */}
      {text.endsWith("\n") && "​"}
    </div>
  );

  return (
    <div
      className="relative"
      style={{
        width: `${CHARS_PER_LINE.normal}ch`,
        height: `${lines * lineCh}ch`,
        transform: style.includes("UpsideDownMode") ? "rotate(180deg)" : undefined,
      }}
    >
      {mirror}
      {onChange && (
        <textarea
          ref={textareaRef}
          aria-label={label}
          value={text}
          placeholder={placeholder}
          spellCheck={false}
          onChange={(e) => onChange(e.target.value)}
          className="resize-none overflow-hidden bg-transparent caret-paper-ink outline-none placeholder:text-paper-faint selection:bg-accent/25"
          style={{ ...glyphStyle, color: "transparent" }}
        />
      )}
    </div>
  );
}
