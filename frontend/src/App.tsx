import { BrowserRouter, Routes, Route, Link } from "react-router-dom";
import Home from "./pages/Home";
import Builder from "./pages/Builder";
import "./index.css";

function App() {
  return (
    <BrowserRouter>
      <div className="min-h-screen bg-background">
        {/* Navigation */}
        <nav className="border-b">
          <div className="container mx-auto px-4 py-4">
            <div className="flex items-center justify-between">
              <h1 className="text-2xl font-bold">Vittore's Printer</h1>
              <div className="flex gap-4">
                <Link
                  to="/"
                  className="px-4 py-2 rounded-md hover:bg-accent transition-colors"
                >
                  Simple Print
                </Link>
                <Link
                  to="/builder"
                  className="px-4 py-2 rounded-md hover:bg-accent transition-colors"
                >
                  Template Builder
                </Link>
              </div>
            </div>
          </div>
        </nav>

        {/* Main Content */}
        <main className="container mx-auto px-4 py-8">
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/builder" element={<Builder />} />
          </Routes>
        </main>
      </div>
    </BrowserRouter>
  );
}

export default App;
