"use client";

import React from "react";
import * as zod from "zod";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { toast } from "@/hooks/use-toast";

const formSchema = zod.object({
  name: zod.string().min(1).max(50),
  message: zod.string().min(1).max(5000),
  image: zod.instanceof(File).nullable(),
});

function ImageInput({ field, resetPreviewRef }: { field: { value: File | null; onChange: (file: File | null) => void }, resetPreviewRef: React.MutableRefObject<(() => void) | null> }) {
  const fileInputRef = React.useRef<HTMLInputElement>(null);
  const [previewUrl, setPreviewUrl] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (resetPreviewRef) {
      resetPreviewRef.current = () => setPreviewUrl(null);
    }

    const handleGlobalPaste = (e: ClipboardEvent) => {
      const files = e.clipboardData?.files;
      if (files && files.length > 0) {
        const imageFile = files[0];
        field.onChange(imageFile);
        setPreviewUrl(URL.createObjectURL(imageFile));
        return;
      }

      const items = e.clipboardData?.items;
      if (items) {
        for (let i = 0; i < items.length; i++) {
          if (items[i].type.indexOf("image") !== -1) {
            const blob = items[i].getAsFile();
            field.onChange(blob);
            setPreviewUrl(blob ? URL.createObjectURL(blob) : null);
            return;
          }
        }
      }
    };

    window.addEventListener("paste", handleGlobalPaste);
    return () => {
      window.removeEventListener("paste", handleGlobalPaste);
    };
  }, [field, resetPreviewRef]);

  const handleRemoveImage = () => {
    field.onChange(null);
    setPreviewUrl(null);
  };

  return (
    <FormItem>
      <FormLabel>Image</FormLabel>
      <div
        onClick={() => fileInputRef.current?.click()}
        className="relative border border-gray-600 p-4 cursor-pointer text-center"
      >
        {previewUrl ? (
          <div className="relative">
            <img
              src={previewUrl}
              alt="Preview"
              className="w-[500px] h-[500px] object-cover"
            />
            <button
              onClick={(e) => {
                e.stopPropagation();
                handleRemoveImage();
              }}
              className="absolute top-2 right-2 bg-red-600 text-white rounded-full p-1"
            >
              X
            </button>
          </div>
        ) : (
          "Paste an image or click here to select a file"
        )}
      </div>
      <FormControl>
        <Input
          type="file"
          id="image"
          ref={fileInputRef}
          accept="image/*"
          onChange={(e) => {
            const file = e.target.files?.[0] || null;
            field.onChange(file);
            setPreviewUrl(file ? URL.createObjectURL(file) : null);
          }}
          className="hidden"
        />
      </FormControl>
      <FormMessage />
    </FormItem>
  );
}

export default function Home() {
  const form = useForm<zod.infer<typeof formSchema>>({
    resolver: zodResolver(formSchema),
    defaultValues: {
      name: "",
      message: "",
      image: null,
    },
  });

  const resetPreviewRef = React.useRef<(() => void) | null>(null);

  const handleSubmit = async (values: zod.infer<typeof formSchema>) => {
    if (resetPreviewRef.current) {
      resetPreviewRef.current();
    }

    try {
      const formData = new FormData();
      formData.append("name", values.name);
      formData.append("message", values.message);
      
      if (values.image) {
        formData.append("image", values.image);
      }

      const response = await fetch("/api/submit", {
        method: "POST",
        body: formData,
      });

      if (response.ok) {
        const result = await response.json();
        console.log("Form submitted successfully:", result);
        toast({
          title: "Submitted successfully",
          description: (
            <pre className="mt-2 w-[340px] rounded-md bg-slate-950 p-4">
              <code className="text-white">{JSON.stringify(result, null, 2)}</code>
            </pre>
          ),
        });
        form.resetField("message");
        form.resetField("image");
      } else {
        const result = await response.json();
        console.error("Form submission failed:", result);
        toast({
          title: "Submit failed",
          description: (
            <pre className="mt-2 w-[340px] rounded-md bg-slate-950 p-4">
              <code className="text-white">{JSON.stringify(result, null, 2)}</code>
            </pre>
          ),
        });
      }
    } catch (error) {
      console.error("Error submitting form:", error);
    }
  };

  return (
    <main className="relative min-h-screen flex flex-col items-center justify-center bg-gray-900">
      {/* Header Title */}
      <h1 className="relative z-20 text-6xl font-extrabold text-center mb-12 text-white">
        Vittore&apos;s Printer
      </h1>

      {/* Form inside the card */}
      <div className="relative z-10 max-w-md w-full p-8 bg-gray-800 rounded-lg border border-gray-700 shadow-lg">
        <Form {...form}>
          <form onSubmit={form.handleSubmit(handleSubmit)} className="flex flex-col gap-4">
            <FormField
              control={form.control}
              name="name"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>Name</FormLabel>
                  <FormControl>
                    <Input
                      placeholder="Your Name"
                      {...field}
                      className="bg-gray-700 text-white border-gray-600"
                    />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="message"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>Message</FormLabel>
                  <FormControl>
                    <Input
                      placeholder="Your Message"
                      {...field}
                      className="bg-gray-700 text-white border-gray-600"
                    />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="image"
              render={({ field }) => <ImageInput field={field} resetPreviewRef={resetPreviewRef} />}
            />
            <Button type="submit" className="w-full bg-gray-600 hover:bg-gray-700 text-white py-2 rounded-lg">
              Submit
            </Button>
          </form>
        </Form>
      </div>
    </main>
  );
}
