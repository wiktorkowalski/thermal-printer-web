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

function ImageInput({ field }: { field: { value: File | null; onChange: (file: File | null) => void } }) {
  const fileInputRef = React.useRef<HTMLInputElement>(null);
  const handlePaste = (e: React.ClipboardEvent<HTMLDivElement>) => {
    const items = e.clipboardData.items;
    for (let i = 0; i < items.length; i++) {
      if (items[i].type.indexOf("image") !== -1) {
        const blob = items[i].getAsFile();
        field.onChange(blob);
      }
    }
  };

  return (
    <FormItem>
      <FormLabel>Image</FormLabel>
      <div
        onPaste={handlePaste}
        onClick={() => fileInputRef.current?.click()}
        className="relative border border-gray-600 p-4 cursor-pointer text-center"
      >
        {field.value ? "Image selected" : "Paste an image or click here to select a file"}
      </div>
      <FormControl>
        <Input
          type="file"
          id="image"
          ref={fileInputRef}
          accept="image/*"
          onChange={(e) => {
            field.onChange(e.target.files?.[0] || null);
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

  const handleSubmit = async (values: zod.infer<typeof formSchema>) => {
    try {
      const formData = new FormData();
      formData.append("name", values.name);
      formData.append("message", values.message);
      
      const fileInput = document.getElementById("image") as HTMLInputElement;
      if (fileInput && fileInput.files && fileInput.files.length > 0) {
        formData.append("image", fileInput.files[0]);
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
              render={({ field }) => <ImageInput field={field} />}
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
