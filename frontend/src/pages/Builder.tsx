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
    <div className="max-w-4xl mx-auto">
      <h1 className="text-3xl font-bold mb-6">Template Builder</h1>

      {/* Add Block Buttons */}
      <div className="bg-card border rounded-lg p-4 mb-6">
        <h2 className="text-lg font-semibold mb-4">Add Content Blocks</h2>
        <div className="flex flex-wrap gap-2">
          <button
            onClick={() => addBlock(CustomPrintContentType.Text)}
            className="px-4 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
          >
            Add Text
          </button>
          <button
            onClick={() => addBlock(CustomPrintContentType.Image)}
            className="px-4 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
          >
            Add Image
          </button>
          <button
            onClick={() => addBlock(CustomPrintContentType.Barcode)}
            className="px-4 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
          >
            Add Barcode
          </button>
          <button
            onClick={() => addBlock(CustomPrintContentType.QRCode)}
            className="px-4 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90"
          >
            Add QR Code
          </button>
          <button
            onClick={() => addBlock(CustomPrintContentType.LineFeed)}
            className="px-4 py-2 bg-secondary text-secondary-foreground rounded-md hover:bg-secondary/90"
          >
            Add Line Feed
          </button>
          <button
            onClick={() => addBlock(CustomPrintContentType.Separator)}
            className="px-4 py-2 bg-secondary text-secondary-foreground rounded-md hover:bg-secondary/90"
          >
            Add Separator
          </button>
          <button
            onClick={() => addBlock(CustomPrintContentType.Cut)}
            className="px-4 py-2 bg-secondary text-secondary-foreground rounded-md hover:bg-secondary/90"
          >
            Add Cut
          </button>
        </div>
      </div>

      {/* Content Blocks */}
      {blocks.length === 0 ? (
        <div className="p-8 text-center border rounded-lg text-muted-foreground">
          No content blocks added yet. Click a button above to add your first block.
        </div>
      ) : (
        <div className="space-y-4 mb-6">
          {blocks.map((block, index) => (
            <div key={block.id} className="border rounded-lg p-4 bg-card">
              <div className="flex items-center justify-between mb-4">
                <h3 className="font-semibold">
                  Block {index + 1}: {block.type}
                </h3>
                <div className="flex gap-2">
                  <button
                    onClick={() => moveBlock(block.id, "up")}
                    disabled={index === 0}
                    className="px-2 py-1 border rounded hover:bg-accent disabled:opacity-50"
                  >
                    ↑
                  </button>
                  <button
                    onClick={() => moveBlock(block.id, "down")}
                    disabled={index === blocks.length - 1}
                    className="px-2 py-1 border rounded hover:bg-accent disabled:opacity-50"
                  >
                    ↓
                  </button>
                  <button
                    onClick={() => removeBlock(block.id)}
                    className="px-2 py-1 bg-destructive text-destructive-foreground rounded hover:bg-destructive/90"
                  >
                    Delete
                  </button>
                </div>
              </div>

              {/* Block-specific fields */}
              {block.type === CustomPrintContentType.Text && (
                <div className="space-y-3">
                  <div>
                    <label className="block text-sm font-medium mb-1">Content</label>
                    <input
                      type="text"
                      value={block.content || ""}
                      onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                      className="w-full px-3 py-2 border rounded-md"
                      placeholder="Enter text"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium mb-1">Alignment</label>
                    <select
                      value={block.alignment}
                      onChange={(e) => updateBlock(block.id, { alignment: e.target.value as CustomPrintAlignment })}
                      className="w-full px-3 py-2 border rounded-md"
                    >
                      <option value={CustomPrintAlignment.Left}>Left</option>
                      <option value={CustomPrintAlignment.Center}>Center</option>
                      <option value={CustomPrintAlignment.Right}>Right</option>
                    </select>
                  </div>
                </div>
              )}

              {block.type === CustomPrintContentType.Barcode && (
                <div className="space-y-3">
                  <div>
                    <label className="block text-sm font-medium mb-1">Barcode Data</label>
                    <input
                      type="text"
                      value={block.content || ""}
                      onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                      className="w-full px-3 py-2 border rounded-md"
                      placeholder="Enter barcode data"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium mb-1">Type</label>
                    <select
                      value={block.barcodeOptions?.type}
                      onChange={(e) =>
                        updateBlock(block.id, {
                          barcodeOptions: { ...block.barcodeOptions!, type: e.target.value as BarcodeType },
                        })
                      }
                      className="w-full px-3 py-2 border rounded-md"
                    >
                      {Object.values(BarcodeType).map((type) => (
                        <option key={type} value={type}>
                          {type}
                        </option>
                      ))}
                    </select>
                  </div>
                </div>
              )}

              {block.type === CustomPrintContentType.QRCode && (
                <div className="space-y-3">
                  <div>
                    <label className="block text-sm font-medium mb-1">QR Code Data</label>
                    <input
                      type="text"
                      value={block.content || ""}
                      onChange={(e) => updateBlock(block.id, { content: e.target.value })}
                      className="w-full px-3 py-2 border rounded-md"
                      placeholder="Enter URL or text"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium mb-1">Size</label>
                    <select
                      value={block.qrCodeOptions?.size}
                      onChange={(e) =>
                        updateBlock(block.id, {
                          qrCodeOptions: { ...block.qrCodeOptions!, size: e.target.value as QRCodeSize },
                        })
                      }
                      className="w-full px-3 py-2 border rounded-md"
                    >
                      {Object.values(QRCodeSize).map((size) => (
                        <option key={size} value={size}>
                          {size}
                        </option>
                      ))}
                    </select>
                  </div>
                </div>
              )}

              {block.type === CustomPrintContentType.LineFeed && (
                <div>
                  <label className="block text-sm font-medium mb-1">Number of Lines</label>
                  <input
                    type="number"
                    value={block.lines || 1}
                    onChange={(e) => updateBlock(block.id, { lines: parseInt(e.target.value) })}
                    className="w-full px-3 py-2 border rounded-md"
                    min="1"
                    max="10"
                  />
                </div>
              )}

              {block.type === CustomPrintContentType.Separator && (
                <div className="space-y-3">
                  <div>
                    <label className="block text-sm font-medium mb-1">Character</label>
                    <input
                      type="text"
                      value={block.separatorChar || "="}
                      onChange={(e) => updateBlock(block.id, { separatorChar: e.target.value[0] })}
                      className="w-full px-3 py-2 border rounded-md"
                      maxLength={1}
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium mb-1">Length</label>
                    <input
                      type="number"
                      value={block.separatorLength || 32}
                      onChange={(e) => updateBlock(block.id, { separatorLength: parseInt(e.target.value) })}
                      className="w-full px-3 py-2 border rounded-md"
                      min="1"
                      max="48"
                    />
                  </div>
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      {/* JSON Preview */}
      {showJson && blocks.length > 0 && (
        <div className="mb-6">
          <div className="bg-card border rounded-lg p-4">
            <h3 className="font-semibold mb-2">Generated JSON</h3>
            <pre className="bg-muted p-4 rounded-md overflow-x-auto text-sm">
              {getJsonPreview()}
            </pre>
          </div>
        </div>
      )}

      {/* Messages */}
      {success && (
        <div className="p-4 bg-green-50 border border-green-200 text-green-800 rounded-md mb-6">
          {success}
        </div>
      )}
      {error && (
        <div className="p-4 bg-red-50 border border-red-200 text-red-800 rounded-md mb-6">
          {error}
        </div>
      )}

      {/* Action Buttons */}
      <div className="flex gap-4">
        <button
          onClick={() => setShowJson(!showJson)}
          disabled={blocks.length === 0}
          className="px-4 py-2 bg-secondary text-secondary-foreground rounded-md hover:bg-secondary/90 disabled:opacity-50"
        >
          {showJson ? "Hide" : "Show"} JSON
        </button>
        <button
          onClick={handlePrint}
          disabled={loading || blocks.length === 0}
          className="px-4 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50"
        >
          {loading ? "Printing..." : "Print"}
        </button>
        <button
          onClick={() => setBlocks([])}
          disabled={blocks.length === 0}
          className="px-4 py-2 bg-destructive text-destructive-foreground rounded-md hover:bg-destructive/90 disabled:opacity-50"
        >
          Clear All
        </button>
      </div>
    </div>
  );
}
