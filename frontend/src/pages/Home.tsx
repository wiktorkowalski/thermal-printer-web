import { useState, useRef, type FormEvent } from "react";
import { printerApi } from "../lib/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Printer, Upload, CheckCircle2, AlertCircle } from "lucide-react";

export default function Home() {
  const [name, setName] = useState("");
  const [message, setMessage] = useState("");
  const [image, setImage] = useState<File | undefined>();
  const [previewUrl, setPreviewUrl] = useState<string | undefined>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [success, setSuccess] = useState<string | undefined>();
  const fileInputRef = useRef<HTMLInputElement>(null);

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) {
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
          setImage(file);
          setPreviewUrl(URL.createObjectURL(file));
        }
      }
    }
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(undefined);
    setSuccess(undefined);
    setLoading(true);

    try {
      await printerApi.printImage(name, message, image);
      setSuccess("Print job sent successfully!");
      // Reset form
      setName("");
      setMessage("");
      setImage(undefined);
      setPreviewUrl(undefined);
      if (fileInputRef.current) {
        fileInputRef.current.value = "";
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to print");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="max-w-2xl mx-auto">
      <div className="mb-8">
        <h1 className="text-3xl font-bold tracking-tight">Simple Print</h1>
        <p className="text-muted-foreground mt-2">
          Print text and images quickly to your thermal printer
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Print Job</CardTitle>
          <CardDescription>
            Enter your details and optionally upload an image to print
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit} className="space-y-6">
            {/* Name Input */}
            <div className="space-y-2">
              <Label htmlFor="name">Name</Label>
              <Input
                id="name"
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Enter name"
              />
            </div>

            {/* Message Input */}
            <div className="space-y-2">
              <Label htmlFor="message">Message</Label>
              <Input
                id="message"
                type="text"
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                placeholder="Enter message"
              />
            </div>

            {/* Image Upload */}
            <div className="space-y-2">
              <Label htmlFor="image">Image (optional)</Label>
              <div
                className="border-2 border-dashed rounded-lg p-8 text-center hover:border-primary/50 hover:bg-accent/5 transition-all cursor-pointer"
                onPaste={handlePaste}
                tabIndex={0}
              >
                <input
                  id="image"
                  type="file"
                  ref={fileInputRef}
                  accept="image/*"
                  onChange={handleFileChange}
                  className="hidden"
                />
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => fileInputRef.current?.click()}
                >
                  <Upload className="mr-2 h-4 w-4" />
                  Choose File
                </Button>
                <p className="mt-3 text-sm text-muted-foreground">
                  or paste an image (Ctrl+V / Cmd+V)
                </p>
              </div>

              {previewUrl && (
                <div className="mt-4 p-4 border rounded-lg bg-accent/5">
                  <img
                    src={previewUrl}
                    alt="Preview"
                    className="max-w-full h-auto max-h-64 mx-auto rounded-md shadow-sm"
                  />
                </div>
              )}
            </div>

            {/* Success Message */}
            {success && (
              <Alert className="bg-green-50 border-green-200">
                <CheckCircle2 className="h-4 w-4 text-green-600" />
                <AlertDescription className="text-green-800">
                  {success}
                </AlertDescription>
              </Alert>
            )}

            {/* Error Message */}
            {error && (
              <Alert variant="destructive">
                <AlertCircle className="h-4 w-4" />
                <AlertDescription>{error}</AlertDescription>
              </Alert>
            )}

            {/* Submit Button */}
            <Button type="submit" disabled={loading} className="w-full" size="lg">
              <Printer className="mr-2 h-5 w-5" />
              {loading ? "Printing..." : "Print"}
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}
