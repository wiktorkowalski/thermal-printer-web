import type { PrintContent } from '../types/printer';
import { ContentType, Alignment, PrintStyle } from '../types/printer';
import type {
  ReceiptData,
  ReceiptItem,
  TaxRates,
  TaxBreakdownLine,
  StoreInfo,
  TaxCategory,
} from '../types/receipt';
import { DEFAULT_TAX_RATES, DEFAULT_STORE } from '../types/receipt';
import { CHARS_PER_LINE } from './printer-constants';

// Storage keys
const STORAGE_KEYS = {
  STORE: 'receipt-store-defaults',
  TAX_RATES: 'receipt-tax-rates',
} as const;

// Calculations
export function calculateItemTotal(item: ReceiptItem): number {
  return item.quantity * item.unitPrice;
}

export function calculateSubtotal(items: ReceiptItem[]): number {
  return items.reduce((sum, item) => sum + calculateItemTotal(item), 0);
}

export function calculateTaxBreakdown(items: ReceiptItem[], rates: TaxRates): TaxBreakdownLine[] {
  const byCategory = items.reduce((acc, item) => {
    const cat = item.taxCategory;
    if (!acc[cat]) {
      acc[cat] = { base: 0, rate: rates[cat] };
    }
    acc[cat].base += calculateItemTotal(item);
    return acc;
  }, {} as Record<TaxCategory, { base: number; rate: number }>);

  return (Object.entries(byCategory) as [TaxCategory, { base: number; rate: number }][])
    .map(([category, { base, rate }]) => {
      // Polish tax: price is gross, tax = gross - (gross / (1 + rate/100))
      const netBase = base / (1 + rate / 100);
      const tax = base - netBase;
      return {
        category,
        rate,
        base: netBase,
        tax,
      };
    })
    .filter(line => line.base > 0)
    .sort((a, b) => a.category.localeCompare(b.category));
}

export function calculateTotalTax(breakdown: TaxBreakdownLine[]): number {
  return breakdown.reduce((sum, line) => sum + line.tax, 0);
}

// Formatting
export function formatCurrency(amount: number): string {
  return amount.toFixed(2).replace('.', ',');
}

export function formatQuantity(qty: number): string {
  return qty % 1 === 0 ? qty.toString() : qty.toFixed(3).replace('.', ',');
}

export function formatDate(date?: Date): string {
  const d = date || new Date();
  const day = d.getDate().toString().padStart(2, '0');
  const month = (d.getMonth() + 1).toString().padStart(2, '0');
  const year = d.getFullYear();
  const hours = d.getHours().toString().padStart(2, '0');
  const minutes = d.getMinutes().toString().padStart(2, '0');
  return `${day}.${month}.${year} ${hours}:${minutes}`;
}

// Format left and right text on same line with padding
export function formatLine(left: string, right: string, width: number = CHARS_PER_LINE.normal): string {
  const padding = Math.max(1, width - left.length - right.length);
  return left + ' '.repeat(padding) + right;
}

// LocalStorage persistence
export function getDefaultStore(): StoreInfo {
  try {
    const stored = localStorage.getItem(STORAGE_KEYS.STORE);
    if (stored) {
      return JSON.parse(stored);
    }
  } catch {
    // ignore
  }
  return { ...DEFAULT_STORE };
}

export function saveDefaultStore(store: StoreInfo): void {
  try {
    localStorage.setItem(STORAGE_KEYS.STORE, JSON.stringify(store));
  } catch {
    // ignore quota errors
  }
}

export function getTaxRates(): TaxRates {
  try {
    const stored = localStorage.getItem(STORAGE_KEYS.TAX_RATES);
    if (stored) {
      return JSON.parse(stored);
    }
  } catch {
    // ignore
  }
  return { ...DEFAULT_TAX_RATES };
}

export function saveTaxRates(rates: TaxRates): void {
  try {
    localStorage.setItem(STORAGE_KEYS.TAX_RATES, JSON.stringify(rates));
  } catch {
    // ignore quota errors
  }
}

// Convert receipt to PrintContent[] for API
export function receiptToContent(receipt: ReceiptData, taxRates: TaxRates): PrintContent[] {
  const content: PrintContent[] = [];
  const { store, items, payment, title, date } = receipt;

  // Header - Store info (centered)
  if (store.name) {
    content.push({
      type: ContentType.Text,
      content: store.name,
      alignment: Alignment.Center,
      style: [PrintStyle.DoubleWidth],
    });
  }
  if (store.addressLine1) {
    content.push({
      type: ContentType.Text,
      content: store.addressLine1,
      alignment: Alignment.Center,
    });
  }
  if (store.city || store.zipCode) {
    content.push({
      type: ContentType.Text,
      content: `${store.zipCode} ${store.city}`.trim(),
      alignment: Alignment.Center,
    });
  }
  if (store.nip) {
    content.push({
      type: ContentType.Text,
      content: `NIP: ${store.nip}`,
      alignment: Alignment.Center,
    });
  }

  // Separator
  content.push({ type: ContentType.LineFeed, lines: 1 });

  // Title
  content.push({
    type: ContentType.Text,
    content: title || 'PARAGON FISKALNY',
    alignment: Alignment.Center,
    style: [PrintStyle.Bold, PrintStyle.DoubleHeight, PrintStyle.DoubleWidth],
  });

  content.push({ type: ContentType.Separator, separatorChar: '-', separatorLength: 32 });

  // Line items
  for (const item of items) {
    const total = calculateItemTotal(item);
    const priceStr = `${formatQuantity(item.quantity)} SZT * ${formatCurrency(item.unitPrice)} = ${formatCurrency(total)} ${item.taxCategory}`;

    // Try to fit on one line, otherwise split
    if (item.name.length + priceStr.length + 1 <= CHARS_PER_LINE.normal) {
      // Fits on one line
      content.push({
        type: ContentType.Text,
        content: formatLine(item.name, priceStr),
      });
    } else {
      // Split into two lines
      content.push({
        type: ContentType.Text,
        content: item.name,
        alignment: Alignment.Left,
      });
      content.push({
        type: ContentType.Text,
        content: priceStr,
        alignment: Alignment.Right,
      });
    }
  }

  content.push({ type: ContentType.Separator, separatorChar: '-', separatorLength: 32 });

  // Tax breakdown
  const breakdown = calculateTaxBreakdown(items, taxRates);
  for (const line of breakdown) {
    content.push({
      type: ContentType.Text,
      content: formatLine(`Sp.op.${line.category}`, formatCurrency(line.base)),
    });
    content.push({
      type: ContentType.Text,
      content: formatLine(`PTU ${line.category}=${line.rate},00%`, formatCurrency(line.tax)),
    });
  }

  const totalTax = calculateTotalTax(breakdown);
  content.push({
    type: ContentType.Text,
    content: formatLine('SUMA PTU', formatCurrency(totalTax)),
  });

  content.push({ type: ContentType.Separator, separatorChar: '-', separatorLength: 32 });

  // Total
  const subtotal = calculateSubtotal(items);
  content.push({
    type: ContentType.Text,
    content: formatLine('SUMA PLN', formatCurrency(subtotal), CHARS_PER_LINE.doubleWidth),
    style: [PrintStyle.Bold, PrintStyle.DoubleHeight, PrintStyle.DoubleWidth],
  });

  content.push({ type: ContentType.Separator, separatorChar: '-', separatorLength: 32 });

  // Payment
  content.push({
    type: ContentType.Text,
    content: 'ROZLICZENIE PLATNOSCI',
    alignment: Alignment.Center,
  });

  if (payment.method === 'cash' || payment.method === 'mixed') {
    const cashAmt = payment.cashAmount ?? (payment.method === 'cash' ? subtotal : 0);
    content.push({
      type: ContentType.Text,
      content: formatLine('ZAPLACONO GOTOWKA PLN', formatCurrency(cashAmt)),
    });
  }

  if (payment.method === 'card' || payment.method === 'mixed') {
    const cardAmt = payment.cardAmount ?? (payment.method === 'card' ? subtotal : 0);
    content.push({
      type: ContentType.Text,
      content: formatLine('ZAPLACONO KARTA PLN', formatCurrency(cardAmt)),
    });
  }

  // Date/time
  content.push({ type: ContentType.LineFeed, lines: 1 });
  content.push({
    type: ContentType.Text,
    content: date || formatDate(),
    alignment: Alignment.Center,
  });

  // Feed and cut
  content.push({ type: ContentType.LineFeed, lines: 3 });
  content.push({ type: ContentType.Cut });

  return content;
}

// Create new empty item
export function createEmptyItem(): ReceiptItem {
  return {
    id: crypto.randomUUID(),
    name: '',
    quantity: 1,
    unitPrice: 0,
    taxCategory: 'A',
  };
}
