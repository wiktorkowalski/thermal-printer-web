import { useSyncExternalStore } from "react";

// Matches Tailwind's `lg` breakpoint, where the side rails appear.
const QUERY = "(min-width: 1024px)";

function subscribe(onChange: () => void) {
  const media = window.matchMedia(QUERY);
  media.addEventListener("change", onChange);
  return () => media.removeEventListener("change", onChange);
}

/** True on wide screens. Lets a panel render once: in a rail or in a sheet. */
export function useIsDesktop(): boolean {
  return useSyncExternalStore(subscribe, () => window.matchMedia(QUERY).matches);
}
