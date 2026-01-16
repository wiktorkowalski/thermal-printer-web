import { Check } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

type Option = string | { value: string; label: string };

interface CheckboxGroupProps {
  options: Option[];
  selected: string[];
  onChange: (selected: string[]) => void;
  className?: string;
}

export function CheckboxGroup({ options, selected, onChange, className }: CheckboxGroupProps) {
  const getValue = (opt: Option) => typeof opt === 'string' ? opt : opt.value;
  const getLabel = (opt: Option) => typeof opt === 'string' ? opt : opt.label;

  const toggleOption = (value: string) => {
    if (selected.includes(value)) {
      onChange(selected.filter((s) => s !== value));
    } else {
      onChange([...selected, value]);
    }
  };

  return (
    <div className={cn("flex flex-wrap gap-2", className)}>
      {options.map((option) => {
        const value = getValue(option);
        const label = getLabel(option);
        const isSelected = selected.includes(value);
        return (
          <Button
            key={value}
            type="button"
            variant="outline"
            size="sm"
            onClick={() => toggleOption(value)}
            className={cn(
              "relative transition-all",
              isSelected && "border-[hsl(var(--terminal-green))] bg-[hsl(var(--terminal-green))]/10 text-[hsl(var(--terminal-green))] hover:bg-[hsl(var(--terminal-green))]/20"
            )}
          >
            {isSelected && <Check className="mr-1 h-3 w-3" />}
            {label}
          </Button>
        );
      })}
    </div>
  );
}
