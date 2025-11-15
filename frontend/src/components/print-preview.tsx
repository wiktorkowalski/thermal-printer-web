import { useState } from "react";
import { ChevronDown, ChevronUp, Printer } from "lucide-react";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";
import type { CustomPrintRequest } from "@/types/printer";

interface PrintPreviewProps {
  content?: CustomPrintRequest;
  simplePrint?: {
    name: string;
    message: string;
    imagePreview?: string;
  };
}

export function PrintPreview({ content, simplePrint }: PrintPreviewProps) {
  const [isExpanded, setIsExpanded] = useState(true);

  const hasContent = content?.content.length || simplePrint;

  if (!hasContent) return null;

  return (
    <Card className="overflow-hidden border-2">
      <button
        onClick={() => setIsExpanded(!isExpanded)}
        className="w-full flex items-center justify-between p-4 hover:bg-muted/50 transition-colors"
      >
        <div className="flex items-center gap-2">
          <Printer className="h-4 w-4 text-[hsl(var(--terminal-green))]" />
          <span className="font-semibold text-sm">Print Preview</span>
        </div>
        {isExpanded ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
      </button>

      {isExpanded && (
        <div className="p-4 border-t-2">
          {/* Receipt Paper Container */}
          <div className="receipt-paper perforated-top rounded-lg shadow-lg p-6 max-w-[320px] mx-auto">
            {/* Thermal Printer Header Effect */}
            <div className="text-center mb-4 pb-4 border-b border-dashed border-gray-400">
              <div className="font-mono text-xs opacity-60">
                ··· PRINT PREVIEW ···
              </div>
            </div>

            {/* Simple Print Content */}
            {simplePrint && (
              <div className="space-y-3 text-center">
                <div className="text-center opacity-40 my-1">
                  {'='.repeat(32)}
                </div>
                <div className="font-semibold">{simplePrint.name}</div>
                <div className="text-center opacity-40 my-1">
                  {'='.repeat(32)}
                </div>
                <div className="whitespace-pre-wrap">{simplePrint.message}</div>
                <div className="text-center opacity-40 my-1">
                  {'='.repeat(32)}
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

            {/* Custom Template Content */}
            {content && (
              <div className="space-y-3">
                {content.content.map((block, index) => {
                  const alignment = block.alignment?.toLowerCase() || 'left';
                  const alignClass =
                    alignment === 'center' ? 'text-center' :
                    alignment === 'right' ? 'text-right' : 'text-left';

                  return (
                    <div key={index} className={alignClass}>
                      {/* Text Block */}
                      {block.type === 'Text' && (
                        <div
                          className={cn(
                            "font-mono",
                            block.styles?.includes('Bold') && "font-bold",
                            block.styles?.includes('Italic') && "italic",
                            block.styles?.includes('Underline') && "underline",
                            block.styles?.includes('DoubleHeight') && "text-2xl leading-tight",
                            block.styles?.includes('DoubleWidth') && "tracking-wider",
                            block.styles?.includes('FontB') && "text-sm"
                          )}
                        >
                          {block.content}
                        </div>
                      )}

                      {/* Image Block */}
                      {block.type === 'Image' && block.base64Image && (
                        <img
                          src={`data:image/png;base64,${block.base64Image}`}
                          alt="Print image"
                          className="max-w-full rounded border border-gray-300 grayscale contrast-150 brightness-110"
                        />
                      )}

                      {/* Barcode Block */}
                      {block.type === 'Barcode' && (
                        <div className="py-2">
                          <div className="bg-white border-2 border-gray-800 p-2 inline-block">
                            <div className="font-mono text-xs text-center mb-1 text-black">
                              {block.barcodeType}
                            </div>
                            <div className="flex gap-[2px] justify-center">
                              {/* Simplified barcode visualization */}
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

                      {/* QR Code Block */}
                      {block.type === 'QRCode' && (
                        <div className="py-2 inline-block">
                          <div className="bg-white border-2 border-gray-800 p-3">
                            <div className="w-24 h-24 bg-black/10 rounded grid grid-cols-8 grid-rows-8 gap-[2px]">
                              {/* Simplified QR visualization */}
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

                      {/* Line Feed */}
                      {block.type === 'LineFeed' && (
                        <div style={{ height: `${(block.lines || 1) * 8}px` }} />
                      )}

                      {/* Separator */}
                      {block.type === 'Separator' && (
                        <div className="text-center opacity-40 my-1">
                          {(block.content || '─').repeat(block.length || 32)}
                        </div>
                      )}

                      {/* Cut Indicator */}
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

            {/* Receipt Footer */}
            <div className="text-center mt-6 pt-4 border-t border-dashed border-gray-400">
              <div className="font-mono text-xs opacity-60">
                ··· END OF PREVIEW ···
              </div>
            </div>
          </div>
        </div>
      )}
    </Card>
  );
}
