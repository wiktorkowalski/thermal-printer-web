import { Barcode, Image, Minus, QrCode, Scissors, Type, WrapText, type LucideIcon } from "lucide-react";
import { ContentType } from "@/types/printer";

/** Insertable block types, with their single-key shortcut. */
export const INSERT_ITEMS: { type: ContentType; label: string; key: string; icon: LucideIcon }[] = [
  { type: ContentType.Text, label: "Text", key: "T", icon: Type },
  { type: ContentType.Image, label: "Image", key: "I", icon: Image },
  { type: ContentType.QRCode, label: "QR code", key: "Q", icon: QrCode },
  { type: ContentType.Barcode, label: "Barcode", key: "B", icon: Barcode },
  { type: ContentType.Separator, label: "Separator", key: "S", icon: Minus },
  { type: ContentType.LineFeed, label: "Line feed", key: "L", icon: WrapText },
  { type: ContentType.Cut, label: "Cut", key: "X", icon: Scissors },
];
