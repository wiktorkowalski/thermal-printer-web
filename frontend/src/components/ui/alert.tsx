import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"

import { cn } from "@/lib/utils"

const alertVariants = cva(
  "relative w-full rounded-lg border-2 px-4 py-3 text-sm font-mono [&>svg+div]:translate-y-[-3px] [&>svg]:absolute [&>svg]:left-4 [&>svg]:top-4 [&>svg]:text-foreground [&>svg~*]:pl-7",
  {
    variants: {
      variant: {
        default: "bg-background text-foreground border-border",
        destructive:
          "border-destructive/70 bg-destructive/5 text-destructive dark:border-destructive/50 dark:bg-destructive/10 [&>svg]:text-destructive box-glow-red",
        success:
          "border-[hsl(var(--terminal-green))]/70 bg-[hsl(var(--terminal-green))]/5 text-[hsl(var(--terminal-green))] dark:border-[hsl(var(--terminal-green))]/50 dark:bg-[hsl(var(--terminal-green))]/10 [&>svg]:text-[hsl(var(--terminal-green))] box-glow-green",
        warning:
          "border-[hsl(var(--terminal-amber))]/70 bg-[hsl(var(--terminal-amber))]/5 text-[hsl(var(--terminal-amber))] dark:border-[hsl(var(--terminal-amber))]/50 dark:bg-[hsl(var(--terminal-amber))]/10 [&>svg]:text-[hsl(var(--terminal-amber))] box-glow-amber",
        info:
          "border-muted-foreground/30 bg-muted/30 text-foreground [&>svg]:text-muted-foreground",
      },
    },
    defaultVariants: {
      variant: "default",
    },
  }
)

const Alert = React.forwardRef<
  HTMLDivElement,
  React.HTMLAttributes<HTMLDivElement> & VariantProps<typeof alertVariants>
>(({ className, variant, ...props }, ref) => (
  <div
    ref={ref}
    role="alert"
    className={cn(alertVariants({ variant }), className)}
    {...props}
  />
))
Alert.displayName = "Alert"

const AlertTitle = React.forwardRef<
  HTMLParagraphElement,
  React.HTMLAttributes<HTMLHeadingElement>
>(({ className, ...props }, ref) => (
  <h5
    ref={ref}
    className={cn("mb-1 font-medium leading-none tracking-tight", className)}
    {...props}
  />
))
AlertTitle.displayName = "AlertTitle"

const AlertDescription = React.forwardRef<
  HTMLParagraphElement,
  React.HTMLAttributes<HTMLParagraphElement>
>(({ className, ...props }, ref) => (
  <div
    ref={ref}
    className={cn("text-sm [&_p]:leading-relaxed", className)}
    {...props}
  />
))
AlertDescription.displayName = "AlertDescription"

export { Alert, AlertTitle, AlertDescription }
