import { useTheme } from "./theme-provider";

export function ScanlineOverlay() {
  const { theme } = useTheme();

  // Only show scanlines in dark mode
  if (theme !== "dark") return null;

  return (
    <div className="scanlines fixed inset-0 pointer-events-none z-50" aria-hidden="true" />
  );
}
