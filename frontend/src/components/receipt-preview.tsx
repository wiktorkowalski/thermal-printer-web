import { useState } from "react";
import { ChevronDown, ChevronUp, Receipt } from "lucide-react";
import { Card } from "@/components/ui/card";
import type { ReceiptData, TaxRates } from "@/types/receipt";
import {
  calculateItemTotal,
  calculateSubtotal,
  calculateTaxBreakdown,
  calculateTotalTax,
  formatCurrency,
  formatQuantity,
  formatDate,
} from "@/lib/receipt-utils";

interface ReceiptPreviewProps {
  receipt: ReceiptData;
  taxRates: TaxRates;
}

export function ReceiptPreview({ receipt, taxRates }: ReceiptPreviewProps) {
  const [isExpanded, setIsExpanded] = useState(true);

  const { store, items, payment, title, date } = receipt;
  const subtotal = calculateSubtotal(items);
  const breakdown = calculateTaxBreakdown(items, taxRates);
  const totalTax = calculateTotalTax(breakdown);

  const hasContent = store.name || items.length > 0;

  if (!hasContent) return null;

  return (
    <Card className="overflow-hidden border-2">
      <button
        onClick={() => setIsExpanded(!isExpanded)}
        className="w-full flex items-center justify-between p-4 hover:bg-muted/50 transition-colors"
      >
        <div className="flex items-center gap-2">
          <Receipt className="h-4 w-4 text-[hsl(var(--terminal-green))]" />
          <span className="font-semibold text-sm">Receipt Preview</span>
        </div>
        {isExpanded ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
      </button>

      {isExpanded && (
        <div className="p-4 border-t-2">
          <div className="receipt-paper perforated-top rounded-lg shadow-lg p-4 max-w-[320px] mx-auto font-mono text-sm bg-white text-black">
            {/* Header */}
            <div className="text-center space-y-0.5">
              {store.name && (
                <div className="font-bold text-base tracking-wide">{store.name}</div>
              )}
              {store.addressLine1 && <div>{store.addressLine1}</div>}
              {store.addressLine2 && <div>{store.addressLine2}</div>}
              {store.nip && <div>NIP: {store.nip}</div>}
            </div>

            {/* Title */}
            <div className="my-3 text-center">
              <div className="font-bold text-lg tracking-widest">
                {title || 'PARAGON FISKALNY'}
              </div>
            </div>

            <div className="border-t border-dashed border-gray-400 my-2" />

            {/* Line items */}
            {items.length > 0 && (
              <div className="space-y-2">
                {items.map((item) => {
                  const total = calculateItemTotal(item);
                  return (
                    <div key={item.id}>
                      <div className="truncate">{item.name || '(no name)'}</div>
                      <div className="text-right text-xs">
                        {formatQuantity(item.quantity)} x {formatCurrency(item.unitPrice)} = {formatCurrency(total)} {item.taxCategory}
                      </div>
                    </div>
                  );
                })}
              </div>
            )}

            {items.length === 0 && (
              <div className="text-center text-gray-400 py-2">No items</div>
            )}

            <div className="border-t border-dashed border-gray-400 my-2" />

            {/* Tax breakdown */}
            {breakdown.length > 0 && (
              <div className="space-y-0.5 text-xs">
                {breakdown.map((line) => (
                  <div key={line.category}>
                    <div className="flex justify-between">
                      <span>Sp.op.{line.category}</span>
                      <span>{formatCurrency(line.base)}</span>
                    </div>
                    <div className="flex justify-between">
                      <span>PTU {line.category}={line.rate},00%</span>
                      <span>{formatCurrency(line.tax)}</span>
                    </div>
                  </div>
                ))}
                <div className="flex justify-between font-semibold">
                  <span>SUMA PTU</span>
                  <span>{formatCurrency(totalTax)}</span>
                </div>
              </div>
            )}

            <div className="border-t border-dashed border-gray-400 my-2" />

            {/* Total */}
            <div className="flex justify-between font-bold text-lg">
              <span>SUMA PLN</span>
              <span>{formatCurrency(subtotal)}</span>
            </div>

            <div className="border-t border-dashed border-gray-400 my-2" />

            {/* Payment */}
            <div className="text-center text-xs mb-1">ROZLICZENIE PLATNOSCI</div>
            <div className="space-y-0.5 text-xs">
              {(payment.method === 'cash' || payment.method === 'mixed') && (
                <div className="flex justify-between">
                  <span>ZAPLACONO GOTOWKA PLN</span>
                  <span>{formatCurrency(payment.cashAmount ?? (payment.method === 'cash' ? subtotal : 0))}</span>
                </div>
              )}
              {(payment.method === 'card' || payment.method === 'mixed') && (
                <div className="flex justify-between">
                  <span>ZAPLACONO KARTA PLN</span>
                  <span>{formatCurrency(payment.cardAmount ?? (payment.method === 'card' ? subtotal : 0))}</span>
                </div>
              )}
            </div>

            {/* Date */}
            <div className="text-center text-xs mt-3">
              {date || formatDate()}
            </div>

            {/* Cut line */}
            <div className="text-center text-xs opacity-40 mt-4">
              ✂ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ✂
            </div>
          </div>
        </div>
      )}
    </Card>
  );
}
