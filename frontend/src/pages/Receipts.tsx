import { useEffect, useRef, useState } from "react";
import { ArrowDown, ArrowUp, Pencil, Plus, Store, Trash2 } from "lucide-react";
import { cn } from "@/lib/utils";
import type { PaymentMethod, ReceiptData, ReceiptItem, ReceiptSerials, ReceiptTemplate, StoreInfo, TaxCategory, TaxRates } from "@/types/receipt";
import { BIEDRONKA_STORE } from "@/types/receipt";
import {
  calculateItemNet,
  calculateItemTotal,
  calculateSubtotal,
  calculateTaxBreakdown,
  calculateTotalTax,
  createEmptyItem,
  estimateItemLine,
  formatCurrency,
  getDefaultStore,
  getTaxRates,
  itemsForTemplate,
  newSerials,
  receiptLayout,
  saveDefaultStore,
  saveTaxRates,
} from "@/lib/receipt-utils";
import { dotsToMm, estimatePaperDots } from "@/lib/paper";
import { contentError } from "@/editor/document";
import { isBlocked, printerLight } from "@/lib/printer-light";
import type { PrinterStatusState } from "@/hooks/use-printer-status";
import { usePrintJob } from "@/hooks/use-print-job";
import { useIsDesktop } from "@/hooks/use-is-desktop";
import { PaperDocument } from "@/components/paper/paper-document";
import { PaperRow, PaperRuler, PrinterBody } from "@/components/paper/paper-strip";
import { PrintDock, PrinterAlert, Toast, roundButtonClass } from "@/components/print-chrome";
import { Sheet } from "@/components/sheet";
import { SectionLabel, Segmented, fieldLabelClass, inputClass, quietButtonClass } from "@/components/editor/controls";

// No feed from the options: a receipt ends with its own 3 empty lines and a Cut block (lib/receipt-utils.ts).
const PRINT_OPTIONS = { codePage: "PC852", autoCut: true, feedLinesAfterPrint: 0 };
const TAX_CATEGORIES: TaxCategory[] = ["A", "B", "C", "D"];

const DRAFT_KEY = "thermal-printer-draft-receipt";

interface ReceiptDraft {
  template: ReceiptTemplate;
  store: StoreInfo;
  items: ReceiptItem[];
  payment: PaymentMethod;
  cashAmount: string;
  cardAmount: string;
}

function loadReceiptDraft(): ReceiptDraft | null {
  try {
    const draft = JSON.parse(localStorage.getItem(DRAFT_KEY) ?? "null") as ReceiptDraft | null;
    return draft?.items?.length ? draft : null;
  } catch {
    return null;
  }
}

/** Accepts "18,99" and "18.99". */
/** Whole percent 0-99: the receipt prints rates as `23,00 %`. */
function parseRate(value: string): number {
  return Math.min(99, parseInt(value.replace(/\D/g, ""), 10) || 0);
}

function parseAmount(value: string): number {
  return parseFloat(value.replace(",", ".")) || 0;
}

export default function Receipts({ printer }: { printer: PrinterStatusState }) {
  const [draft] = useState(loadReceiptDraft);
  const [template, setTemplate] = useState<ReceiptTemplate>(draft?.template ?? "paragon-fiskalny");
  const [store, setStore] = useState<StoreInfo>(() => draft?.store ?? getDefaultStore());
  const [taxRates, setTaxRates] = useState<TaxRates>(getTaxRates);
  const [items, setItems] = useState<ReceiptItem[]>(() => draft?.items ?? [createEmptyItem()]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [payment, setPayment] = useState<PaymentMethod>(draft?.payment ?? "card");
  const [cashAmount, setCashAmount] = useState(draft?.cashAmount ?? "");
  const [cardAmount, setCardAmount] = useState(draft?.cardAmount ?? "");
  const [serials, setSerials] = useState<ReceiptSerials>(newSerials);
  const [sheet, setSheet] = useState<"item" | "store" | null>(null);
  const [confirmClear, setConfirmClear] = useState(false);
  const nameRef = useRef<HTMLInputElement>(null);
  const job = usePrintJob(printer);
  const desktop = useIsDesktop();

  // Store details double as defaults for the next visit (paragon only).
  useEffect(() => {
    if (template !== "paragon-fiskalny") return;
    const timer = window.setTimeout(() => saveDefaultStore(store), 800);
    return () => window.clearTimeout(timer);
  }, [store, template]);

  useEffect(() => saveTaxRates(taxRates), [taxRates]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      try {
        localStorage.setItem(DRAFT_KEY, JSON.stringify({ template, store, items, payment, cashAmount, cardAmount } satisfies ReceiptDraft));
      } catch {
        // Storage full or blocked: the draft is lost on reload, nothing else breaks.
      }
    }, 400);
    return () => window.clearTimeout(timer);
  }, [template, store, items, payment, cashAmount, cardAmount]);

  useEffect(() => {
    if (!confirmClear) return;
    const timer = window.setTimeout(() => setConfirmClear(false), 3000);
    return () => window.clearTimeout(timer);
  }, [confirmClear]);

  // A row with neither name nor price is a blank left for typing; it never prints.
  const filled = itemsForTemplate(
    items.filter((i) => i.name.trim() !== "" || i.unitPrice > 0),
    template,
  );
  const subtotal = calculateSubtotal(filled);
  const totalTax = calculateTotalTax(calculateTaxBreakdown(filled, taxRates));

  const receipt = (list: ReceiptItem[]): ReceiptData => ({
    store,
    items: list,
    template,
    serials,
    payment: {
      method: payment,
      cashAmount: payment !== "card" ? parseAmount(cashAmount) || subtotal : undefined,
      cardAmount: payment !== "cash" ? parseAmount(cardAmount) || subtotal : undefined,
    },
  });

  // The paper shows unnamed items too, so a new row is visible and clickable.
  const preview = receiptLayout(receipt(items.map((i) => (i.name.trim() ? i : { ...i, name: "[new item]" }))), taxRates);
  const lengthMm = dotsToMm(estimatePaperDots(preview.content, PRINT_OPTIONS));
  const selectedIndex = items.findIndex((i) => i.id === selectedId);
  const selected = selectedIndex >= 0 ? items[selectedIndex] : null;

  const updateItem = (id: string, patch: Partial<ReceiptItem>) => setItems((prev) => prev.map((i) => (i.id === id ? { ...i, ...patch } : i)));

  const addItem = () => {
    const item = createEmptyItem();
    setItems((prev) => [...prev, item]);
    setSelectedId(item.id);
    if (!desktop) setSheet("item");
    window.setTimeout(() => nameRef.current?.focus(), 0);
  };

  const removeItem = (id: string) => {
    const index = items.findIndex((i) => i.id === id);
    const next = items.length > 1 ? items.filter((i) => i.id !== id) : [createEmptyItem()];
    setItems(next);
    setSelectedId(next[Math.min(index, next.length - 1)]?.id ?? null);
  };

  const step = (delta: number) => {
    const next = items[selectedIndex + delta];
    if (next) setSelectedId(next.id);
  };

  const changeTemplate = (next: ReceiptTemplate) => {
    setTemplate(next);
    setStore(next === "biedronka" ? BIEDRONKA_STORE : getDefaultStore());
  };

  const print = async () => {
    if (filled.length === 0) {
      job.notify("Add at least one item with a name.", "error");
      return;
    }
    const unnamed = filled.find((i) => !i.name.trim());
    if (unnamed) {
      setSelectedId(unnamed.id);
      job.notify(`Item ${items.indexOf(unnamed) + 1} has a price but no name.`, "error");
      return;
    }
    const layout = receiptLayout(receipt(filled), taxRates);
    // Selects the item that owns a block of the sent content and returns its name on the paper.
    const locate = (index: number) => {
      const owner = layout.items.find((range) => index >= range.start && index < range.end);
      if (!owner) return undefined;
      setSelectedId(owner.id);
      return `Item ${items.findIndex((i) => i.id === owner.id) + 1}`;
    };
    const invalid = contentError(layout.content, PRINT_OPTIONS, estimatePaperDots(layout.content, PRINT_OPTIONS), locate);
    if (invalid) {
      job.notify(invalid, "error");
      return;
    }
    const request = { content: layout.content, options: PRINT_OPTIONS, source: "web/receipt" };
    const ok = await job.print(request, locate);
    // A new receipt gets new fiscal numbers.
    if (ok) setSerials(newSerials());
  };

  const light = printerLight(printer, job.phase);

  const itemEditor = selected && (
    <ItemInspector
      item={selected}
      index={selectedIndex}
      count={items.length}
      biedronka={template === "biedronka"}
      taxRates={taxRates}
      nameRef={nameRef}
      onChange={(patch) => updateItem(selected.id, patch)}
      onRemove={() => removeItem(selected.id)}
      onStep={step}
    />
  );

  const storePanel = (
    <StorePanel
      template={template}
      store={store}
      payment={payment}
      cashAmount={cashAmount}
      cardAmount={cardAmount}
      subtotal={subtotal}
      taxRates={taxRates}
      onTemplate={changeTemplate}
      onStore={(patch) => setStore((prev) => ({ ...prev, ...patch }))}
      onPayment={setPayment}
      onTaxRate={(category, rate) => setTaxRates((prev) => ({ ...prev, [category]: rate }))}
      onCash={setCashAmount}
      onCard={setCardAmount}
    />
  );

  return (
    <div className="stage-glow mx-auto grid max-w-[1600px] grid-cols-1 lg:grid-cols-[300px_minmax(0,1fr)_300px] xl:gap-4">
      {desktop && <aside className="sticky top-[72px] h-[calc(100vh-72px)] overflow-y-auto px-6 py-6">{storePanel}</aside>}

      <main className="flex min-w-0 flex-col items-center px-2.5 pt-3 pb-40">
        <h1 className="sr-only">Receipt</h1>
        <PrinterAlert printer={printer} />
        <PrinterBody tone={light.tone} label={light.label} />
        <div
          className={cn(
            "paper-font -mt-2 flex flex-col drop-shadow-[0_18px_24px_var(--paper-shadow)]",
            job.phase === "printing" && "strip-printing",
            job.phase === "torn" && "strip-torn",
          )}
        >
          <PaperRuler />
          <PaperDocument
            content={preview.content}
            feedLines={PRINT_OPTIONS.feedLinesAfterPrint}
            autoCut={PRINT_OPTIONS.autoCut}
            groups={preview.items.map((g) => ({ ...g, label: String(items.findIndex((i) => i.id === g.id) + 1) }))}
            selectedGroup={selectedId}
            onSelectGroup={(id) => {
              setSelectedId(id);
              if (!desktop) setSheet("item");
            }}
            afterGroups={
              <PaperRow paperClassName="py-1">
                <button
                  type="button"
                  onClick={addItem}
                  className="flex h-8 w-full items-center justify-center gap-1.5 rounded border border-dashed border-paper-rule font-sans text-xs text-paper-faint hover:border-paper-faint hover:text-paper-ink"
                >
                  <Plus className="size-3.5" aria-hidden="true" />
                  add item
                </button>
              </PaperRow>
            }
            cutNote={<span>≈ {lengthMm} mm</span>}
          />
        </div>
      </main>

      {desktop && (
          <aside className="sticky top-[72px] flex h-[calc(100vh-72px)] flex-col gap-6 overflow-y-auto px-6 pt-6 pb-32">
          {itemEditor ?? (
            <div className="flex flex-col gap-3 rounded-xl border border-dashed border-line-strong p-5 text-[13px] text-ink-2">
              <SectionLabel>No item selected</SectionLabel>
              <p>Click a line on the receipt to edit it, or add an item below the list.</p>
            </div>
          )}
          <div className="mt-auto flex flex-col gap-2 rounded-xl border border-line p-4 text-[13px]">
            <div className="flex justify-between">
              <span className="text-ink-2">Items</span>
              <span className="font-mono">{filled.length}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-ink-2">SUMA PTU</span>
              <span className="font-mono">{formatCurrency(totalTax)}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-ink-2">Paper</span>
              <span className="font-mono">≈ {lengthMm} mm</span>
            </div>
            <button
              type="button"
              onClick={() => {
                if (!confirmClear) return setConfirmClear(true);
                setItems([createEmptyItem()]);
                setSelectedId(null);
                setConfirmClear(false);
              }}
              className="mt-1 h-10 text-left text-danger-text hover:underline"
            >
              {confirmClear ? "Click again to clear all items" : "Clear all items"}
            </button>
          </div>
        </aside>
      )}

      <PrintDock
        printing={job.printing}
        blocked={isBlocked(printer)}
        label="Print receipt"
        onPrint={() => void print()}
        meta={
          <>
            <span className="font-serif text-[30px] leading-none text-ink">{formatCurrency(subtotal)} zł</span>
            <span className="text-xs">
              {filled.length} {filled.length === 1 ? "item" : "items"} · PTU {formatCurrency(totalTax)} · {payment === "cash" ? "cash" : payment === "card" ? "card" : "mixed"}
            </span>
          </>
        }
        leading={
          <>
            <button type="button" onClick={() => setSheet("store")} aria-label="Store and payment" className={roundButtonClass}>
              <Store className="size-5" aria-hidden="true" />
            </button>
            <button type="button" onClick={addItem} aria-label="Add item" className={roundButtonClass}>
              <Plus className="size-[22px]" aria-hidden="true" />
            </button>
            {selected && (
              <button type="button" onClick={() => setSheet("item")} aria-label="Edit selected item" className={roundButtonClass}>
                <Pencil className="size-5" aria-hidden="true" />
              </button>
            )}
          </>
        }
      />
      <Toast notice={job.notice} onDismiss={job.dismiss} />

      {sheet && !desktop && (
        <Sheet title={sheet === "item" ? "Edit item" : "Store & payment"} onClose={() => setSheet(null)}>
          {sheet === "item" ? itemEditor : storePanel}
        </Sheet>
      )}
    </div>
  );
}

interface ItemInspectorProps {
  item: ReceiptItem;
  index: number;
  count: number;
  biedronka: boolean;
  taxRates: TaxRates;
  nameRef: React.RefObject<HTMLInputElement | null>;
  onChange: (patch: Partial<ReceiptItem>) => void;
  onRemove: () => void;
  onStep: (delta: number) => void;
}

function ItemInspector({ item, index, count, biedronka, taxRates, nameRef, onChange, onRemove, onStep }: ItemInspectorProps) {
  const id = `item-${item.id}`;
  const line = estimateItemLine(item, biedronka);
  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center justify-between">
        <span className="font-serif text-[24px]">
          Item {index + 1} <span className="text-ink-3">of {count}</span>
        </span>
        <div className="flex gap-1">
          <button type="button" onClick={() => onStep(-1)} disabled={index === 0} aria-label="Previous item" className={cn(quietButtonClass, "w-11 rounded-full px-0")}>
            <ArrowUp className="size-4" aria-hidden="true" />
          </button>
          <button type="button" onClick={() => onStep(1)} disabled={index === count - 1} aria-label="Next item" className={cn(quietButtonClass, "w-11 rounded-full px-0")}>
            <ArrowDown className="size-4" aria-hidden="true" />
          </button>
        </div>
      </div>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-name`} className={fieldLabelClass}>
          Name
        </label>
        <input
          ref={nameRef}
          id={`${id}-name`}
          value={item.name}
          placeholder="Kawa ziarnista 1kg"
          onChange={(e) => onChange({ name: e.target.value })}
          className={cn(inputClass, "font-mono")}
        />
      </div>
      <div className="grid grid-cols-2 gap-2.5">
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-qty`} className={fieldLabelClass}>
            Quantity
          </label>
          <input
            id={`${id}-qty`}
            inputMode="decimal"
            defaultValue={String(item.quantity).replace(".", ",")}
            key={`${item.id}-qty`}
            onChange={(e) => onChange({ quantity: parseAmount(e.target.value) })}
            onFocus={(e) => e.target.select()}
            className={cn(inputClass, "font-mono")}
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-price`} className={fieldLabelClass}>
            Unit price
          </label>
          <input
            id={`${id}-price`}
            inputMode="decimal"
            defaultValue={item.unitPrice ? formatCurrency(item.unitPrice) : ""}
            key={`${item.id}-price`}
            placeholder="0,00"
            onChange={(e) => onChange({ unitPrice: parseAmount(e.target.value) })}
            onFocus={(e) => e.target.select()}
            className={cn(inputClass, "font-mono")}
          />
        </div>
      </div>
      {biedronka && (
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-discount`} className={fieldLabelClass}>
            Rabat
          </label>
          <input
            id={`${id}-discount`}
            inputMode="decimal"
            defaultValue={item.discount ? formatCurrency(item.discount) : ""}
            key={`${item.id}-discount`}
            placeholder="0,00"
            onChange={(e) => onChange({ discount: parseAmount(e.target.value) || undefined })}
            className={cn(inputClass, "font-mono")}
          />
        </div>
      )}
      <div className="flex flex-col gap-1.5">
        <span className={fieldLabelClass}>PTU</span>
        <Segmented
          label="PTU"
          value={item.taxCategory}
          onChange={(taxCategory) => onChange({ taxCategory })}
          options={TAX_CATEGORIES.map((c) => ({ value: c, label: <span className="font-mono">{c} {taxRates[c]}%</span> }))}
        />
      </div>
      <div className="flex justify-between font-mono text-xs text-ink-2">
        <span>
          = {formatCurrency(biedronka ? calculateItemNet(item) : calculateItemTotal(item))} {item.taxCategory}
        </span>
        <span className={line.fits ? undefined : "text-warn"}>{line.fits ? "one line · fits" : "splits into two lines"}</span>
      </div>
      <button type="button" onClick={onRemove} className={cn(quietButtonClass, "text-danger-text")}>
        <Trash2 className="size-4" aria-hidden="true" />
        Remove item
      </button>
    </div>
  );
}

interface StorePanelProps {
  template: ReceiptTemplate;
  store: StoreInfo;
  payment: PaymentMethod;
  cashAmount: string;
  cardAmount: string;
  subtotal: number;
  taxRates: TaxRates;
  onTemplate: (template: ReceiptTemplate) => void;
  onStore: (patch: Partial<StoreInfo>) => void;
  onPayment: (method: PaymentMethod) => void;
  onCash: (value: string) => void;
  onCard: (value: string) => void;
  onTaxRate: (category: TaxCategory, rate: number) => void;
}

function StorePanel(props: StorePanelProps) {
  const { template, store, payment, cashAmount, cardAmount, subtotal, taxRates, onTemplate, onStore, onPayment, onCash, onCard, onTaxRate } = props;
  const biedronka = template === "biedronka";
  const field = (key: keyof StoreInfo, label: string, mono = false) => (
    <div className="flex flex-col gap-1">
      <label htmlFor={`store-${key}`} className="text-xs text-ink-2">
        {label}
      </label>
      <input
        id={`store-${key}`}
        value={store[key] ?? ""}
        onChange={(e) => onStore({ [key]: e.target.value })}
        className={cn("h-10 w-full min-w-0 border-0 border-b border-line bg-transparent px-0 text-sm outline-none focus:border-accent", mono && "font-mono")}
      />
    </div>
  );

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-2">
        <SectionLabel>Format</SectionLabel>
        <Segmented
          label="Receipt format"
          value={template}
          onChange={onTemplate}
          options={[
            { value: "paragon-fiskalny", label: "Paragon fiskalny" },
            { value: "biedronka", label: "Biedronka" },
          ]}
        />
      </div>

      <div className="flex flex-col gap-3 rounded-xl border border-line bg-surface p-4">
        <div className="flex items-baseline justify-between">
          <SectionLabel>Store</SectionLabel>
          {!biedronka && <span className="text-xs text-ink-2">saved as default</span>}
        </div>
        <label htmlFor="store-name" className="sr-only">
          Store name
        </label>
        <input
          id="store-name"
          value={store.name}
          onChange={(e) => onStore({ name: e.target.value })}
          className="border-0 border-b border-line bg-transparent py-1 font-serif text-[22px] outline-none focus:border-accent"
        />
        {biedronka && field("storeNumber", "Store number", true)}
        {field("addressLine1", "Street")}
        <div className="grid grid-cols-[90px_1fr] gap-3">
          {field("zipCode", "Zip code", true)}
          {field("city", "City")}
        </div>
        {field("nip", "NIP", true)}
        {biedronka && field("parentCompany", "Parent company")}
        {biedronka && field("parentAddress", "Parent address")}
      </div>

      <div className="flex flex-col gap-2">
        <SectionLabel>Payment</SectionLabel>
        <Segmented
          label="Payment"
          value={payment}
          onChange={onPayment}
          options={[
            { value: "cash", label: "Cash" },
            { value: "card", label: "Card" },
            { value: "mixed", label: "Mixed" },
          ]}
        />
        {payment === "mixed" && (
          <div className="grid grid-cols-2 gap-2.5 pt-1">
            <div className="flex flex-col gap-1">
              <label htmlFor="pay-cash" className="text-xs text-ink-2">
                Cash
              </label>
              <input id="pay-cash" inputMode="decimal" value={cashAmount} placeholder={formatCurrency(subtotal)} onChange={(e) => onCash(e.target.value)} className={cn(inputClass, "font-mono")} />
            </div>
            <div className="flex flex-col gap-1">
              <label htmlFor="pay-card" className="text-xs text-ink-2">
                Card
              </label>
              <input id="pay-card" inputMode="decimal" value={cardAmount} placeholder="0,00" onChange={(e) => onCard(e.target.value)} className={cn(inputClass, "font-mono")} />
            </div>
          </div>
        )}
      </div>

      <div className="flex flex-col gap-2">
        <div className="flex items-baseline justify-between">
          <SectionLabel>PTU rates</SectionLabel>
          <span className="text-xs text-ink-2">saved as default</span>
        </div>
        <div className="grid grid-cols-4 gap-1.5 font-mono text-xs">
          {TAX_CATEGORIES.map((c) => (
            <label key={c} htmlFor={`ptu-${c}`} className="flex h-11 flex-col items-center justify-center rounded-lg border border-line focus-within:border-accent">
              <span className="font-medium">{c}</span>
              <span className="flex items-baseline text-ink-2">
                <input
                  id={`ptu-${c}`}
                  inputMode="numeric"
                  aria-label={`PTU rate ${c}`}
                  value={taxRates[c]}
                  onFocus={(e) => e.target.select()}
                  onChange={(e) => onTaxRate(c, parseRate(e.target.value))}
                  className="w-[3ch] border-0 bg-transparent p-0 text-right text-ink-2 outline-none"
                />
                %
              </span>
            </label>
          ))}
        </div>
      </div>
    </div>
  );
}
