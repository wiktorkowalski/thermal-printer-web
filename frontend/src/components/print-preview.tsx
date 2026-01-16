import { useState } from "react";
import { ChevronDown, ChevronUp, Printer } from "lucide-react";
import { Card } from "@/components/ui/card";
import { ReceiptPaper } from "@/components/receipt-paper";
import type { PrintRequest } from "@/types/printer";

interface PrintPreviewProps {
  content?: PrintRequest;
  simplePrint?: {
    name: string;
    message: string;
    imagePreview?: string;
  };
}

export function PrintPreview({ content, simplePrint }: PrintPreviewProps) {
  const [isExpanded, setIsExpanded] = useState(true);

  const hasContent = (content?.content?.length ?? 0) > 0 || simplePrint;

  if (!hasContent) return null;

  return (
    <Card className="border-2">
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
        <div className="p-4 border-t-2 overflow-x-auto">
          <ReceiptPaper
            content={content?.content}
            simplePrint={simplePrint}
            className="mx-auto"
          />
        </div>
      )}
    </Card>
  );
}
