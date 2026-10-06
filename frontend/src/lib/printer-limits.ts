// Limits the backend applies to a print job. The backend is the source of truth:
// a job past one of these gets a 400. Keep each number equal to the named constant.

// backend/Services/Printing/Handlers/TextBlockHandler.cs: MaxLength, MaxLines
// For a block with `wrap: true` the server counts the text after its wrap; the editor counts the text as sent.
export const TEXT_MAX_LENGTH = 10_000;
export const TEXT_MAX_LINES = 500;

// backend/Models/TextSize.cs: Min, Max (the width and the height multiplier of a text)
export const TEXT_SIZE_MIN = 1;
export const TEXT_SIZE_MAX = 8;

// backend/Services/Printing/Handlers/SeparatorBlockHandler.cs: MaxLength
export const SEPARATOR_MAX_LENGTH = 64;

// backend/Services/Printing/Handlers/LineFeedBlockHandler.cs: MaxLines
export const LINE_FEED_MAX_LINES = 100;

// backend/Services/Printing/SignalCommand.cs: Min, Max (the count and the duration of a Signal block and of the beep call)
export const SIGNAL_MIN = 1;
export const SIGNAL_MAX = 9;

// backend/Services/Printing/Handlers/BarcodeBlockHandler.cs: MinHeightInDots, MaxHeightInDots
export const BARCODE_MIN_HEIGHT_DOTS = 1;
export const BARCODE_MAX_HEIGHT_DOTS = 255;
// Same file, BuildCommand: the payload length is one byte. CODE128 spends
// 2 bytes of it on the code set prefix and sends each '{' twice.
export const BARCODE_MAX_PAYLOAD = 255;
export const CODE128_MAX_PAYLOAD = BARCODE_MAX_PAYLOAD - 2;

// backend/Services/Printing/Handlers/QRCodeBlockHandler.cs: Model2MaxBytes, Model1MaxBytes, MicroMaxBytes
// Bytes of the content as UTF-8, not characters.
export const QR_MAX_BYTES = { Model2: 2953, Model1: 707, Micro: 21 } as const;

// backend/Services/Printing/Handlers/ImageBlockHandler.cs: MaxImageBytes, MaxSidePixels, MaxPixels
export const IMAGE_MAX_BYTES = 16 * 1024 * 1024;
export const IMAGE_MAX_SIDE_PIXELS = 16_384;
export const IMAGE_MAX_PIXELS = 8192 * 6144;
// Same file, PrintLimit: maxWidth and maxHeight below 1 are rejected.
export const IMAGE_MIN_PRINT_SIZE = 1;

// backend/Services/PrinterService.cs: MaxBlocks, MaxImageBlocks, MaxSignalBlocks, MaxFeedBeforeCut, MaxLineSpacing, MaxRequestBodyBytes
export const MAX_BLOCKS = 500;
export const MAX_IMAGE_BLOCKS = 20;
export const MAX_SIGNAL_BLOCKS = 3;
// MaxFeedBeforeCut is a number of lines (the feed is built in backend/Services/Printing/CutFeed.cs): one line is 29 dots, LINE_DOTS in lib/paper.ts.
export const MAX_FEED_BEFORE_CUT = 255;
export const MAX_LINE_SPACING = 255;
export const MAX_REQUEST_BYTES = 30_000_000;

// backend/Services/Printing/PaperLength.cs: MaxDots (32,000 dots = 4 m per job)
export const MAX_PAPER_DOTS = 32_000;
// Same file: QRMinModules, QRMaxModules, QRModulesPerByte. The paper estimate for a QR code.
export const QR_MIN_MODULES = 21;
export const QR_MAX_MODULES = 177;
export const QR_MODULES_PER_BYTE = 25;

// backend/Controllers/PrintResultResponse.cs: BusyRetryAfterSeconds (the Retry-After of a busy answer)
export const BUSY_RETRY_AFTER_SECONDS = 5;

// backend/Services/Journal/PrintJobEntry.cs: ImageHashPrefix. A stored image block holds a hash that starts with it.
export const IMAGE_HASH_PREFIX = "sha256:";

// backend/Services/Journal/PrintJournalReader.Queries.cs: MinQueryLength, MaxQueryLength, MaxStatsDays
export const SEARCH_MIN_QUERY_LENGTH = 2;
export const SEARCH_MAX_QUERY_LENGTH = 100;
export const STATS_MAX_DAYS = 365;

// Stricter in the editor on purpose. The backend takes more, the editor does not offer it.
// A file over this does not fit the draft in localStorage.
export const EDITOR_IMAGE_MAX_BYTES = 5 * 1024 * 1024;
export const EDITOR_IMAGE_MAX_MB = EDITOR_IMAGE_MAX_BYTES / 1024 / 1024;
export const EDITOR_LINE_FEED_MAX_LINES = 20;
export const EDITOR_MAX_FEED_BEFORE_CUT = 10;
export const EDITOR_BARCODE_MIN_HEIGHT_DOTS = 16;
