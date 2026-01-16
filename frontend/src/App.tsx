import { BrowserRouter, Routes, Route, Link, useLocation } from "react-router-dom";
import Home from "./pages/Home";
import Builder from "./pages/Builder";
import Receipts from "./pages/Receipts";
import "./index.css";
import { Button } from "@/components/ui/button";
import { Printer } from "lucide-react";
import { ThemeProvider } from "@/components/theme-provider";
import { ThemeToggle } from "@/components/theme-toggle";
import { ScanlineOverlay } from "@/components/scanline-overlay";

function Navigation() {
  const location = useLocation();

  return (
    <nav className="border-b-2 bg-card/50 backdrop-blur supports-[backdrop-filter]:bg-card/50">
      <div className="container mx-auto px-4 py-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="p-2 bg-[hsl(var(--terminal-green))]/10 rounded-lg border-2 border-[hsl(var(--terminal-green))]/30">
              <Printer className="h-6 w-6 text-[hsl(var(--terminal-green))]" />
            </div>
            <div>
              <h1 className="text-2xl font-bold tracking-tight font-mono">Vittore's Printer</h1>
              <p className="text-sm text-muted-foreground font-mono">Thermal Print Management</p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Button
              asChild
              variant={location.pathname === "/" ? "default" : "ghost"}
              className="font-mono"
            >
              <Link to="/">Simple Print</Link>
            </Button>
            <Button
              asChild
              variant={location.pathname === "/builder" ? "default" : "ghost"}
              className="font-mono"
            >
              <Link to="/builder">Template Builder</Link>
            </Button>
            <Button
              asChild
              variant={location.pathname === "/receipts" ? "default" : "ghost"}
              className="font-mono"
            >
              <Link to="/receipts">Receipts</Link>
            </Button>
            <ThemeToggle />
          </div>
        </div>
      </div>
    </nav>
  );
}

function App() {
  return (
    <ThemeProvider defaultTheme="system" storageKey="thermal-printer-theme">
      <BrowserRouter>
        <div className="min-h-screen bg-background">
          <ScanlineOverlay />
          <Navigation />

          {/* Main Content */}
          <main className="container mx-auto px-4 py-12">
            <Routes>
              <Route path="/" element={<Home />} />
              <Route path="/builder" element={<Builder />} />
              <Route path="/receipts" element={<Receipts />} />
            </Routes>
          </main>

          {/* Footer */}
          <footer className="border-t-2 mt-auto py-6">
            <div className="container mx-auto px-4 text-center text-sm text-muted-foreground font-mono">
              <div className="opacity-60">··· Thermal Printer Web Interface ···</div>
            </div>
          </footer>
        </div>
      </BrowserRouter>
    </ThemeProvider>
  );
}

export default App;
