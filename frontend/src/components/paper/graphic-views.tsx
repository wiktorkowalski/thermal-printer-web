import { useEffect, useMemo, useRef, useState } from "react";
import QRCode from "qrcode";
import JsBarcode from "jsbarcode";
import type { BarcodeOptions, ImageOptions, QRCodeOptions } from "@/types/printer";
import { BAR_MODULE_DOTS, DEFAULT_BARCODE_HEIGHT_DOTS, HEAD_DOTS, QR_MODULE_DOTS, dotsToCh } from "@/lib/paper";

// ---------------------------------------------------------------------------
// Image: resized like the backend (max 576x576, aspect kept) and dithered to
// 1-bit so the preview shows what the thermal head can actually do.

function ditherToCanvas(img: HTMLImageElement, canvas: HTMLCanvasElement, maxWidth: number, maxHeight: number) {
  const scale = Math.min(1, maxWidth / img.naturalWidth, maxHeight / img.naturalHeight);
  const width = Math.max(1, Math.round(img.naturalWidth * scale));
  const height = Math.max(1, Math.round(img.naturalHeight * scale));
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  if (!ctx) return { width, height };
  ctx.fillStyle = "#fff";
  ctx.fillRect(0, 0, width, height);
  ctx.drawImage(img, 0, 0, width, height);
  const image = ctx.getImageData(0, 0, width, height);
  const d = image.data;
  const lum = new Float32Array(width * height);
  for (let i = 0; i < lum.length; i++) {
    lum[i] = 0.299 * d[i * 4] + 0.587 * d[i * 4 + 1] + 0.114 * d[i * 4 + 2];
  }
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const i = y * width + x;
      const value = lum[i] < 128 ? 0 : 255;
      const err = lum[i] - value;
      lum[i] = value;
      if (x + 1 < width) lum[i + 1] += (err * 7) / 16;
      if (y + 1 < height) {
        if (x > 0) lum[i + width - 1] += (err * 3) / 16;
        lum[i + width] += (err * 5) / 16;
        if (x + 1 < width) lum[i + width + 1] += err / 16;
      }
    }
  }
  // Ink is a warm near-black; paper is transparent so the sheet shows through.
  for (let i = 0; i < lum.length; i++) {
    const ink = lum[i] === 0;
    d[i * 4] = 31;
    d[i * 4 + 1] = 28;
    d[i * 4 + 2] = 24;
    d[i * 4 + 3] = ink ? 255 : 0;
  }
  ctx.putImageData(image, 0, 0);
  return { width, height };
}

export function DitheredImage({
  base64,
  options,
  onSize,
}: {
  base64: string;
  options?: ImageOptions;
  onSize?: (size: { width: number; height: number }) => void;
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [size, setSize] = useState<{ width: number; height: number } | null>(null);
  const maxWidth = Math.min(options?.maxWidth ?? HEAD_DOTS, HEAD_DOTS);
  const maxHeight = options?.maxHeight ?? HEAD_DOTS;
  // Dithering is costly: report the size through a ref so a new callback
  // identity does not redraw the image.
  const onSizeRef = useRef(onSize);
  useEffect(() => {
    onSizeRef.current = onSize;
  });

  useEffect(() => {
    const img = new Image();
    let cancelled = false;
    img.onload = () => {
      if (cancelled || !canvasRef.current) return;
      const next = ditherToCanvas(img, canvasRef.current, maxWidth, maxHeight);
      setSize(next);
      onSizeRef.current?.(next);
    };
    img.src = base64.startsWith("data:") ? base64 : `data:image/png;base64,${base64}`;
    return () => {
      cancelled = true;
    };
  }, [base64, maxWidth, maxHeight]);

  return (
    <canvas
      ref={canvasRef}
      role="img"
      aria-label={size ? `Image, ${size.width} by ${size.height} dots, 1-bit` : "Image"}
      className="pixelated inline-block align-top"
      style={size ? { width: dotsToCh(size.width), height: dotsToCh(size.height) } : { width: 0, height: 0 }}
    />
  );
}

// ---------------------------------------------------------------------------
// QR code: real modules at the printer's module size.

const QR_LEVEL = { Percent7: "L", Percent15: "M", Percent25: "Q", Percent30: "H" } as const;

export function QrView({ data, options }: { data: string; options?: QRCodeOptions }) {
  const qr = useMemo(() => {
    try {
      return QRCode.create(data, { errorCorrectionLevel: QR_LEVEL[options?.correctionLevel ?? "Percent7"] });
    } catch {
      return null;
    }
  }, [data, options?.correctionLevel]);

  if (!qr) return <span className="text-paper-faint">[QR data too long]</span>;

  const n = qr.modules.size;
  const moduleDots = QR_MODULE_DOTS[options?.size ?? "Normal"];
  const rects: string[] = [];
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      if (qr.modules.get(x, y)) rects.push(`M${x} ${y}h1v1h-1z`);
    }
  }
  return (
    <svg
      role="img"
      aria-label={`QR code, ${n} by ${n} modules`}
      viewBox={`0 0 ${n} ${n}`}
      shapeRendering="crispEdges"
      className="inline-block align-top"
      style={{ width: dotsToCh(n * moduleDots), height: dotsToCh(n * moduleDots) }}
    >
      <path d={rects.join("")} fill="currentColor" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Barcode: JsBarcode draws in dots (1 unit = 1 dot); CSS scales it to paper.

const BARCODE_FORMAT: Record<string, { format: string; ean128?: boolean }> = {
  CODE128: { format: "CODE128" },
  CODE39: { format: "CODE39" },
  EAN13: { format: "EAN13" },
  EAN8: { format: "EAN8" },
  UPC_A: { format: "UPC" },
  UPC_E: { format: "UPCE" },
  ITF: { format: "ITF" },
  CODABAR: { format: "codabar" },
  GS1_128: { format: "CODE128", ean128: true },
};

export interface BarcodeRender {
  widthDots: number;
  heightDots: number;
  valid: boolean;
}

export function BarcodeView({
  data,
  options,
  onRender,
}: {
  data: string;
  options?: BarcodeOptions;
  onRender?: (result: BarcodeRender) => void;
}) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [result, setResult] = useState<BarcodeRender | null>(null);
  const type = options?.type ?? "CODE128";
  const mapping = BARCODE_FORMAT[type];
  const label = options?.labelPosition ?? "Below";
  const height = options?.heightInDots ?? DEFAULT_BARCODE_HEIGHT_DOTS;
  const moduleDots = BAR_MODULE_DOTS[options?.width ?? "Default"];

  useEffect(() => {
    if (!svgRef.current || !mapping) return;
    let valid = true;
    try {
      JsBarcode(svgRef.current, data, {
        format: mapping.format,
        ean128: mapping.ean128,
        width: moduleDots,
        height,
        margin: 0,
        displayValue: label !== "None",
        textPosition: label === "Above" ? "top" : "bottom",
        font: "DM Mono",
        fontSize: options?.useFontB ? 17 : 24,
        textMargin: 4,
        background: "transparent",
        lineColor: "currentColor",
        valid: (v: boolean) => {
          valid = v;
        },
      });
    } catch {
      valid = false;
    }
    const svg = svgRef.current;
    const widthDots = Number(svg.getAttribute("width")?.replace("px", "")) || 0;
    const heightDots = Number(svg.getAttribute("height")?.replace("px", "")) || height;
    // Scale by CSS: give the drawing a viewBox in dot units.
    svg.setAttribute("viewBox", `0 0 ${widthDots} ${heightDots}`);
    const next = { widthDots: valid ? widthDots : 0, heightDots, valid };
    setResult(next);
    onRender?.(next);
  }, [data, mapping, moduleDots, height, label, options?.useFontB, onRender]);

  if (!mapping) {
    return (
      <span className="inline-flex flex-col items-center gap-1 text-paper-faint">
        <span className="inline-block border border-dashed border-paper-rule" style={{ width: "20ch", height: dotsToCh(height) }} />
        [GS1 DataBar · no preview]
      </span>
    );
  }

  return (
    <span className="inline-flex flex-col items-center">
      <svg
        ref={svgRef}
        role="img"
        aria-label={`Barcode ${type}: ${data}`}
        className="inline-block align-top"
        style={
          result?.valid
            ? { width: dotsToCh(result.widthDots), height: dotsToCh(result.heightDots) }
            : { width: 0, height: 0 }
        }
      />
      {result && !result.valid && <span className="text-paper-faint">[{data} · not a valid {type}]</span>}
      {label === "Both" && result?.valid && <span className="-order-1">{data}</span>}
    </span>
  );
}
