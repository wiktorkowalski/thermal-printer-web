import { useState, useRef, useEffect } from "react";
import {
  CustomPrintContentType,
  CustomPrintAlignment,
  type CustomPrintContent,
  type CustomPrintRequest,
  BarcodeType,
  QRCodeModel,
  QRCodeSize,
  QRCodeCorrectionLevel,
  BarWidth,
  BarLabelPosition,
} from "../types/printer";
import { printerApi, type PrintError } from "../lib/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { InlineSelect } from "@/components/inline-select";
import { CheckboxGroup } from "@/components/checkbox-group";
import { StatusIndicator } from "@/components/status-indicator";
import { PrintPreview } from "@/components/print-preview";
import { TemplateManager } from "@/components/template-manager";
import { validateRequired, validateBarcode, validateQRCode, validateImage, validateSeparator } from "@/lib/validation";
import {
  Type,
  Image as ImageIcon,
  Barcode as BarcodeIcon,
  QrCode,
  ArrowDown as LineFeedIcon,
  Minus,
  Scissors,
  Printer,
  Code2,
  Trash2,
  ChevronUp,
  ChevronDown,
  CheckCircle2,
  AlertCircle,
  Upload,
  Copy,
} from "lucide-react";
import { cn } from "@/lib/utils";

type BlockWithMetadata = CustomPrintContent & {
  id: number;
  imageFile?: File;
  imagePreview?: string;
};

export default function Builder() {
  const [blocks, setBlocks] = useState<BlockWithMetadata[]>([]);
  const [nextId, setNextId] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [errorDetails, setErrorDetails] = useState<string | undefined>();
  const [success, setSuccess] = useState<string | undefined>();
  const [status, setStatus] = useState<'idle' | 'printing' | 'success' | 'error'>('idle');
  const [showJson, setShowJson] = useState(false);
  const [validationErrors, setValidationErrors] = useState<Record<number, string>>({});
  const fileInputRefs = useRef<{ [key: number]: HTMLInputElement | null }>({});

  // Auto-clear success message
  useEffect(() => {
    if (success) {
      const timer = setTimeout(() => {
        setSuccess(undefined);
        setStatus('idle');
      }, 5000);
      return () => clearTimeout(timer);
    }
  }, [success]);

  // Keyboard shortcuts
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      // Ctrl/Cmd + S to save (would trigger template save)
      if ((e.ctrlKey || e.metaKey) && e.key === 's') {
        e.preventDefault();
        // Focus template name input if exists
        document.getElementById('template-name')?.focus();
      }

      // Delete key to remove focused block (when not in input)
      if (e.key === 'Delete' && !(e.target instanceof HTMLInputElement) && !(e.target instanceof HTMLTextAreaElement)) {
        // Could implement focused block deletion here
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, []);

  const addBlock = (type: CustomPrintContentType) => {
    const newBlock: CustomPrintContent & { id: number } = {
      id: nextId,
      type,
      content: "",
      alignment: CustomPrintAlignment.Center,
      styles: type === CustomPrintContentType.Text ? ["Bold"] : undefined,
    };

    // Set type-specific defaults
    if (type === CustomPrintContentType.QRCode) {
      newBlock.qrCodeOptions = {
        model: QRCodeModel.Model2,
        size: QRCodeSize.Normal,
        correctionLevel: QRCodeCorrectionLevel.Percent7,
      };
    } else if (type === CustomPrintContentType.Barcode) {
      newBlock.barcodeType = BarcodeType.CODE128;
      newBlock.barcodeOptions = {
        type: BarcodeType.CODE128,
        heightInDots: 100,
        width: BarWidth.Default,
        labelPosition: BarLabelPosition.Below,
      };
    } else if (type === CustomPrintContentType.LineFeed) {
      newBlock.lines = 1;
    } else if (type === CustomPrintContentType.Separator) {
      newBlock.content = "=";
      newBlock.length = 32;
    }

    setBlocks([...blocks, newBlock]);
    setNextId(nextId + 1);
  };

  const duplicateBlock = (id: number) => {
    const block = blocks.find(b => b.id === id);
    if (!block) return;

    const { id: _, imageFile, imagePreview, ...blockData } = block;
    const newBlock = {
      ...blockData,
      id: nextId,
      imageFile,
      imagePreview,
    };

    const index = blocks.findIndex(b => b.id === id);
    const newBlocks = [...blocks];
    newBlocks.splice(index + 1, 0, newBlock);
    setBlocks(newBlocks);
    setNextId(nextId + 1);
  };

  const removeBlock = (id: number) => {
    setBlocks(blocks.filter((b) => b.id !== id));
    setValidationErrors(prev => {
      const { [id]: _, ...rest } = prev;
      return rest;
    });
  };

  const updateBlock = (id: number, updates: Partial<BlockWithMetadata>) => {
    setBlocks(blocks.map((b) => (b.id === id ? { ...b, ...updates } : b)));
    // Clear validation error for this block
    setValidationErrors(prev => {
      const { [id]: _, ...rest } = prev;
      return rest;
    });
  };

  const handleImageChange = (blockId: number, file: File | undefined) => {
    if (file) {
      const validation = validateImage(file);
      if (!validation.isValid) {
        setValidationErrors(prev => ({ ...prev, [blockId]: validation.error! }));
        return;
      }

      const reader = new FileReader();
      reader.onloadend = () => {
        const base64 = (reader.result as string).split(',')[1]; // Remove data:image/...;base64, prefix
        updateBlock(blockId, {
          imageFile: file,
          imagePreview: URL.createObjectURL(file),
          base64Image: base64,
        });
      };
      reader.readAsDataURL(file);
    }
  };

  const handleImagePaste = (blockId: number, e: React.ClipboardEvent) => {
    const items = e.clipboardData?.items;
    if (!items) return;

    for (const item of Array.from(items)) {
      if (item.type.indexOf("image") !== -1) {
        const file = item.getAsFile();
        if (file) {
          handleImageChange(blockId, file);
        }
      }
    }
  };

  const moveBlock = (id: number, direction: "up" | "down") => {
    const index = blocks.findIndex((b) => b.id === id);
    if (index === -1) return;
    if (direction === "up" && index === 0) return;
    if (direction === "down" && index === blocks.length - 1) return;

    const newBlocks = [...blocks];
    const targetIndex = direction === "up" ? index - 1 : index + 1;
    [newBlocks[index], newBlocks[targetIndex]] = [newBlocks[targetIndex], newBlocks[index]];
    setBlocks(newBlocks);
  };

  const validateBlocks = (): boolean => {
    const errors: Record<number, string> = {};

    blocks.forEach(block => {
      if (block.type === CustomPrintContentType.Text) {
        const validation = validateRequired(block.content || '', 'Text content');
        if (!validation.isValid) errors[block.id] = validation.error!;
      }
      else if (block.type === CustomPrintContentType.Barcode) {
        const validation = validateBarcode(block.content || '', block.barcodeType || BarcodeType.CODE128);
        if (!validation.isValid) errors[block.id] = validation.error!;
      }
      else if (block.type === CustomPrintContentType.QRCode) {
        const validation = validateQRCode(block.content || '');
        if (!validation.isValid) errors[block.id] = validation.error!;
      }
      else if (block.type === CustomPrintContentType.Image && !block.base64Image) {
        errors[block.id] = '[ERROR] Please select an image';
      }
      else if (block.type === CustomPrintContentType.Separator) {
        const validation = validateSeparator(block.content || '=', block.length || 32);
        if (!validation.isValid) errors[block.id] = validation.error!;
      }
    });

    setValidationErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handlePrint = async () => {
    if (blocks.length === 0) {
      setError("[ERROR] Please add at least one content block");
      return;
    }

    if (!validateBlocks()) {
      setError("[ERROR] Please fix validation errors before printing");
      return;
    }

    setError(undefined);
    setErrorDetails(undefined);
    setSuccess(undefined);
    setLoading(true);
    setStatus('printing');

    try {
      const request: CustomPrintRequest = {
        content: blocks.map(({ id, imageFile, imagePreview, ...block }) => block),
        source: "Template Builder",
        options: {
          autoCut: true,
          feedLinesAfterPrint: 3,
        },
      };

      await printerApi.printCustom(request);
      setSuccess("[OK] Print job sent successfully!");
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

  const handleLoadTemplate = (template: CustomPrintRequest) => {
    // Convert template content to blocks with metadata
    const loadedBlocks: BlockWithMetadata[] = template.content.map((block, index) => ({
      ...block,
      id: nextId + index,
      imagePreview: block.base64Image ? `data:image/png;base64,${block.base64Image}` : undefined,
    }));

    setBlocks(loadedBlocks);
    setNextId(nextId + loadedBlocks.length);
    setValidationErrors({});
  };

  const getCurrentTemplate = (): CustomPrintRequest => {
    return {
      content: blocks.map(({ id, imageFile, imagePreview, ...block }) => block),
      source: "Template Builder",
      options: {
        autoCut: true,
        feedLinesAfterPrint: 3,
      },
    };
  };

  const getJsonPreview = () => {
    return JSON.stringify(getCurrentTemplate(), null, 2);
  };

  return (
    <div className="max-w-6xl mx-auto">
      <div className="mb-8 matrix-cascade">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-3xl font-bold tracking-tight">Template Builder</h1>
            <p className="text-muted-foreground mt-2 font-mono text-sm">
              Create custom print templates with text, images, barcodes, and QR codes
            </p>
          </div>
          <StatusIndicator status={status} />
        </div>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-6">
          {/* Add Block Buttons */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.05s' }}>
            <CardHeader>
              <CardTitle className="flex items-center gap-2 text-lg">
                <Code2 className="h-5 w-5 text-[hsl(var(--terminal-green))]" />
                Add Content Blocks
              </CardTitle>
              <CardDescription className="font-mono text-xs">
                Build your print template by adding different types of content
              </CardDescription>
            </CardHeader>
            <CardContent>
              <div className="flex flex-wrap gap-2">
                <Button
                  onClick={() => addBlock(CustomPrintContentType.Text)}
                  variant="outline"
                  size="sm"
                  className="font-mono"
                >
                  <Type className="mr-2 h-4 w-4" />
                  Text
                </Button>
                <Button
                  onClick={() => addBlock(CustomPrintContentType.Image)}
                  variant="outline"
                  size="sm"
                  className="font-mono"
                >
                  <ImageIcon className="mr-2 h-4 w-4" />
                  Image
                </Button>
                <Button
                  onClick={() => addBlock(CustomPrintContentType.Barcode)}
                  variant="outline"
                  size="sm"
                  className="font-mono"
                >
                  <BarcodeIcon className="mr-2 h-4 w-4" />
                  Barcode
                </Button>
                <Button
                  onClick={() => addBlock(CustomPrintContentType.QRCode)}
                  variant="outline"
                  size="sm"
                  className="font-mono"
                >
                  <QrCode className="mr-2 h-4 w-4" />
                  QR Code
                </Button>
                <Button
                  onClick={() => addBlock(CustomPrintContentType.LineFeed)}
                  variant="outline"
                  size="sm"
                  className="font-mono"
                >
                  <LineFeedIcon className="mr-2 h-4 w-4" />
                  Line Feed
                </Button>
                <Button
                  onClick={() => addBlock(CustomPrintContentType.Separator)}
                  variant="outline"
                  size="sm"
                  className="font-mono"
                >
                  <Minus className="mr-2 h-4 w-4" />
                  Separator
                </Button>
                <Button
                  onClick={() => addBlock(CustomPrintContentType.Cut)}
                  variant="outline"
                  size="sm"
                  className="font-mono"
                >
                  <Scissors className="mr-2 h-4 w-4" />
                  Cut
                </Button>
              </div>
            </CardContent>
          </Card>

          {/* Content Blocks */}
          {blocks.length === 0 ? (
            <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.1s' }}>
              <CardContent className="p-12 text-center">
                <p className="text-muted-foreground font-mono text-sm">
                  No content blocks added yet. Click a button above to add your first block.
                </p>
              </CardContent>
            </Card>
          ) : (
            <div className="space-y-4">
              {blocks.map((block, index) => (
                <Card
                  key={block.id}
                  className={cn(
                    "border-2 matrix-cascade",
                    validationErrors[block.id] && "border-destructive"
                  )}
                  style={{ animationDelay: `${0.1 + index * 0.02}s` }}
                >
                  <CardHeader>
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-3">
                        <Badge variant="outline" className="font-mono">
                          #{index + 1}
                        </Badge>
                        <CardTitle className="text-lg font-mono">{block.type}</CardTitle>
                      </div>
                      <div className="flex gap-2">
                        <Button
                          onClick={() => duplicateBlock(block.id)}
                          variant="outline"
                          size="sm"
                          title="Duplicate block"
                        >
                          <Copy className="h-4 w-4" />
                        </Button>
                        <Button
                          onClick={() => moveBlock(block.id, "up")}
                          disabled={index === 0}
                          variant="outline"
                          size="sm"
                          title="Move up"
                        >
                          <ChevronUp className="h-4 w-4" />
                        </Button>
                        <Button
                          onClick={() => moveBlock(block.id, "down")}
                          disabled={index === blocks.length - 1}
                          variant="outline"
                          size="sm"
                          title="Move down"
                        >
                          <ChevronDown className="h-4 w-4" />
                        </Button>
                        <Button
                          onClick={() => removeBlock(block.id)}
                          variant="outline"
                          size="sm"
                          className="text-destructive border-destructive/50 hover:bg-destructive/10"
                          title="Delete block"
                        >
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  </CardHeader>
                  <CardContent className="space-y-4">
                    {validationErrors[block.id] && (
                      <Alert variant="destructive">
                        <AlertCircle className="h-4 w-4" />
                        <AlertDescription className="font-mono text-xs">
                          {validationErrors[block.id]}
                        </AlertDescription>
                      </Alert>
                    )}

                    {/* TEXT BLOCK */}
                    {block.type === CustomPrintContentType.Text && (
                      <>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Content</Label>
                          <Input
                            type="text"
                            value={block.content || ""}
                            onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                            placeholder="Enter text..."
                            className="font-mono"
                          />
                        </div>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Text Styles</Label>
                          <CheckboxGroup
                            options={["Bold", "Italic", "Underline", "DoubleHeight", "DoubleWidth", "FontB"]}
                            selected={block.styles || []}
                            onChange={(styles) => updateBlock(block.id, { styles: styles.length > 0 ? styles : undefined })}
                          />
                        </div>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Alignment</Label>
                          <InlineSelect
                            value={block.alignment ?? CustomPrintAlignment.Center}
                            onChange={(value) => updateBlock(block.id, { alignment: value as CustomPrintAlignment })}
                            options={[
                              { value: CustomPrintAlignment.Left, label: "Left" },
                              { value: CustomPrintAlignment.Center, label: "Center" },
                              { value: CustomPrintAlignment.Right, label: "Right" },
                            ]}
                          />
                        </div>
                      </>
                    )}

                    {/* IMAGE BLOCK */}
                    {block.type === CustomPrintContentType.Image && (
                      <>
                        <div className="space-y-2">
                          <Label htmlFor={`image-${block.id}`} className="font-mono text-sm">Image</Label>
                          <div
                            className={cn(
                              "border-2 border-dashed rounded-lg p-8 text-center transition-all cursor-pointer",
                              "hover:border-[hsl(var(--terminal-green))]/50 hover:bg-[hsl(var(--terminal-green))]/5"
                            )}
                            onPaste={(e) => handleImagePaste(block.id, e)}
                            tabIndex={0}
                            role="button"
                            aria-label="Upload image area"
                          >
                            <input
                              id={`image-${block.id}`}
                              type="file"
                              ref={(el) => {
                                fileInputRefs.current[block.id] = el;
                              }}
                              accept="image/*"
                              onChange={(e) => handleImageChange(block.id, e.target.files?.[0])}
                              className="hidden"
                            />
                            <Button
                              type="button"
                              variant="outline"
                              onClick={() => fileInputRefs.current[block.id]?.click()}
                            >
                              <Upload className="mr-2 h-4 w-4" />
                              Choose File
                            </Button>
                            <p className="mt-3 text-sm text-muted-foreground font-mono">
                              or paste (Ctrl+V / Cmd+V)
                            </p>
                            <p className="mt-1 text-xs text-muted-foreground/60 font-mono">
                              Max 5MB • JPG, PNG, GIF, WebP
                            </p>
                          </div>

                          {block.imagePreview && (
                            <div className="mt-4 p-4 border-2 rounded-lg bg-muted/30">
                              <img
                                src={block.imagePreview}
                                alt="Preview"
                                className="max-w-full h-auto max-h-64 mx-auto rounded-md shadow-sm"
                              />
                            </div>
                          )}
                        </div>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Alignment</Label>
                          <InlineSelect
                            value={block.alignment ?? CustomPrintAlignment.Center}
                            onChange={(value) => updateBlock(block.id, { alignment: value as CustomPrintAlignment })}
                            options={[
                              { value: CustomPrintAlignment.Left, label: "Left" },
                              { value: CustomPrintAlignment.Center, label: "Center" },
                              { value: CustomPrintAlignment.Right, label: "Right" },
                            ]}
                          />
                        </div>
                      </>
                    )}

                    {/* BARCODE BLOCK */}
                    {block.type === CustomPrintContentType.Barcode && (
                      <>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Barcode Data</Label>
                          <Input
                            type="text"
                            value={block.content || ""}
                            onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                            placeholder="Enter barcode data..."
                            className="font-mono"
                          />
                        </div>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Type</Label>
                          <InlineSelect
                            value={block.barcodeType ?? BarcodeType.CODE128}
                            onChange={(value) =>
                              updateBlock(block.id, {
                                barcodeType: value as BarcodeType,
                                barcodeOptions: { ...block.barcodeOptions!, type: value as BarcodeType },
                              })
                            }
                            options={[
                              { value: BarcodeType.UPC_A, label: "UPC-A" },
                              { value: BarcodeType.UPC_E, label: "UPC-E" },
                              { value: BarcodeType.EAN13, label: "EAN13" },
                              { value: BarcodeType.EAN8, label: "EAN8" },
                              { value: BarcodeType.CODE39, label: "CODE39" },
                              { value: BarcodeType.CODE128, label: "CODE128" },
                              { value: BarcodeType.ITF, label: "ITF" },
                              { value: BarcodeType.CODABAR, label: "CODABAR" },
                              { value: BarcodeType.GS1_128, label: "GS1-128" },
                              { value: BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL, label: "GS1-DataBar" },
                            ]}
                          />
                        </div>
                      </>
                    )}

                    {/* QR CODE BLOCK */}
                    {block.type === CustomPrintContentType.QRCode && (
                      <>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">QR Code Data</Label>
                          <Input
                            type="text"
                            value={block.content || ""}
                            onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                            placeholder="Enter URL or text..."
                            className="font-mono"
                          />
                        </div>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Size</Label>
                          <InlineSelect
                            value={block.qrCodeOptions?.size ?? QRCodeSize.Normal}
                            onChange={(value) =>
                              updateBlock(block.id, {
                                qrCodeOptions: { ...block.qrCodeOptions!, size: value as QRCodeSize },
                              })
                            }
                            options={[
                              { value: QRCodeSize.Normal, label: "Normal" },
                              { value: QRCodeSize.Large, label: "Large" },
                              { value: QRCodeSize.ExtraLarge, label: "Extra Large" },
                            ]}
                          />
                        </div>
                      </>
                    )}

                    {/* LINE FEED BLOCK */}
                    {block.type === CustomPrintContentType.LineFeed && (
                      <div className="space-y-2">
                        <Label className="font-mono text-sm">Number of Lines</Label>
                        <Input
                          type="number"
                          value={block.lines || 1}
                          onChange={(e) => updateBlock(block.id, { lines: parseInt(e.target.value) })}
                          min="1"
                          max="10"
                          className="font-mono"
                        />
                      </div>
                    )}

                    {/* SEPARATOR BLOCK */}
                    {block.type === CustomPrintContentType.Separator && (
                      <>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Character</Label>
                          <Input
                            type="text"
                            value={block.content || "="}
                            onChange={(e) => updateBlock(block.id, { content: e.target.value[0] || "=" })}
                            maxLength={1}
                            className="font-mono"
                          />
                        </div>
                        <div className="space-y-2">
                          <Label className="font-mono text-sm">Length</Label>
                          <Input
                            type="number"
                            value={block.length || 32}
                            onChange={(e) => updateBlock(block.id, { length: parseInt(e.target.value) })}
                            min="1"
                            max="48"
                            className="font-mono"
                          />
                        </div>
                      </>
                    )}

                    {/* CUT BLOCK - no options */}
                    {block.type === CustomPrintContentType.Cut && (
                      <p className="text-sm text-muted-foreground font-mono">
                        This block will cut the paper at this position.
                      </p>
                    )}
                  </CardContent>
                </Card>
              ))}
            </div>
          )}

          {/* JSON Preview */}
          {showJson && blocks.length > 0 && (
            <Card className="border-2">
              <CardHeader>
                <CardTitle className="font-mono text-lg">Generated JSON</CardTitle>
                <CardDescription className="font-mono text-xs">
                  Preview the JSON payload that will be sent to the printer
                </CardDescription>
              </CardHeader>
              <CardContent>
                <pre className="bg-muted p-4 rounded-md overflow-x-auto text-xs font-mono border-2">
                  {getJsonPreview()}
                </pre>
              </CardContent>
            </Card>
          )}

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

          {/* Action Buttons */}
          <div className="flex flex-wrap gap-3 pb-6">
            <Button
              onClick={() => setShowJson(!showJson)}
              disabled={blocks.length === 0}
              variant="outline"
              size="lg"
              className="font-mono"
            >
              <Code2 className="mr-2 h-5 w-5" />
              {showJson ? "Hide" : "Show"} JSON
            </Button>
            <Button
              onClick={handlePrint}
              disabled={loading || blocks.length === 0}
              size="lg"
              className={cn(
                "border-2 transition-all font-mono",
                "border-[hsl(var(--terminal-green))] bg-[hsl(var(--terminal-green))]/10 text-[hsl(var(--terminal-green))]",
                "hover:bg-[hsl(var(--terminal-green))]/20 hover:box-glow-green",
                (loading || blocks.length === 0) && "opacity-50 cursor-not-allowed"
              )}
            >
              <Printer className="mr-2 h-5 w-5" />
              {loading ? "PRINTING..." : "PRINT"}
            </Button>
            <Button
              onClick={() => {
                if (confirm("Clear all blocks?")) {
                  setBlocks([]);
                  setValidationErrors({});
                }
              }}
              disabled={blocks.length === 0}
              variant="outline"
              size="lg"
              className="font-mono text-destructive border-destructive/50 hover:bg-destructive/10"
            >
              <Trash2 className="mr-2 h-5 w-5" />
              Clear All
            </Button>
          </div>
        </div>

        {/* Right Sidebar */}
        <div className="lg:col-span-1 space-y-6">
          {/* Template Manager */}
          <div className="matrix-cascade" style={{ animationDelay: '0.15s' }}>
            <TemplateManager
              currentTemplate={getCurrentTemplate()}
              onLoadTemplate={handleLoadTemplate}
            />
          </div>

          {/* Print Preview */}
          {blocks.length > 0 && (
            <div className="matrix-cascade" style={{ animationDelay: '0.2s' }}>
              <PrintPreview content={getCurrentTemplate()} />
            </div>
          )}

          {/* Keyboard Shortcuts Help */}
          <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.25s' }}>
            <CardHeader>
              <CardTitle className="text-sm font-mono">Keyboard Shortcuts</CardTitle>
            </CardHeader>
            <CardContent className="space-y-1 text-xs font-mono">
              <div className="flex justify-between">
                <span className="text-muted-foreground">Save Template</span>
                <kbd className="px-2 py-1 bg-muted rounded border">Ctrl+S</kbd>
              </div>
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
