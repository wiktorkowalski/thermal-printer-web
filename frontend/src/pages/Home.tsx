import { useState, useRef, type FormEvent, useEffect } from "react";
import { printerApi, type PrintError } from "../lib/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { StatusIndicator } from "@/components/status-indicator";
import { PrintPreview } from "@/components/print-preview";
import { validateRequired, validateImage } from "@/lib/validation";
import { Printer, Upload, CheckCircle2, AlertCircle } from "lucide-react";
import { cn } from "@/lib/utils";

export default function Home() {
  const [name, setName] = useState("");
  const [message, setMessage] = useState("");
  const [image, setImage] = useState<File | undefined>();
  const [previewUrl, setPreviewUrl] = useState<string | undefined>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [errorDetails, setErrorDetails] = useState<string | undefined>();
  const [success, setSuccess] = useState<string | undefined>();
  const [status, setStatus] = useState<'idle' | 'printing' | 'success' | 'error'>('idle');
  const [validationErrors, setValidationErrors] = useState<Record<string, string>>({});
  const fileInputRef = useRef<HTMLInputElement>(null);

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

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    setValidationErrors(prev => ({ ...prev, image: '' }));

    if (file) {
      const validation = validateImage(file);
      if (!validation.isValid) {
        setValidationErrors(prev => ({ ...prev, image: validation.error || 'Invalid image' }));
        return;
      }
      setImage(file);
      setPreviewUrl(URL.createObjectURL(file));
    }
  };

  const handlePaste = async (e: React.ClipboardEvent) => {
    const items = e.clipboardData?.items;
    if (!items) return;

    for (const item of Array.from(items)) {
      if (item.type.indexOf("image") !== -1) {
        const file = item.getAsFile();
        if (file) {
          const validation = validateImage(file);
          if (!validation.isValid) {
            setValidationErrors(prev => ({ ...prev, image: validation.error || 'Invalid image' }));
            return;
          }
          setImage(file);
          setPreviewUrl(URL.createObjectURL(file));
        }
      }
    }
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(undefined);
    setErrorDetails(undefined);
    setSuccess(undefined);
    setValidationErrors({});

    // Validation
    const nameValidation = validateRequired(name, 'Name');
    const messageValidation = validateRequired(message, 'Message');
    const imageValidation = validateImage(image || null);

    const errors: Record<string, string> = {};
    if (!nameValidation.isValid) errors.name = nameValidation.error!;
    if (!messageValidation.isValid) errors.message = messageValidation.error!;
    if (!imageValidation.isValid) errors.image = imageValidation.error!;

    if (Object.keys(errors).length > 0) {
      setValidationErrors(errors);
      return;
    }

    setLoading(true);
    setStatus('printing');

    try {
      await printerApi.printImage(name, message, image);
      setSuccess("[OK] Print job sent successfully!");
      setStatus('success');

      // Reset form
      setTimeout(() => {
        setName("");
        setMessage("");
        setImage(undefined);
        setPreviewUrl(undefined);
        if (fileInputRef.current) {
          fileInputRef.current.value = "";
        }
      }, 1000);
    } catch (err) {
      const printError = err as PrintError;
      setError(printError.message || "[ERROR] Failed to print");
      setErrorDetails(printError.details);
      setStatus('error');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="max-w-4xl mx-auto">
      <div className="mb-8 matrix-cascade">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-3xl font-bold tracking-tight">Simple Print</h1>
            <p className="text-muted-foreground mt-2 font-mono text-sm">
              Print text and images quickly to your thermal printer
            </p>
          </div>
          <StatusIndicator status={status} />
        </div>
      </div>

      <div className="grid gap-6">
        <Card className="border-2 matrix-cascade" style={{ animationDelay: '0.1s' }}>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Printer className="h-5 w-5 text-[hsl(var(--terminal-green))]" />
              Print Job
            </CardTitle>
            <CardDescription className="font-mono text-xs">
              Enter your details and optionally upload an image to print
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form onSubmit={handleSubmit} className="space-y-6">
              {/* Name Input */}
              <div className="space-y-2">
                <Label htmlFor="name" className="font-mono text-sm">Name</Label>
                <Input
                  id="name"
                  type="text"
                  value={name}
                  onChange={(e) => {
                    setName(e.target.value);
                    setValidationErrors(prev => ({ ...prev, name: '' }));
                  }}
                  placeholder="Enter name..."
                  className={cn(
                    "font-mono",
                    validationErrors.name && "border-destructive focus-visible:ring-destructive"
                  )}
                  aria-invalid={!!validationErrors.name}
                  aria-describedby={validationErrors.name ? "name-error" : undefined}
                />
                {validationErrors.name && (
                  <p id="name-error" className="text-xs text-destructive font-mono">
                    {validationErrors.name}
                  </p>
                )}
              </div>

              {/* Message Input */}
              <div className="space-y-2">
                <Label htmlFor="message" className="font-mono text-sm">Message</Label>
                <Input
                  id="message"
                  type="text"
                  value={message}
                  onChange={(e) => {
                    setMessage(e.target.value);
                    setValidationErrors(prev => ({ ...prev, message: '' }));
                  }}
                  placeholder="Enter message..."
                  className={cn(
                    "font-mono",
                    validationErrors.message && "border-destructive focus-visible:ring-destructive"
                  )}
                  aria-invalid={!!validationErrors.message}
                  aria-describedby={validationErrors.message ? "message-error" : undefined}
                />
                {validationErrors.message && (
                  <p id="message-error" className="text-xs text-destructive font-mono">
                    {validationErrors.message}
                  </p>
                )}
              </div>

              {/* Image Upload */}
              <div className="space-y-2">
                <Label htmlFor="image" className="font-mono text-sm">Image (optional)</Label>
                <div
                  className={cn(
                    "border-2 border-dashed rounded-lg p-8 text-center transition-all cursor-pointer",
                    "hover:border-[hsl(var(--terminal-green))]/50 hover:bg-[hsl(var(--terminal-green))]/5",
                    validationErrors.image && "border-destructive"
                  )}
                  onPaste={handlePaste}
                  tabIndex={0}
                  role="button"
                  aria-label="Upload image area. Click to choose file or paste image."
                >
                  <input
                    id="image"
                    type="file"
                    ref={fileInputRef}
                    accept="image/*"
                    onChange={handleFileChange}
                    className="hidden"
                    aria-describedby={validationErrors.image ? "image-error" : undefined}
                  />
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => fileInputRef.current?.click()}
                  >
                    <Upload className="mr-2 h-4 w-4" />
                    Choose File
                  </Button>
                  <p className="mt-3 text-sm text-muted-foreground font-mono">
                    or paste an image (Ctrl+V / Cmd+V)
                  </p>
                  <p className="mt-1 text-xs text-muted-foreground/60 font-mono">
                    Max 5MB • JPG, PNG, GIF, WebP
                  </p>
                </div>

                {validationErrors.image && (
                  <p id="image-error" className="text-xs text-destructive font-mono">
                    {validationErrors.image}
                  </p>
                )}

                {previewUrl && (
                  <div className="mt-4 p-4 border-2 rounded-lg bg-muted/30">
                    <img
                      src={previewUrl}
                      alt="Preview"
                      className="max-w-full h-auto max-h-64 mx-auto rounded-md shadow-sm"
                    />
                    <p className="text-center text-xs text-muted-foreground mt-2 font-mono">
                      {image?.name} ({(image?.size! / 1024).toFixed(1)} KB)
                    </p>
                  </div>
                )}
              </div>

              {/* Success Message */}
              {success && (
                <Alert variant="success" className="paper-feed">
                  <CheckCircle2 className="h-4 w-4" />
                  <AlertDescription className="glow-green">
                    {success}
                  </AlertDescription>
                </Alert>
              )}

              {/* Error Message */}
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

              {/* Submit Button */}
              <Button
                type="submit"
                disabled={loading}
                className={cn(
                  "w-full border-2 transition-all",
                  "border-[hsl(var(--terminal-green))] bg-[hsl(var(--terminal-green))]/10 text-[hsl(var(--terminal-green))]",
                  "hover:bg-[hsl(var(--terminal-green))]/20 hover:box-glow-green",
                  loading && "opacity-50 cursor-not-allowed"
                )}
                size="lg"
              >
                <Printer className="mr-2 h-5 w-5" />
                {loading ? "PRINTING..." : "PRINT"}
              </Button>
            </form>
          </CardContent>
        </Card>

        {/* Print Preview */}
        {(name || message || previewUrl) && (
          <div className="matrix-cascade" style={{ animationDelay: '0.2s' }}>
            <PrintPreview
              simplePrint={{
                name: name || 'N/A',
                message: message || 'N/A',
                imagePreview: previewUrl
              }}
            />
          </div>
        )}
      </div>
    </div>
  );
}
