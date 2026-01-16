import { useState, useEffect } from "react";
import { Printer, Plus, Trash2, ChevronDown, ChevronUp, Store, CheckCircle2, AlertCircle, FileText, Check } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { StatusIndicator } from "@/components/status-indicator";
import { ReceiptPreview } from "@/components/receipt-preview";
import { cn } from "@/lib/utils";
import { printerApi, type PrintError } from "@/lib/api";
import type { ReceiptData, ReceiptItem, TaxRates, StoreInfo, PaymentMethod, TaxCategory } from "@/types/receipt";
import {
  getDefaultStore,
  saveDefaultStore,
  getTaxRates,
  calculateSubtotal,
  createEmptyItem,
  receiptToContent,
} from "@/lib/receipt-utils";

export default function Receipts() {
  // Store info
  const [store, setStore] = useState<StoreInfo>(getDefaultStore);
  const [storeExpanded, setStoreExpanded] = useState(true);

  // Tax rates
  const [taxRates] = useState<TaxRates>(getTaxRates);

  // Line items
  const [items, setItems] = useState<ReceiptItem[]>([createEmptyItem()]);

  // Payment
  const [paymentMethod, setPaymentMethod] = useState<PaymentMethod>('cash');
  const [cashAmount, setCashAmount] = useState<string>('');
  const [cardAmount, setCardAmount] = useState<string>('');

  // Template selection
  const [selectedTemplate, setSelectedTemplate] = useState<string>('paragon-fiskalny');

  // UI state
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [errorDetails, setErrorDetails] = useState<string | undefined>();
  const [success, setSuccess] = useState<string | undefined>();
  const [status, setStatus] = useState<'idle' | 'printing' | 'success' | 'error'>('idle');

  // Auto-save store defaults when changed
  useEffect(() => {
    const timeout = setTimeout(() => {
      saveDefaultStore(store);
    }, 1000);
    return () => clearTimeout(timeout);
  }, [store]);

  // Auto-clear success
  useEffect(() => {
    if (success) {
      const timer = setTimeout(() => {
        setSuccess(undefined);
        setStatus('idle');
      }, 5000);
      return () => clearTimeout(timer);
    }
  }, [success]);

  const subtotal = calculateSubtotal(items);

  const updateStore = (field: keyof StoreInfo, value: string) => {
    setStore(prev => ({ ...prev, [field]: value }));
  };

  const addItem = () => {
    setItems(prev => [...prev, createEmptyItem()]);
  };

  const removeItem = (id: string) => {
    if (items.length > 1) {
      setItems(prev => prev.filter(item => item.id !== id));
    }
  };

  const updateItem = (id: string, updates: Partial<ReceiptItem>) => {
    setItems(prev => prev.map(item =>
      item.id === id ? { ...item, ...updates } : item
    ));
  };

  const getReceiptData = (): ReceiptData => ({
    store,
    items: items.filter(item => item.name.trim() !== ''),
    payment: {
      method: paymentMethod,
      cashAmount: paymentMethod !== 'card' ? (parseFloat(cashAmount) || subtotal) : undefined,
      cardAmount: paymentMethod !== 'cash' ? (parseFloat(cardAmount) || subtotal) : undefined,
    },
  });

  const handlePrint = async () => {
    const validItems = items.filter(item => item.name.trim() !== '');
    if (validItems.length === 0) {
      setError("[ERROR] Add at least one item with a name");
      return;
    }

    setError(undefined);
    setErrorDetails(undefined);
    setSuccess(undefined);
    setLoading(true);
    setStatus('printing');

    try {
      const receipt = getReceiptData();
      const content = receiptToContent(receipt, taxRates);

      await printerApi.printCustom({
        content,
        options: {
          codePage: 'PC852', // Polish characters (Latin 2)
          autoCut: true,
          feedLinesAfterPrint: 3,
        },
      });

      setSuccess("[OK] Receipt printed successfully!");
      setStatus('success');
    } catch (err) {
      const printError = err as PrintError;
      setError(printError.message || "[ERROR] Failed to print");
      setErrorDetails(printError.details);
      setStatus('error');
    } finally {
      setLoading(false);
    }
  };

  const clearAll = () => {
    if (confirm("Clear all items?")) {
      setItems([createEmptyItem()]);
      setCashAmount('');
      setCardAmount('');
    }
  };

  return (
    <div className="max-w-6xl mx-auto">
      <div className="mb-8 matrix-cascade">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-3xl font-bold tracking-tight">Receipt Generator</h1>
            <p className="text-muted-foreground mt-2 font-mono text-sm">
              Generate Polish fiscal receipts (paragon fiskalny)
            </p>
          </div>
          <StatusIndicator status={status} />
        </div>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-6">
          {/* Store Info */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.05s' }}>
            <CardHeader
              className="cursor-pointer"
              onClick={() => setStoreExpanded(!storeExpanded)}
            >
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Store className="h-5 w-5 text-[hsl(var(--terminal-green))]" />
                  <CardTitle className="text-lg">Store Information</CardTitle>
                </div>
                {storeExpanded ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
              </div>
              <CardDescription className="font-mono text-xs">
                Saved automatically as defaults
              </CardDescription>
            </CardHeader>
            {storeExpanded && (
              <CardContent className="grid gap-3 sm:grid-cols-4">
                <div className="sm:col-span-2 space-y-1">
                  <Label className="font-mono text-xs text-muted-foreground">Store Name</Label>
                  <Input
                    value={store.name}
                    onChange={(e) => updateStore('name', e.target.value)}
                    placeholder="Store name..."
                    className="font-mono h-9"
                  />
                </div>
                <div className="sm:col-span-2 space-y-1">
                  <Label className="font-mono text-xs text-muted-foreground">NIP</Label>
                  <Input
                    value={store.nip}
                    onChange={(e) => updateStore('nip', e.target.value)}
                    placeholder="1234567890"
                    className="font-mono h-9"
                  />
                </div>
                <div className="sm:col-span-2 space-y-1">
                  <Label className="font-mono text-xs text-muted-foreground">Street</Label>
                  <Input
                    value={store.addressLine1}
                    onChange={(e) => updateStore('addressLine1', e.target.value)}
                    placeholder="ul. Przykładowa 1"
                    className="font-mono h-9"
                  />
                </div>
                <div className="space-y-1">
                  <Label className="font-mono text-xs text-muted-foreground">Zip Code</Label>
                  <Input
                    value={store.zipCode}
                    onChange={(e) => updateStore('zipCode', e.target.value)}
                    placeholder="00-000"
                    className="font-mono h-9"
                  />
                </div>
                <div className="space-y-1">
                  <Label className="font-mono text-xs text-muted-foreground">City</Label>
                  <Input
                    value={store.city}
                    onChange={(e) => updateStore('city', e.target.value)}
                    placeholder="Warszawa"
                    className="font-mono h-9"
                  />
                </div>
              </CardContent>
            )}
          </Card>

          {/* Line Items */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.1s' }}>
            <CardHeader className="pb-3">
              <CardTitle className="text-lg font-mono">Line Items</CardTitle>
            </CardHeader>
            <CardContent className="space-y-2">
              {/* Header row */}
              <div className="grid grid-cols-[1fr_70px_80px_60px_32px] gap-2 text-xs font-mono text-muted-foreground">
                <div>Name</div>
                <div className="text-center">Qty</div>
                <div className="text-center">Price</div>
                <div className="text-center">Tax</div>
                <div></div>
              </div>

              {items.map((item) => (
                <div key={item.id} className="grid grid-cols-[1fr_70px_80px_60px_32px] gap-2 items-center">
                  <Input
                    value={item.name}
                    onChange={(e) => updateItem(item.id, { name: e.target.value })}
                    placeholder="Item name..."
                    className="font-mono text-sm h-9"
                  />
                  <Input
                    inputMode="decimal"
                    value={item.quantity || ''}
                    onChange={(e) => {
                      const val = e.target.value.replace(',', '.');
                      updateItem(item.id, { quantity: parseFloat(val) || 0 });
                    }}
                    onFocus={(e) => e.target.select()}
                    placeholder="1"
                    className="font-mono text-sm h-9 text-center"
                  />
                  <Input
                    inputMode="decimal"
                    value={item.unitPrice || ''}
                    onChange={(e) => {
                      const val = e.target.value.replace(',', '.');
                      updateItem(item.id, { unitPrice: parseFloat(val) || 0 });
                    }}
                    onFocus={(e) => e.target.select()}
                    placeholder="0.00"
                    className="font-mono text-sm h-9 text-center"
                  />
                  <select
                    value={item.taxCategory}
                    onChange={(e) => updateItem(item.id, { taxCategory: e.target.value as TaxCategory })}
                    className="h-9 px-2 rounded-md border bg-background font-mono text-sm"
                  >
                    <option value="A">A</option>
                    <option value="B">B</option>
                    <option value="C">C</option>
                    <option value="D">D</option>
                  </select>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => removeItem(item.id)}
                    disabled={items.length === 1}
                    className="h-9 w-8 p-0 text-destructive hover:text-destructive hover:bg-destructive/10"
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>
              ))}

              <Button
                variant="outline"
                size="sm"
                onClick={addItem}
                className="w-full font-mono h-9"
              >
                <Plus className="h-4 w-4 mr-2" />
                Add Item
              </Button>
            </CardContent>
          </Card>

          {/* Total & Payment */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.15s' }}>
            <CardContent className="pt-4 space-y-4">
              <div className="flex justify-between items-center text-xl font-bold font-mono">
                <span>SUMA PLN</span>
                <span className="text-[hsl(var(--terminal-green))]">
                  {subtotal.toFixed(2).replace('.', ',')}
                </span>
              </div>

              <div className="flex items-center gap-3">
                <Label className="font-mono text-xs text-muted-foreground whitespace-nowrap">Payment</Label>
                <div className="flex gap-1">
                  {(['cash', 'card', 'mixed'] as const).map((method) => (
                    <button
                      key={method}
                      onClick={() => setPaymentMethod(method)}
                      className={cn(
                        "px-3 py-1.5 text-xs font-mono rounded border transition-colors",
                        paymentMethod === method
                          ? "border-[hsl(var(--terminal-green))] bg-[hsl(var(--terminal-green))]/10 text-[hsl(var(--terminal-green))]"
                          : "border-muted hover:border-muted-foreground/50"
                      )}
                    >
                      {method === 'cash' ? 'Gotówka' : method === 'card' ? 'Karta' : 'Mix'}
                    </button>
                  ))}
                </div>
              </div>

              {paymentMethod === 'mixed' && (
                <div className="grid grid-cols-2 gap-3">
                  <div className="space-y-1">
                    <Label className="font-mono text-xs text-muted-foreground">Gotówka</Label>
                    <Input
                      inputMode="decimal"
                      value={cashAmount}
                      onChange={(e) => setCashAmount(e.target.value.replace(',', '.'))}
                      onFocus={(e) => e.target.select()}
                      placeholder={subtotal.toFixed(2)}
                      className="font-mono h-9"
                    />
                  </div>
                  <div className="space-y-1">
                    <Label className="font-mono text-xs text-muted-foreground">Karta</Label>
                    <Input
                      inputMode="decimal"
                      value={cardAmount}
                      onChange={(e) => setCardAmount(e.target.value.replace(',', '.'))}
                      onFocus={(e) => e.target.select()}
                      placeholder="0.00"
                      className="font-mono h-9"
                    />
                  </div>
                </div>
              )}
            </CardContent>
          </Card>

          {/* Messages */}
          {success && (
            <Alert variant="success" className="paper-feed">
              <CheckCircle2 className="h-4 w-4" />
              <AlertDescription className="glow-green">
                {success}
              </AlertDescription>
            </Alert>
          )}
          {error && (
            <Alert variant="destructive">
              <AlertCircle className="h-4 w-4" />
              <AlertDescription>
                <div className="font-bold">{error}</div>
                {errorDetails && (
                  <div className="text-xs mt-1 opacity-90">{errorDetails}</div>
                )}
              </AlertDescription>
            </Alert>
          )}

          {/* Actions */}
          <div className="flex flex-wrap gap-3 pb-6">
            <Button
              onClick={handlePrint}
              disabled={loading}
              size="lg"
              className={cn(
                "border-2 transition-all font-mono",
                "border-[hsl(var(--terminal-green))] bg-[hsl(var(--terminal-green))]/10 text-[hsl(var(--terminal-green))]",
                "hover:bg-[hsl(var(--terminal-green))]/20 hover:box-glow-green",
                loading && "opacity-50 cursor-not-allowed"
              )}
            >
              <Printer className="mr-2 h-5 w-5" />
              {loading ? "PRINTING..." : "PRINT RECEIPT"}
            </Button>
            <Button
              onClick={clearAll}
              variant="outline"
              size="lg"
              className="font-mono text-destructive border-destructive/50 hover:bg-destructive/10"
            >
              <Trash2 className="mr-2 h-5 w-5" />
              Clear Items
            </Button>
          </div>
        </div>

        {/* Right Sidebar - Template & Preview */}
        <div className="lg:col-span-1 space-y-6">
          {/* Template Selection */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.25s' }}>
            <CardHeader className="pb-3">
              <div className="flex items-center gap-2">
                <FileText className="h-5 w-5 text-[hsl(var(--terminal-green))]" />
                <CardTitle className="text-lg">Receipt Template</CardTitle>
              </div>
            </CardHeader>
            <CardContent className="space-y-2">
              <button
                onClick={() => setSelectedTemplate('paragon-fiskalny')}
                className={cn(
                  "w-full text-left p-3 rounded-lg border-2 transition-all",
                  "hover:border-[hsl(var(--terminal-green))]/50",
                  selectedTemplate === 'paragon-fiskalny'
                    ? "border-[hsl(var(--terminal-green))] bg-[hsl(var(--terminal-green))]/10"
                    : "border-muted"
                )}
              >
                <div className="flex items-center justify-between">
                  <div>
                    <div className="font-mono font-semibold text-sm">Paragon Fiskalny</div>
                    <div className="text-xs text-muted-foreground">Standard Polish fiscal receipt</div>
                  </div>
                  {selectedTemplate === 'paragon-fiskalny' && (
                    <Check className="h-4 w-4 text-[hsl(var(--terminal-green))]" />
                  )}
                </div>
              </button>
            </CardContent>
          </Card>

          {/* Receipt Preview */}
          <div className="matrix-cascade" style={{ animationDelay: '0.3s' }}>
            <ReceiptPreview receipt={getReceiptData()} taxRates={taxRates} />
          </div>
        </div>
      </div>
    </div>
  );
}
