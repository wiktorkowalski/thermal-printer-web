import { useState, useEffect } from "react";
import { Printer, Plus, Trash2, ChevronDown, ChevronUp, Store, CheckCircle2, AlertCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { InlineSelect } from "@/components/inline-select";
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
              <CardContent className="grid gap-4 sm:grid-cols-2">
                <div className="space-y-2">
                  <Label className="font-mono text-sm">Store Name</Label>
                  <Input
                    value={store.name}
                    onChange={(e) => updateStore('name', e.target.value)}
                    placeholder="Store name..."
                    className="font-mono"
                  />
                </div>
                <div className="space-y-2">
                  <Label className="font-mono text-sm">NIP</Label>
                  <Input
                    value={store.nip}
                    onChange={(e) => updateStore('nip', e.target.value)}
                    placeholder="1234567890"
                    className="font-mono"
                  />
                </div>
                <div className="space-y-2">
                  <Label className="font-mono text-sm">Address Line 1</Label>
                  <Input
                    value={store.addressLine1}
                    onChange={(e) => updateStore('addressLine1', e.target.value)}
                    placeholder="Street address..."
                    className="font-mono"
                  />
                </div>
                <div className="space-y-2">
                  <Label className="font-mono text-sm">Address Line 2</Label>
                  <Input
                    value={store.addressLine2}
                    onChange={(e) => updateStore('addressLine2', e.target.value)}
                    placeholder="City, postal code..."
                    className="font-mono"
                  />
                </div>
              </CardContent>
            )}
          </Card>

          {/* Line Items */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.1s' }}>
            <CardHeader>
              <CardTitle className="text-lg font-mono">Line Items</CardTitle>
              <CardDescription className="font-mono text-xs">
                Add products or services to the receipt
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {/* Header row */}
              <div className="grid grid-cols-12 gap-2 text-xs font-mono text-muted-foreground px-1">
                <div className="col-span-5">Name</div>
                <div className="col-span-2 text-center">Qty</div>
                <div className="col-span-2 text-center">Price</div>
                <div className="col-span-2 text-center">Tax</div>
                <div className="col-span-1"></div>
              </div>

              {items.map((item) => (
                <div key={item.id} className="grid grid-cols-12 gap-2 items-center">
                  <div className="col-span-5">
                    <Input
                      value={item.name}
                      onChange={(e) => updateItem(item.id, { name: e.target.value })}
                      placeholder="Item name..."
                      className="font-mono text-sm"
                    />
                  </div>
                  <div className="col-span-2">
                    <Input
                      type="number"
                      value={item.quantity}
                      onChange={(e) => updateItem(item.id, { quantity: parseFloat(e.target.value) || 0 })}
                      min="0"
                      step="1"
                      className="font-mono text-sm text-center"
                    />
                  </div>
                  <div className="col-span-2">
                    <Input
                      type="number"
                      value={item.unitPrice || ''}
                      onChange={(e) => updateItem(item.id, { unitPrice: parseFloat(e.target.value) || 0 })}
                      min="0"
                      step="0.01"
                      placeholder="0,00"
                      className="font-mono text-sm text-center"
                    />
                  </div>
                  <div className="col-span-2">
                    <InlineSelect
                      value={item.taxCategory}
                      onChange={(value) => updateItem(item.id, { taxCategory: value as TaxCategory })}
                      options={[
                        { value: 'A', label: 'A 23%' },
                        { value: 'B', label: 'B 8%' },
                        { value: 'C', label: 'C 5%' },
                        { value: 'D', label: 'D 0%' },
                      ]}
                    />
                  </div>
                  <div className="col-span-1 flex justify-center">
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => removeItem(item.id)}
                      disabled={items.length === 1}
                      className="h-8 w-8 p-0 text-destructive hover:text-destructive hover:bg-destructive/10"
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                </div>
              ))}

              <Button
                variant="outline"
                size="sm"
                onClick={addItem}
                className="w-full font-mono"
              >
                <Plus className="h-4 w-4 mr-2" />
                Add Item
              </Button>
            </CardContent>
          </Card>

          {/* Totals */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.15s' }}>
            <CardHeader>
              <CardTitle className="text-lg font-mono">Total</CardTitle>
            </CardHeader>
            <CardContent>
              <div className="flex justify-between items-center text-2xl font-bold font-mono">
                <span>SUMA PLN</span>
                <span className="text-[hsl(var(--terminal-green))]">
                  {subtotal.toFixed(2).replace('.', ',')}
                </span>
              </div>
            </CardContent>
          </Card>

          {/* Payment */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.2s' }}>
            <CardHeader>
              <CardTitle className="text-lg font-mono">Payment</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-2">
                <Label className="font-mono text-sm">Payment Method</Label>
                <InlineSelect
                  value={paymentMethod}
                  onChange={(value) => setPaymentMethod(value as PaymentMethod)}
                  options={[
                    { value: 'cash', label: 'Cash' },
                    { value: 'card', label: 'Card' },
                    { value: 'mixed', label: 'Mixed' },
                  ]}
                />
              </div>

              {paymentMethod === 'mixed' && (
                <div className="grid grid-cols-2 gap-4">
                  <div className="space-y-2">
                    <Label className="font-mono text-sm">Cash Amount</Label>
                    <Input
                      type="number"
                      value={cashAmount}
                      onChange={(e) => setCashAmount(e.target.value)}
                      placeholder={subtotal.toFixed(2)}
                      className="font-mono"
                    />
                  </div>
                  <div className="space-y-2">
                    <Label className="font-mono text-sm">Card Amount</Label>
                    <Input
                      type="number"
                      value={cardAmount}
                      onChange={(e) => setCardAmount(e.target.value)}
                      placeholder="0.00"
                      className="font-mono"
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

        {/* Right Sidebar - Preview */}
        <div className="lg:col-span-1 space-y-6">
          <div className="matrix-cascade" style={{ animationDelay: '0.25s' }}>
            <ReceiptPreview receipt={getReceiptData()} taxRates={taxRates} />
          </div>
        </div>
      </div>
    </div>
  );
}
