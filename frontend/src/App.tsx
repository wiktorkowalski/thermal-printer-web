import { BrowserRouter, Routes, Route, Link, useLocation } from "react-router-dom";
import Home from "./pages/Home";
import Builder from "./pages/Builder";
import "./index.css";
import { Button } from "@/components/ui/button";
import { Printer } from "lucide-react";
import { ThemeProvider } from "@/components/theme-provider";
import { ThemeToggle } from "@/components/theme-toggle";

function Navigation() {
  const location = useLocation();

  return (
    <nav className="border-b bg-card/50 backdrop-blur supports-[backdrop-filter]:bg-card/50">
      <div className="container mx-auto px-4 py-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="p-2 bg-primary/10 rounded-lg">
              <Printer className="h-6 w-6 text-primary" />
            </div>
            <div>
              <h1 className="text-2xl font-bold tracking-tight">Vittore's Printer</h1>
              <p className="text-sm text-muted-foreground">Thermal Print Management</p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Button
              asChild
              variant={location.pathname === "/" ? "default" : "ghost"}
            >
              <Link to="/">Simple Print</Link>
            </Button>
            <Button
              asChild
              variant={location.pathname === "/builder" ? "default" : "ghost"}
            >
              <Link to="/builder">Template Builder</Link>
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
          <Navigation />

          {/* Main Content */}
          <main className="container mx-auto px-4 py-12">
            <Routes>
              <Route path="/" element={<Home />} />
              <Route path="/builder" element={<Builder />} />
            </Routes>
          </main>

          {/* Footer */}
          <footer className="border-t mt-auto py-6">
            <div className="container mx-auto px-4 text-center text-sm text-muted-foreground">
              Thermal Printer Web Interface
            </div>
          </footer>
        </div>
      </BrowserRouter>
    </ThemeProvider>
  );
}

export default App;
