// Receipt generator types

export interface StoreInfo {
  name: string;
  addressLine1: string;
  city: string;
  zipCode: string;
  nip: string;
  // Biedronka-specific: parent company info
  parentCompany?: string;
  parentAddress?: string;
  storeNumber?: string;
}

export type ReceiptTemplate = 'paragon-fiskalny' | 'biedronka';

export interface ReceiptItem {
  id: string;
  name: string;
  quantity: number;
  unitPrice: number;
  taxCategory: TaxCategory;
  discount?: number; // Rabat amount
}

export type TaxCategory = 'A' | 'B' | 'C' | 'D';

export interface TaxRates {
  A: number;
  B: number;
  C: number;
  D: number;
}

export type PaymentMethod = 'cash' | 'card' | 'mixed';

export interface Payment {
  method: PaymentMethod;
  cashAmount?: number;
  cardAmount?: number;
}

export interface ReceiptData {
  store: StoreInfo;
  items: ReceiptItem[];
  payment: Payment;
  template?: ReceiptTemplate;
  title?: string;
  documentNumber?: string;
  date?: string;
  // Biedronka-specific footer
  kasaNumber?: string;
  kasjerNumber?: string;
}

export interface TaxBreakdownLine {
  category: TaxCategory;
  rate: number;
  base: number;
  tax: number;
}

// Default values
export const DEFAULT_TAX_RATES: TaxRates = {
  A: 23,
  B: 8,
  C: 5,
  D: 0,
};

export const DEFAULT_STORE: StoreInfo = {
  name: 'Sklep u Janusza',
  addressLine1: 'ul. Świętego Mikołaja 42',
  city: 'Pcim Dolny',
  zipCode: '69-420',
  nip: '1234567890',
};

export const BIEDRONKA_STORE: StoreInfo = {
  name: 'Biedronka',
  storeNumber: '4387',
  addressLine1: 'ul. Ks. Czesława Majorka 1',
  city: 'Ostrów Wielkopolski',
  zipCode: '63-400',
  parentCompany: 'Jeronimo Martins Polska S.A.',
  parentAddress: 'ul. Żniwna 5, 62-025 Kostrzyn',
  nip: '779-10-11-327',
};

export const TAX_CATEGORY_LABELS: Record<TaxCategory, string> = {
  A: 'PTU A (23%)',
  B: 'PTU B (8%)',
  C: 'PTU C (5%)',
  D: 'PTU D (0%)',
};
