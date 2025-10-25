# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a thermal printer web application with two main components:
- **Backend**: ASP.NET Core 10.0 Web API (C#) that interfaces with thermal printers via network connection
- **Frontend**: React + TypeScript + Vite application with TailwindCSS

The application provides both simple printing and a template builder for custom print jobs supporting text, images, barcodes, and QR codes.

## Architecture

### Backend (.NET)

**Core Components**:
- `Program.cs` - Entry point, configures services and middleware, sets up CORS for React frontend at `http://localhost:5173`
- `PrinterController.cs` - REST API controller with three endpoints:
  - `POST /api/printer` - Simple text printing
  - `POST /api/printer/image` - Print with optional image upload
  - `POST /api/printer/custom` - Custom template printing with advanced options
- `PrinterService.cs` - Service implementing `IPrinterService`, handles all printer communication via ESCPOS_NET library

**Printer Configuration**: Hardcoded to `192.168.123.100:9100` in `PrinterService.cs:20`

**Key Dependencies**:
- `ESCPOS_NET` (v3.0.0) - ESC/POS thermal printer protocol implementation
- `SixLabors.ImageSharp` (v2.1.10) - Image processing and format conversion

### Frontend (React)

**Structure**:
- `src/pages/Home.tsx` - Simple print interface
- `src/pages/Builder.tsx` - Template builder for custom print jobs
- `src/types/printer.ts` - TypeScript types matching backend models
- Uses shadcn/ui components with TailwindCSS styling

**Routing**: React Router with two routes: `/` (Home) and `/builder` (Template Builder)

## Common Development Commands

### Backend

Build the .NET project:
```bash
dotnet build
```

Run the backend (development):
```bash
dotnet run
```

Run without rebuilding:
```bash
dotnet run --no-build
```

Restore dependencies:
```bash
dotnet restore
```

### Frontend

Navigate to frontend directory first:
```bash
cd frontend
```

Install dependencies:
```bash
npm install
```

Run development server (runs on `http://localhost:5173`):
```bash
npm run dev
```

Build for production:
```bash
npm run build
```

Lint:
```bash
npm run lint
```

Preview production build:
```bash
npm run preview
```

### Docker

Build Docker image:
```bash
docker build -t thermal-printer-web .
```

The Dockerfile uses .NET 10.0 preview images and builds a production-ready container.

## Development Workflow

1. **Backend changes**: The backend runs on the default ASP.NET port and serves the React app from `wwwroot` in production
2. **Frontend changes**: In development, use `npm run dev` from the `frontend/` directory - it will proxy API requests to the backend
3. **CORS**: The backend is configured to allow requests from `http://localhost:5173` for local React development
4. **Production**: Frontend is built and served from `wwwroot` with a fallback to `index.html` for SPA routing

## Custom Print API

The `/api/printer/custom` endpoint accepts complex print jobs with:
- **Content types**: Text, Image, Barcode, QRCode, LineFeed, Cut, Separator, CodePage
- **Text styles**: Bold, Italic, Underline, DoubleHeight, DoubleWidth, FontB
- **Alignment**: Left, Center, Right
- **Barcode types**: UPC-A, UPC-E, EAN13, EAN8, CODE39, CODE128, ITF, CODABAR, GS1-128, GS1-DataBar
- **QR codes**: Configurable model, size, and error correction level
- **Images**: Base64 encoded, with resize options and aspect ratio preservation
- **Options**: CodePage selection, line spacing, auto-cut behavior

## CI/CD

GitHub Actions workflow (`.github/workflows/docker-image-ci.yml`) builds and pushes Docker images to GitHub Container Registry on:
- Push to master branch
- Pull requests to master
- Manual workflow dispatch

Images are tagged with: `sha-<commit>`, branch name, and `latest`.
