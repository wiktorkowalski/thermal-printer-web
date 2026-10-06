import type { PrintContent } from '../types/printer';
import { ContentType, Alignment, PrintStyle } from '../types/printer';
import type {
  ReceiptData,
  ReceiptItem,
  TaxRates,
  TaxBreakdownLine,
  StoreInfo,
  TaxCategory,
  ReceiptSerials,
  ReceiptTemplate,
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

/** What the customer pays for the item: gross total minus its rabat. */
export function calculateItemNet(item: ReceiptItem): number {
  return calculateItemTotal(item) - (item.discount ?? 0);
}

export function calculateSubtotal(items: ReceiptItem[]): number {
  return items.reduce((sum, item) => sum + calculateItemNet(item), 0);
}

/** Only the Biedronka layout prints a rabat; other templates ignore a leftover discount. */
export function itemsForTemplate(items: ReceiptItem[], template: ReceiptTemplate): ReceiptItem[] {
  return template === 'biedronka' ? items : items.map((item) => (item.discount ? { ...item, discount: undefined } : item));
}

export function calculateTaxBreakdown(items: ReceiptItem[], rates: TaxRates): TaxBreakdownLine[] {
  const byCategory = items.reduce((acc, item) => {
    const cat = item.taxCategory;
    if (!acc[cat]) {
      acc[cat] = { base: 0, rate: rates[cat] };
    }
    acc[cat].base += calculateItemNet(item);
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

/** Which printed blocks belong to which line item, so the UI can select them on paper. */
export interface ItemRange {
  id: string;
  start: number;
  end: number;
}

export interface ReceiptLayout {
  content: PrintContent[];
  items: ItemRange[];
}

export function receiptLayout(receipt: ReceiptData, taxRates: TaxRates): ReceiptLayout {
  return receipt.template === 'biedronka'
    ? biedronkaLayout(receipt, taxRates)
    : paragonLayout({ ...receipt, items: itemsForTemplate(receipt.items, 'paragon-fiskalny') }, taxRates);
}

function paragonLayout(receipt: ReceiptData, taxRates: TaxRates): ReceiptLayout {
  const content: PrintContent[] = [];
  const ranges: ItemRange[] = [];
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

  content.push({ type: ContentType.Separator, separatorChar: '-', separatorLength: CHARS_PER_LINE.normal });

  // Line items
  for (const item of items) {
    const start = content.length;
    const priceStr = paragonPriceText(item);

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
    ranges.push({ id: item.id, start, end: content.length });
  }

  content.push({ type: ContentType.Separator, separatorChar: '-', separatorLength: CHARS_PER_LINE.normal });

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

  content.push({ type: ContentType.Separator, separatorChar: '-', separatorLength: CHARS_PER_LINE.normal });

  // Total
  const subtotal = calculateSubtotal(items);
  content.push({
    type: ContentType.Text,
    content: formatLine('SUMA PLN', formatCurrency(subtotal), CHARS_PER_LINE.doubleWidth),
    style: [PrintStyle.Bold, PrintStyle.DoubleHeight, PrintStyle.DoubleWidth],
  });

  content.push({ type: ContentType.Separator, separatorChar: '-', separatorLength: CHARS_PER_LINE.normal });

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

  return { content, items: ranges };
}

function paragonPriceText(item: ReceiptItem): string {
  const total = calculateItemTotal(item);
  return `${formatQuantity(item.quantity)} SZT * ${formatCurrency(item.unitPrice)} = ${formatCurrency(total)} ${item.taxCategory}`;
}

/** Whether an item prints as one line (paragon) or its name line fits (Biedronka). */
export function estimateItemLine(item: ReceiptItem, biedronka: boolean): { fits: boolean } {
  if (biedronka) return { fits: item.name.length + 2 <= CHARS_PER_LINE.normal };
  return { fits: item.name.length + paragonPriceText(item).length + 1 <= CHARS_PER_LINE.normal };
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

function biedronkaLayout(receipt: ReceiptData, taxRates: TaxRates): ReceiptLayout {
  const content: PrintContent[] = [];
  const ranges: ItemRange[] = [];
  const serials = receipt.serials ?? newSerials();
  const { store, items, date, kasaNumber, kasjerNumber } = receipt;

  // Header - Biedronka style
  content.push({
    type: ContentType.Text,
    content: 'Biedronka',
    alignment: Alignment.Center,
    style: [PrintStyle.Bold, PrintStyle.DoubleWidth],
  });
  content.push({
    type: ContentType.Text,
    content: 'Codziennie niskie ceny',
    alignment: Alignment.Center,
  });
  content.push({ type: ContentType.LineFeed, lines: 1 });

  // Store address
  const storeNumStr = store.storeNumber ? `Sklep ${store.storeNumber} ` : '';
  content.push({
    type: ContentType.Text,
    content: `${storeNumStr}${store.addressLine1}`,
    alignment: Alignment.Center,
  });
  content.push({
    type: ContentType.Text,
    content: `${store.zipCode} ${store.city}`,
    alignment: Alignment.Center,
  });

  // Parent company
  if (store.parentCompany) {
    content.push({
      type: ContentType.Text,
      content: store.parentCompany,
      alignment: Alignment.Center,
    });
  }
  if (store.parentAddress) {
    content.push({
      type: ContentType.Text,
      content: store.parentAddress,
      alignment: Alignment.Center,
    });
  }

  // NIP
  content.push({
    type: ContentType.Text,
    content: `NIP ${store.nip}`,
    alignment: Alignment.Center,
  });

  content.push({ type: ContentType.LineFeed, lines: 1 });

  // Date and receipt number line
  const receiptNum = serials.receiptNumber;
  const dateStr = date || formatDate();
  content.push({
    type: ContentType.Text,
    content: formatLine(dateStr, receiptNum),
  });

  // PARAGON FISKALNY
  content.push({
    type: ContentType.Text,
    content: 'PARAGON FISKALNY',
    alignment: Alignment.Center,
    style: [PrintStyle.Bold],
  });

  // Items - Biedronka format
  for (const item of items) {
    const start = content.length;
    const total = calculateItemTotal(item);

    // First line: name + tax category
    const nameLine = formatLine(item.name, item.taxCategory);
    content.push({
      type: ContentType.Text,
      content: nameLine,
    });

    // Second line: qty x price = total + tax category
    const qtyStr = formatQuantity(item.quantity);
    const priceStr = formatCurrency(item.unitPrice);
    const totalStr = formatCurrency(total);
    const priceLine = `${qtyStr} x${priceStr} ${totalStr}${item.taxCategory}`;
    content.push({
      type: ContentType.Text,
      content: priceLine,
      alignment: Alignment.Right,
    });

    // Rabat line if discount exists
    if (item.discount && item.discount > 0) {
      content.push({
        type: ContentType.Text,
        content: formatLine('   Rabat', `-${formatCurrency(item.discount)}`),
      });
      content.push({
        type: ContentType.Text,
        content: formatLine('', `${formatCurrency(calculateItemNet(item))}${item.taxCategory}`),
      });
    }
    ranges.push({ id: item.id, start, end: content.length });
  }

  content.push({ type: ContentType.Separator, separatorChar: '.', separatorLength: CHARS_PER_LINE.normal });

  // Tax breakdown - Biedronka style
  const breakdown = calculateTaxBreakdown(items, taxRates);
  for (const line of breakdown) {
    const grossAmount = line.base + line.tax;
    content.push({
      type: ContentType.Text,
      content: formatLine(`SPRZEDAŻ OPODATKOWANA ${line.category}`, formatCurrency(grossAmount)),
    });
    content.push({
      type: ContentType.Text,
      content: formatLine(`PTU ${line.category} ${line.rate},00 %`, formatCurrency(line.tax)),
    });
  }

  const totalTax = calculateTotalTax(breakdown);
  content.push({
    type: ContentType.Text,
    content: formatLine('SUMA PTU', formatCurrency(totalTax)),
  });

  // Total
  const totalAfterDiscount = calculateSubtotal(items);
  content.push({
    type: ContentType.Text,
    content: formatLine('SUMA PLN', formatCurrency(totalAfterDiscount), CHARS_PER_LINE.doubleWidth),
    style: [PrintStyle.Bold, PrintStyle.DoubleHeight, PrintStyle.DoubleWidth],
  });

  // Footer - Kasa/Kasjer info
  const kasa = kasaNumber || '3';
  const kasjer = kasjerNumber || '9';
  const footerNum = serials.footerNumber;
  content.push({
    type: ContentType.Text,
    content: formatLine(`${footerNum} #Kasa ${kasa} Kasjer nr ${kasjer}`, dateStr),
  });

  // Fiscal code (random hex)
  const fiscalCode = serials.fiscalCode;
  content.push({
    type: ContentType.Text,
    content: fiscalCode,
    alignment: Alignment.Center,
    style: [PrintStyle.FontB],
  });

  // CCH number
  const cchNum = serials.cchNumber;
  content.push({
    type: ContentType.Text,
    content: `CCH ${cchNum}`,
    alignment: Alignment.Center,
  });

  // No barcode: the 22 digits of the real receipt are 1108 dots wide in CODE128 code set B, the only set that the
  // server sends. The printer dropped that barcode with no error (issue #44); the server now rejects one over 576 dots.

  // Nr sys
  content.push({
    type: ContentType.Text,
    content: 'Nr sys. 9950',
    alignment: Alignment.Center,
  });

  // Feed and cut
  content.push({ type: ContentType.LineFeed, lines: 3 });
  content.push({ type: ContentType.Cut });

  return { content, items: ranges };
}

/** Fake fiscal serials for the Biedronka layout. Kept in state so the preview does not flicker. */
export function newSerials(): ReceiptSerials {
  return {
    receiptNumber: Math.floor(Math.random() * 900000 + 100000).toString(),
    footerNumber: Math.floor(Math.random() * 90000 + 10000).toString().padStart(5, '0'),
    fiscalCode: Array.from({ length: 40 }, () => '0123456789ABCDEF'[Math.floor(Math.random() * 16)]).join(''),
    cchNumber: Math.floor(Math.random() * 9000000000 + 1000000000).toString(),
  };
}
