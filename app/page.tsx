"use client";

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
});

export default function Home() {
  const form = useForm<zod.infer<typeof formSchema>>({
    resolver: zodResolver(formSchema),
    defaultValues: {
      name: "",
      message: "",
    },
  });

  const handleSubmit = async (values: zod.infer<typeof formSchema>) => {
    try {
      const response = await fetch("/api/submit", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify(values),
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
          <form
            onSubmit={form.handleSubmit(handleSubmit)}
            className="flex flex-col gap-4"
          >
            <FormField
              control={form.control}
              name="name"
              render={({ field }) => {
                return (
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
                );
              }}
            />
            <FormField
              control={form.control}
              name="message"
              render={({ field }) => {
                return (
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
                );
              }}
            />
            <Button
              type="submit"
              className="w-full bg-gray-600 hover:bg-gray-700 text-white py-2 rounded-lg"
            >
              Submit
            </Button>
          </form>
        </Form>
      </div>
    </main>
  );
}
