import { cn } from "@/lib/utils";

interface StatusIndicatorProps {
  status: 'idle' | 'printing' | 'success' | 'error';
  className?: string;
}

export function StatusIndicator({ status, className }: StatusIndicatorProps) {
  const statusConfig = {
    idle: {
      color: 'bg-muted-foreground',
      glow: '',
      label: 'READY'
    },
    printing: {
      color: 'bg-[hsl(var(--terminal-amber))]',
      glow: 'box-glow-amber animate-pulse',
      label: 'PRINTING'
    },
    success: {
      color: 'bg-[hsl(var(--terminal-green))]',
      glow: 'box-glow-green',
      label: 'SUCCESS'
    },
    error: {
      color: 'bg-destructive',
      glow: 'box-glow-red',
      label: 'ERROR'
    }
  };

  const config = statusConfig[status];

  return (
    <div className={cn("flex items-center gap-2 font-mono text-xs", className)}>
      <div
        className={cn(
          "w-2 h-2 rounded-full",
          config.color,
          config.glow
        )}
        aria-hidden="true"
      />
      <span className="font-semibold">{config.label}</span>
    </div>
  );
}
