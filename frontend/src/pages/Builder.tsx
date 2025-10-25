import { useState } from "react";
import {
  CustomPrintContentType,
  CustomPrintAlignment,
  CustomPrintStyle,
  type CustomPrintContent,
  type CustomPrintRequest,
  BarcodeType,
  QRCodeModel,
  QRCodeSize,
  QRCodeCorrectionLevel,
  BarWidth,
  BarLabelPosition,
} from "../types/printer";
import { printerApi } from "../lib/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
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
} from "lucide-react";

export default function Builder() {
  const [blocks, setBlocks] = useState<(CustomPrintContent & { id: number })[]>([]);
  const [nextId, setNextId] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [success, setSuccess] = useState<string | undefined>();
  const [showJson, setShowJson] = useState(false);

  const addBlock = (type: CustomPrintContentType) => {
    const newBlock: CustomPrintContent & { id: number } = {
      id: nextId,
      type,
      content: "",
      alignment: CustomPrintAlignment.Center,
      style: type === CustomPrintContentType.Text ? [CustomPrintStyle.Bold] : undefined,
    };

    // Set type-specific defaults
    if (type === CustomPrintContentType.QRCode) {
      newBlock.qrCodeOptions = {
        model: QRCodeModel.Model2,
        size: QRCodeSize.Normal,
        correctionLevel: QRCodeCorrectionLevel.Percent7,
      };
    } else if (type === CustomPrintContentType.Barcode) {
      newBlock.barcodeOptions = {
        type: BarcodeType.CODE128,
        heightInDots: 100,
        width: BarWidth.Default,
        labelPosition: BarLabelPosition.Below,
      };
    } else if (type === CustomPrintContentType.LineFeed) {
      newBlock.lines = 1;
    } else if (type === CustomPrintContentType.Separator) {
      newBlock.separatorChar = "=";
      newBlock.separatorLength = 32;
    }

    setBlocks([...blocks, newBlock]);
    setNextId(nextId + 1);
  };

  const removeBlock = (id: number) => {
    setBlocks(blocks.filter((b) => b.id !== id));
  };

  const updateBlock = (id: number, updates: Partial<CustomPrintContent>) => {
    setBlocks(blocks.map((b) => (b.id === id ? { ...b, ...updates } : b)));
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

  const handlePrint = async () => {
    if (blocks.length === 0) {
      setError("Please add at least one content block");
      return;
    }

    setError(undefined);
    setSuccess(undefined);
    setLoading(true);

    try {
      const request: CustomPrintRequest = {
        content: blocks.map(({ id, ...block }) => block),
        source: "Template Builder",
        options: {
          autoCut: true,
          feedLinesAfterPrint: 3,
        },
      };

      await printerApi.printCustom(request);
      setSuccess("Print job sent successfully!");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to print");
    } finally {
      setLoading(false);
    }
  };

  const getJsonPreview = () => {
    const request: CustomPrintRequest = {
      content: blocks.map(({ id, ...block }) => block),
      source: "Template Builder",
      options: {
        autoCut: true,
        feedLinesAfterPrint: 3,
      },
    };
    return JSON.stringify(request, null, 2);
  };

  return (
    <div className="max-w-5xl mx-auto">
      <div className="mb-8">
        <h1 className="text-3xl font-bold tracking-tight">Template Builder</h1>
        <p className="text-muted-foreground mt-2">
          Create custom print templates with text, images, barcodes, and QR codes
        </p>
      </div>

      {/* Add Block Buttons */}
      <Card className="mb-6">
        <CardHeader>
          <CardTitle>Add Content Blocks</CardTitle>
          <CardDescription>
            Build your print template by adding different types of content
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="flex flex-wrap gap-2">
            <Button
              onClick={() => addBlock(CustomPrintContentType.Text)}
              variant="default"
            >
              <Type className="mr-2 h-4 w-4" />
              Add Text
            </Button>
            <Button
              onClick={() => addBlock(CustomPrintContentType.Image)}
              variant="default"
            >
              <ImageIcon className="mr-2 h-4 w-4" />
              Add Image
            </Button>
            <Button
              onClick={() => addBlock(CustomPrintContentType.Barcode)}
              variant="default"
            >
              <BarcodeIcon className="mr-2 h-4 w-4" />
              Add Barcode
            </Button>
            <Button
              onClick={() => addBlock(CustomPrintContentType.QRCode)}
              variant="default"
            >
              <QrCode className="mr-2 h-4 w-4" />
              Add QR Code
            </Button>
            <Button
              onClick={() => addBlock(CustomPrintContentType.LineFeed)}
              variant="secondary"
            >
              <LineFeedIcon className="mr-2 h-4 w-4" />
              Add Line Feed
            </Button>
            <Button
              onClick={() => addBlock(CustomPrintContentType.Separator)}
              variant="secondary"
            >
              <Minus className="mr-2 h-4 w-4" />
              Add Separator
            </Button>
            <Button
              onClick={() => addBlock(CustomPrintContentType.Cut)}
              variant="secondary"
            >
              <Scissors className="mr-2 h-4 w-4" />
              Add Cut
            </Button>
          </div>
        </CardContent>
      </Card>

      {/* Content Blocks */}
      {blocks.length === 0 ? (
        <Card>
          <CardContent className="p-12 text-center">
            <p className="text-muted-foreground">
              No content blocks added yet. Click a button above to add your first block.
            </p>
          </CardContent>
        </Card>
      ) : (
        <div className="space-y-4 mb-6">
          {blocks.map((block, index) => (
            <Card key={block.id}>
              <CardHeader>
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-3">
                    <Badge variant="outline">Block {index + 1}</Badge>
                    <CardTitle className="text-lg">{block.type}</CardTitle>
                  </div>
                  <div className="flex gap-2">
                    <Button
                      onClick={() => moveBlock(block.id, "up")}
                      disabled={index === 0}
                      variant="outline"
                      size="sm"
                    >
                      <ChevronUp className="h-4 w-4" />
                    </Button>
                    <Button
                      onClick={() => moveBlock(block.id, "down")}
                      disabled={index === blocks.length - 1}
                      variant="outline"
                      size="sm"
                    >
                      <ChevronDown className="h-4 w-4" />
                    </Button>
                    <Button
                      onClick={() => removeBlock(block.id)}
                      variant="destructive"
                      size="sm"
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                </div>
              </CardHeader>
              <CardContent>

              {/* Block-specific fields */}
              {block.type === CustomPrintContentType.Text && (
                <div className="space-y-4">
                  <div className="space-y-2">
                    <Label>Content</Label>
                    <Input
                      type="text"
                      value={block.content || ""}
                      onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                      placeholder="Enter text"
                    />
                  </div>
                  <div className="space-y-2">
                    <Label>Alignment</Label>
                    <Select
                      value={block.alignment}
                      onValueChange={(value) => updateBlock(block.id, { alignment: value as CustomPrintAlignment })}
                    >
                      <SelectTrigger>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={CustomPrintAlignment.Left}>Left</SelectItem>
                        <SelectItem value={CustomPrintAlignment.Center}>Center</SelectItem>
                        <SelectItem value={CustomPrintAlignment.Right}>Right</SelectItem>
                      </SelectContent>
                    </Select>
                  </div>
                </div>
              )}

              {block.type === CustomPrintContentType.Barcode && (
                <div className="space-y-4">
                  <div className="space-y-2">
                    <Label>Barcode Data</Label>
                    <Input
                      type="text"
                      value={block.content || ""}
                      onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                      placeholder="Enter barcode data"
                    />
                  </div>
                  <div className="space-y-2">
                    <Label>Type</Label>
                    <Select
                      value={block.barcodeOptions?.type}
                      onValueChange={(value) =>
                        updateBlock(block.id, {
                          barcodeOptions: { ...block.barcodeOptions!, type: value as BarcodeType },
                        })
                      }
                    >
                      <SelectTrigger>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {Object.values(BarcodeType).map((type) => (
                          <SelectItem key={type} value={type}>
                            {type}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                </div>
              )}

              {block.type === CustomPrintContentType.QRCode && (
                <div className="space-y-4">
                  <div className="space-y-2">
                    <Label>QR Code Data</Label>
                    <Input
                      type="text"
                      value={block.content || ""}
                      onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                      placeholder="Enter URL or text"
                    />
                  </div>
                  <div className="space-y-2">
                    <Label>Size</Label>
                    <Select
                      value={block.qrCodeOptions?.size}
                      onValueChange={(value) =>
                        updateBlock(block.id, {
                          qrCodeOptions: { ...block.qrCodeOptions!, size: value as QRCodeSize },
                        })
                      }
                    >
                      <SelectTrigger>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {Object.values(QRCodeSize).map((size) => (
                          <SelectItem key={size} value={size}>
                            {size}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                </div>
              )}

              {block.type === CustomPrintContentType.LineFeed && (
                <div className="space-y-2">
                  <Label>Number of Lines</Label>
                  <Input
                    type="number"
                    value={block.lines || 1}
                    onChange={(e) => updateBlock(block.id, { lines: parseInt(e.target.value) })}
                    min="1"
                    max="10"
                  />
                </div>
              )}

              {block.type === CustomPrintContentType.Separator && (
                <div className="space-y-4">
                  <div className="space-y-2">
                    <Label>Character</Label>
                    <Input
                      type="text"
                      value={block.separatorChar || "="}
                      onChange={(e) => updateBlock(block.id, { separatorChar: e.target.value[0] })}
                      maxLength={1}
                    />
                  </div>
                  <div className="space-y-2">
                    <Label>Length</Label>
                    <Input
                      type="number"
                      value={block.separatorLength || 32}
                      onChange={(e) => updateBlock(block.id, { separatorLength: parseInt(e.target.value) })}
                      min="1"
                      max="48"
                    />
                  </div>
                </div>
              )}
              </CardContent>
            </Card>
          ))}
        </div>
      )}

      {/* JSON Preview */}
      {showJson && blocks.length > 0 && (
        <Card className="mb-6">
          <CardHeader>
            <CardTitle>Generated JSON</CardTitle>
            <CardDescription>
              Preview the JSON payload that will be sent to the printer
            </CardDescription>
          </CardHeader>
          <CardContent>
            <pre className="bg-muted p-4 rounded-md overflow-x-auto text-sm font-mono">
              {getJsonPreview()}
            </pre>
          </CardContent>
        </Card>
      )}

      {/* Messages */}
      {success && (
        <Alert className="bg-green-50 border-green-200 mb-6">
          <CheckCircle2 className="h-4 w-4 text-green-600" />
          <AlertDescription className="text-green-800">
            {success}
          </AlertDescription>
        </Alert>
      )}
      {error && (
        <Alert variant="destructive" className="mb-6">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}

      {/* Action Buttons */}
      <div className="flex flex-wrap gap-3">
        <Button
          onClick={() => setShowJson(!showJson)}
          disabled={blocks.length === 0}
          variant="outline"
          size="lg"
        >
          <Code2 className="mr-2 h-5 w-5" />
          {showJson ? "Hide" : "Show"} JSON
        </Button>
        <Button
          onClick={handlePrint}
          disabled={loading || blocks.length === 0}
          size="lg"
        >
          <Printer className="mr-2 h-5 w-5" />
          {loading ? "Printing..." : "Print Template"}
        </Button>
        <Button
          onClick={() => setBlocks([])}
          disabled={blocks.length === 0}
          variant="destructive"
          size="lg"
        >
          <Trash2 className="mr-2 h-5 w-5" />
          Clear All
        </Button>
      </div>
    </div>
  );
}
