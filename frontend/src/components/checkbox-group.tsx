import { Check } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

interface CheckboxGroupProps {
  options: string[];
  selected: string[];
  onChange: (selected: string[]) => void;
  className?: string;
}

export function CheckboxGroup({ options, selected, onChange, className }: CheckboxGroupProps) {
  const toggleOption = (option: string) => {
    if (selected.includes(option)) {
      onChange(selected.filter((s) => s !== option));
    } else {
      onChange([...selected, option]);
    }
  };

  return (
    <div className={cn("flex flex-wrap gap-2", className)}>
      {options.map((option) => {
        const isSelected = selected.includes(option);
        return (
          <Button
            key={option}
            type="button"
            variant="outline"
            size="sm"
            onClick={() => toggleOption(option)}
            className={cn(
              "relative transition-all",
              isSelected && "border-[hsl(var(--terminal-green))] bg-[hsl(var(--terminal-green))]/10 text-[hsl(var(--terminal-green))] hover:bg-[hsl(var(--terminal-green))]/20"
            )}
          >
            {isSelected && <Check className="mr-1 h-3 w-3" />}
            {option}
          </Button>
        );
      })}
    </div>
  );
}
