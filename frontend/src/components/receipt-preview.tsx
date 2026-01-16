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
import { CHARS_PER_LINE } from "@/lib/printer-constants";

interface ReceiptPreviewProps {
  receipt: ReceiptData;
  taxRates: TaxRates;
}

export function ReceiptPreview({ receipt, taxRates }: ReceiptPreviewProps) {
  const [isExpanded, setIsExpanded] = useState(true);

  const { store, items, payment, title, date, template } = receipt;
  const subtotal = calculateSubtotal(items);
  const breakdown = calculateTaxBreakdown(items, taxRates);
  const totalTax = calculateTotalTax(breakdown);
  const isBiedronka = template === 'biedronka';
  const displayDate = date || formatDate();

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
            {isBiedronka ? (
              // Biedronka template
              <>
                {/* Header - Biedronka style */}
                <div className="text-center space-y-0.5">
                  <div className="font-bold text-base tracking-wide">Biedronka</div>
                  <div className="text-xs">Codziennie niskie ceny</div>
                </div>

                <div className="text-center space-y-0.5 mt-2 text-xs">
                  <div>
                    {store.storeNumber ? `Sklep ${store.storeNumber} ` : ''}{store.addressLine1}
                  </div>
                  <div>{store.zipCode} {store.city}</div>
                  {store.parentCompany && <div>{store.parentCompany}</div>}
                  {store.parentAddress && <div>{store.parentAddress}</div>}
                  <div>NIP {store.nip}</div>
                </div>

                {/* Date line */}
                <div className="flex justify-between text-xs mt-2">
                  <span>{displayDate}</span>
                  <span>222162</span>
                </div>

                {/* Title */}
                <div className="my-2 text-center">
                  <div className="font-bold">PARAGON FISKALNY</div>
                </div>

                {/* Line items - Biedronka format */}
                {items.length > 0 && (
                  <div className="space-y-1">
                    {items.map((item) => {
                      const total = calculateItemTotal(item) - (item.discount || 0);
                      const name = item.name || '(no name)';

                      return (
                        <div key={item.id} className="text-xs">
                          <div className="flex justify-between">
                            <span className="truncate">{name}</span>
                            <span>{item.taxCategory}</span>
                          </div>
                          <div className="text-right">
                            {formatQuantity(item.quantity)} x{formatCurrency(item.unitPrice)} {formatCurrency(total)}{item.taxCategory}
                          </div>
                          {item.discount && item.discount > 0 && (
                            <>
                              <div className="flex justify-between">
                                <span className="pl-4">Rabat</span>
                                <span>-{formatCurrency(item.discount)}</span>
                              </div>
                              <div className="text-right">{formatCurrency(total)}{item.taxCategory}</div>
                            </>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}

                {items.length === 0 && (
                  <div className="text-center text-gray-400 py-2">No items</div>
                )}

                <div className="border-t border-dotted border-gray-400 my-2" />

                {/* Tax breakdown - Biedronka style */}
                {breakdown.length > 0 && (
                  <div className="space-y-0.5 text-xs">
                    {breakdown.map((line) => {
                      const gross = line.base + line.tax;
                      return (
                        <div key={line.category}>
                          <div className="flex justify-between">
                            <span>SPRZEDAŻ OPODATKOWANA {line.category}</span>
                            <span>{formatCurrency(gross)}</span>
                          </div>
                          <div className="flex justify-between">
                            <span>PTU {line.category} {line.rate},00 %</span>
                            <span>{formatCurrency(line.tax)}</span>
                          </div>
                        </div>
                      );
                    })}
                    <div className="flex justify-between">
                      <span>SUMA PTU</span>
                      <span>{formatCurrency(totalTax)}</span>
                    </div>
                  </div>
                )}

                {/* Total */}
                <div className="flex justify-between font-bold text-lg mt-2">
                  <span>SUMA PLN</span>
                  <span>{formatCurrency(subtotal)}</span>
                </div>

                {/* Footer */}
                <div className="text-xs mt-2 space-y-1">
                  <div className="flex justify-between">
                    <span>00049 #Kasa 3 Kasjer nr 9</span>
                    <span>{displayDate}</span>
                  </div>
                  <div className="text-center text-[10px] break-all">
                    FA7DF8F64F93123AB227453F5827CBBAF0B4ACBA
                  </div>
                  <div className="text-center">CCH 1701372836</div>
                  <div className="text-center mt-2 py-2 border-2 border-black">
                    <div className="text-xs">||||||||||||||||||||||||</div>
                    <div className="text-[10px]">Nr sys. 9950</div>
                  </div>
                </div>

                {/* Cut line */}
                <div className="text-center text-xs opacity-40 mt-4">
                  ✂ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ✂
                </div>
              </>
            ) : (
              // Standard Paragon Fiskalny template
              <>
                {/* Header */}
                <div className="text-center space-y-0.5">
                  {store.name && (
                    <div className="font-bold text-base tracking-wide">{store.name}</div>
                  )}
                  {store.addressLine1 && <div>{store.addressLine1}</div>}
                  {(store.city || store.zipCode) && <div>{store.zipCode} {store.city}</div>}
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
                  <div className="space-y-1">
                    {items.map((item) => {
                      const total = calculateItemTotal(item);
                      const name = item.name || '(no name)';
                      const priceStr = `${formatQuantity(item.quantity)} SZT * ${formatCurrency(item.unitPrice)} = ${formatCurrency(total)} ${item.taxCategory}`;
                      const fitsOneLine = name.length + priceStr.length + 1 <= CHARS_PER_LINE.normal;

                      return fitsOneLine ? (
                        <div key={item.id} className="flex justify-between text-xs">
                          <span className="truncate">{name}</span>
                          <span className="ml-2 whitespace-nowrap">{priceStr}</span>
                        </div>
                      ) : (
                        <div key={item.id}>
                          <div className="truncate text-xs">{name}</div>
                          <div className="text-right text-xs">{priceStr}</div>
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
                  {displayDate}
                </div>

                {/* Cut line */}
                <div className="text-center text-xs opacity-40 mt-4">
                  ✂ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ✂
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </Card>
  );
}
