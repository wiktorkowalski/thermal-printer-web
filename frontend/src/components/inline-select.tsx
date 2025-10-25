import { Check } from "lucide-react";
import { cn } from "@/lib/utils";

interface InlineSelectOption {
  value: string | number;
  label: string;
}

interface InlineSelectProps {
  options: InlineSelectOption[];
  value: string | number;
  onChange: (value: string | number) => void;
  className?: string;
}

export function InlineSelect({ options, value, onChange, className }: InlineSelectProps) {
  return (
    <div className={cn("flex flex-wrap gap-2", className)}>
      {options.map((option) => {
        const isSelected = value === option.value;
        return (
          <button
            key={option.value}
            type="button"
            onClick={() => onChange(option.value)}
            className={cn(
              "relative px-4 py-2 rounded-md text-sm font-medium transition-all border-2",
              "hover:bg-accent/50 focus:outline-none focus:ring-2 focus:ring-ring",
              isSelected
                ? "bg-primary text-primary-foreground border-primary/30"
                : "bg-background text-foreground border-border"
            )}
          >
            <span className="flex items-center gap-2">
              {option.label}
              {isSelected && <Check className="h-4 w-4" />}
            </span>
          </button>
        );
      })}
    </div>
  );
}
