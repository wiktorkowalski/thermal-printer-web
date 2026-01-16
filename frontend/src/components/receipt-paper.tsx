import { cn } from "@/lib/utils";
import type { PrintContent } from "@/types/printer";
import { DEFAULT_LINE_WIDTH } from "@/lib/printer-constants";

interface SimplePrint {
  name: string;
  message: string;
  imagePreview?: string;
}

interface ReceiptPaperProps {
  content?: PrintContent[];
  simplePrint?: SimplePrint;
  className?: string;
}

export function ReceiptPaper({ content, simplePrint, className }: ReceiptPaperProps) {
  return (
    <div
      className={cn(
        "receipt-paper perforated-top rounded-lg shadow-lg px-2 py-4",
        className
      )}
      style={{ fontFamily: 'ui-monospace, monospace', width: '384px', fontSize: '12px' }}
    >
      {simplePrint && (
        <div className="space-y-3 text-center">
          <div className="text-center opacity-40 my-1">
            {'='.repeat(DEFAULT_LINE_WIDTH)}
          </div>
          <div className="font-semibold">{simplePrint.name}</div>
          <div className="text-center opacity-40 my-1">
            {'='.repeat(DEFAULT_LINE_WIDTH)}
          </div>
          <div className="whitespace-pre-wrap">{simplePrint.message}</div>
          <div className="text-center opacity-40 my-1">
            {'='.repeat(DEFAULT_LINE_WIDTH)}
          </div>
          {simplePrint.imagePreview && (
            <img
              src={simplePrint.imagePreview}
              alt="Print preview"
              className="w-full rounded border border-gray-300 grayscale contrast-150 brightness-110"
            />
          )}
        </div>
      )}

      {content && (
        <div className="space-y-3">
          {content.map((block, index) => {
            const alignment = block.alignment?.toLowerCase() || 'left';
            const alignClass =
              alignment === 'center' ? 'text-center' :
              alignment === 'right' ? 'text-right' : 'text-left';

            return (
              <div key={index} className={alignClass}>
                {block.type === 'Text' && (() => {
                  const hasFontB = block.style?.includes('FontB');
                  const hasDoubleHeight = block.style?.includes('DoubleHeight');

                  let fontSize = hasFontB ? 9 : 12;
                  if (hasDoubleHeight) fontSize *= 2;

                  return (
                    <div
                      className={cn(
                        "font-mono break-words",
                        block.style?.includes('Bold') && "font-bold",
                        block.style?.includes('Italic') && "italic",
                        block.style?.includes('Underline') && "underline",
                        block.style?.includes('ReverseMode') && "bg-black text-white px-1",
                        block.style?.includes('UpsideDownMode') && "rotate-180"
                      )}
                      style={{ fontSize: `${fontSize}px`, lineHeight: 1.2 }}
                    >
                      {block.content}
                    </div>
                  );
                })()}

                {block.type === 'Image' && block.content && (
                  <img
                    src={`data:image/png;base64,${block.content}`}
                    alt="Print image"
                    className="max-w-full rounded border border-gray-300 grayscale contrast-150 brightness-110"
                  />
                )}

                {block.type === 'Barcode' && (
                  <div className="py-2">
                    <div className="bg-white border-2 border-gray-800 p-2 inline-block">
                      <div className="font-mono text-xs text-center mb-1 text-black">
                        {block.barcodeOptions?.type || 'CODE128'}
                      </div>
                      <div className="flex gap-[2px] justify-center">
                        {Array.from({ length: 12 }).map((_, i) => (
                          <div
                            key={i}
                            className="w-[3px] h-12 bg-black"
                            style={{
                              opacity: Math.random() > 0.3 ? 1 : 0,
                              height: Math.random() > 0.5 ? '48px' : '40px'
                            }}
                          />
                        ))}
                      </div>
                      <div className="font-mono text-xs text-center mt-1 text-black">
                        {block.content}
                      </div>
                    </div>
                  </div>
                )}

                {block.type === 'QRCode' && (
                  <div className="py-2 inline-block">
                    <div className="bg-white border-2 border-gray-800 p-3">
                      <div className="w-24 h-24 bg-black/10 rounded grid grid-cols-8 grid-rows-8 gap-[2px]">
                        {Array.from({ length: 64 }).map((_, i) => (
                          <div
                            key={i}
                            className={cn(
                              "rounded-sm",
                              Math.random() > 0.5 ? "bg-black" : "bg-white"
                            )}
                          />
                        ))}
                      </div>
                      <div className="font-mono text-[10px] text-center mt-2 text-black opacity-60">
                        QR: {block.content?.substring(0, 15)}...
                      </div>
                    </div>
                  </div>
                )}

                {block.type === 'LineFeed' && (
                  <div style={{ height: `${(block.lines || 1) * 8}px` }} />
                )}

                {block.type === 'Separator' && (() => {
                  const hasFontB = block.style?.includes('FontB');
                  const hasDoubleHeight = block.style?.includes('DoubleHeight');

                  let fontSize = hasFontB ? 9 : 12;
                  if (hasDoubleHeight) fontSize *= 2;

                  return (
                    <div
                      className={cn(
                        "text-center opacity-40 my-1 overflow-hidden font-mono",
                        block.style?.includes('Bold') && "font-bold",
                        block.style?.includes('ReverseMode') && "bg-black text-white px-1 opacity-100"
                      )}
                      style={{ fontSize: `${fontSize}px`, lineHeight: 1.2 }}
                    >
                      {(block.separatorChar || '─').repeat(block.separatorLength || DEFAULT_LINE_WIDTH)}
                    </div>
                  );
                })()}

                {block.type === 'Cut' && (
                  <div className="text-center text-xs opacity-40 my-2">
                    ✂ ─ ─ ─ ─ ─ ─ CUT HERE ─ ─ ─ ─ ─ ─ ✂
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
