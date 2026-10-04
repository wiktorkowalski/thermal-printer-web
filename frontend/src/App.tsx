import { useCallback, useEffect, useState } from "react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import Editor from "./pages/Editor";
import Receipts from "./pages/Receipts";
import Journal from "./pages/Journal";
import { ThemeProvider } from "@/components/theme-provider";
import { AppHeader } from "@/components/app-header";
import { describeStatus, usePrinterStatus } from "@/hooks/use-printer-status";
import { printerApi, type PrintError } from "@/lib/api";
import { TrayDrawer } from "@/components/tray-drawer";
import { useTray } from "@/hooks/use-tray";

function Shell() {
  const printer = usePrinterStatus();
  const status = describeStatus(printer);
  const [beepError, setBeepError] = useState<string | null>(null);
  const [trayOpen, setTrayOpen] = useState(false);
  const tray = useTray();
  const closeTray = useCallback(() => setTrayOpen(false), []);

  useEffect(() => {
    if (!beepError) return;
    const timer = window.setTimeout(() => setBeepError(null), 5000);
    return () => window.clearTimeout(timer);
  }, [beepError]);

  const beep = async () => {
    try {
      await printerApi.beep();
    } catch (err) {
      setBeepError((err as PrintError).details ?? "The printer did not answer.");
    }
  };

  return (
    <div className="min-h-screen bg-bg text-ink">
      <AppHeader
        tone={status.tone}
        label={status.label}
        detail={status.detail}
        onBeep={() => void beep()}
        trayCount={tray.jobs.length}
        trayHasMore={tray.hasMore}
        onOpenTray={() => setTrayOpen(true)}
      />
      <TrayDrawer open={trayOpen} onClose={closeTray} printer={printer} tray={tray} />
      {beepError && (
        <div role="alert" className="fixed bottom-28 left-1/2 z-50 -translate-x-1/2 lg:bottom-10 rounded-xl bg-danger px-4 py-3 text-sm text-white shadow-lg">
          Beep failed. {beepError}
        </div>
      )}
      <Routes>
        {/* `key` gives each mode its own editor state and draft. */}
        <Route path="/" element={<Editor key="note" mode="note" printer={printer} />} />
        <Route path="/template" element={<Editor key="template" mode="template" printer={printer} />} />
        <Route
          path="/receipt"
          element={<Receipts printer={printer} />}
        />
        <Route path="/journal" element={<Journal printer={printer} />} />
        <Route path="/builder" element={<Navigate to="/template" replace />} />
        <Route path="/receipts" element={<Navigate to="/receipt" replace />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </div>
  );
}

export default function App() {
  return (
    <ThemeProvider defaultTheme="system" storageKey="thermal-printer-theme">
      <BrowserRouter>
        <Shell />
      </BrowserRouter>
    </ThemeProvider>
  );
}
