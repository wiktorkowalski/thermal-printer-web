import { useState, useEffect } from "react";
import { Save, FolderOpen, Trash2, Download, Upload, File } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { cn } from "@/lib/utils";
import { validateTemplateName } from "@/lib/validation";
import type { CustomPrintRequest } from "@/types/printer";

interface Template {
  id: string;
  name: string;
  content: CustomPrintRequest;
  createdAt: string;
  updatedAt: string;
}

interface TemplateManagerProps {
  currentTemplate: CustomPrintRequest;
  onLoadTemplate: (template: CustomPrintRequest) => void;
  className?: string;
}

const STORAGE_KEY = 'thermal-printer-templates';

export function TemplateManager({ currentTemplate, onLoadTemplate, className }: TemplateManagerProps) {
  const [templates, setTemplates] = useState<Template[]>([]);
  const [templateName, setTemplateName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  // Load templates from localStorage
  useEffect(() => {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (stored) {
        setTemplates(JSON.parse(stored));
      }
    } catch (err) {
      setError('[ERROR] Failed to load templates from storage');
    }
  }, []);

  // Save templates to localStorage
  const saveToStorage = (newTemplates: Template[]) => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(newTemplates));
      setTemplates(newTemplates);
    } catch (err) {
      setError('[ERROR] Failed to save to storage. Quota exceeded?');
    }
  };

  // Save current template
  const handleSave = () => {
    setError(null);
    setSuccess(null);

    const validation = validateTemplateName(templateName);
    if (!validation.isValid) {
      setError(validation.error || 'Invalid template name');
      return;
    }

    if (currentTemplate.content.length === 0) {
      setError('[ERROR] Cannot save empty template');
      return;
    }

    const existingIndex = templates.findIndex(t => t.name === templateName);
    const now = new Date().toISOString();

    if (existingIndex >= 0) {
      // Update existing
      const updated = [...templates];
      updated[existingIndex] = {
        ...updated[existingIndex],
        content: currentTemplate,
        updatedAt: now
      };
      saveToStorage(updated);
      setSuccess(`[OK] Template "${templateName}" updated`);
    } else {
      // Create new
      const newTemplate: Template = {
        id: crypto.randomUUID(),
        name: templateName,
        content: currentTemplate,
        createdAt: now,
        updatedAt: now
      };
      saveToStorage([...templates, newTemplate]);
      setSuccess(`[OK] Template "${templateName}" saved`);
    }

    setTemplateName('');
    setTimeout(() => setSuccess(null), 3000);
  };

  // Load template
  const handleLoad = (template: Template) => {
    onLoadTemplate(template.content);
    setSuccess(`[OK] Loaded template "${template.name}"`);
    setTimeout(() => setSuccess(null), 3000);
  };

  // Delete template
  const handleDelete = (id: string, name: string) => {
    if (!confirm(`Delete template "${name}"?`)) return;

    const filtered = templates.filter(t => t.id !== id);
    saveToStorage(filtered);
    setSuccess(`[OK] Deleted template "${name}"`);
    setTimeout(() => setSuccess(null), 3000);
  };

  // Export template to JSON
  const handleExport = (template: Template) => {
    const dataStr = JSON.stringify(template, null, 2);
    const blob = new Blob([dataStr], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${template.name}.json`;
    a.click();
    URL.revokeObjectURL(url);
    setSuccess(`[OK] Exported "${template.name}"`);
    setTimeout(() => setSuccess(null), 3000);
  };

  // Import template from JSON
  const handleImport = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = (e) => {
      try {
        const imported = JSON.parse(e.target?.result as string) as Template;

        // Validate structure
        if (!imported.name || !imported.content || !imported.content.content) {
          setError('[ERROR] Invalid template file structure');
          return;
        }

        // Check if name exists
        const existingIndex = templates.findIndex(t => t.name === imported.name);
        if (existingIndex >= 0) {
          if (!confirm(`Template "${imported.name}" exists. Overwrite?`)) return;
        }

        const now = new Date().toISOString();
        const newTemplate: Template = {
          id: crypto.randomUUID(),
          name: imported.name,
          content: imported.content,
          createdAt: imported.createdAt || now,
          updatedAt: now
        };

        if (existingIndex >= 0) {
          const updated = [...templates];
          updated[existingIndex] = newTemplate;
          saveToStorage(updated);
        } else {
          saveToStorage([...templates, newTemplate]);
        }

        setSuccess(`[OK] Imported "${imported.name}"`);
        setTimeout(() => setSuccess(null), 3000);
      } catch (err) {
        setError('[ERROR] Failed to parse template file');
      }
    };
    reader.readAsText(file);
    event.target.value = '';
  };

  return (
    <Card className={cn("border-2", className)}>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-lg">
          <FolderOpen className="h-5 w-5 text-[hsl(var(--terminal-green))]" />
          Template Manager
        </CardTitle>
        <CardDescription className="font-mono text-xs">
          Save, load, and manage print templates
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {/* Status Messages */}
        {error && (
          <Alert variant="destructive">
            <AlertDescription className="font-mono text-xs">{error}</AlertDescription>
          </Alert>
        )}
        {success && (
          <Alert variant="success">
            <AlertDescription className="font-mono text-xs glow-green">{success}</AlertDescription>
          </Alert>
        )}

        {/* Save Current Template */}
        <div className="space-y-2">
          <Label htmlFor="template-name" className="font-mono text-sm">
            Save Current Template
          </Label>
          <div className="flex gap-2">
            <Input
              id="template-name"
              value={templateName}
              onChange={(e) => setTemplateName(e.target.value)}
              placeholder="Template name..."
              className="font-mono"
              onKeyDown={(e) => e.key === 'Enter' && handleSave()}
            />
            <Button
              onClick={handleSave}
              variant="outline"
              className="border-[hsl(var(--terminal-green))] text-[hsl(var(--terminal-green))] hover:bg-[hsl(var(--terminal-green))]/10"
            >
              <Save className="h-4 w-4 mr-2" />
              SAVE
            </Button>
          </div>
        </div>

        {/* Import/Export */}
        <div className="flex gap-2">
          <Button variant="outline" size="sm" className="flex-1" asChild>
            <label className="cursor-pointer">
              <Upload className="h-3 w-3 mr-2" />
              IMPORT
              <input
                type="file"
                accept=".json"
                onChange={handleImport}
                className="hidden"
              />
            </label>
          </Button>
        </div>

        {/* Template List */}
        {templates.length > 0 && (
          <div className="space-y-2">
            <Label className="font-mono text-sm">Saved Templates ({templates.length})</Label>
            <div className="space-y-2 max-h-64 overflow-y-auto border-2 rounded-lg p-2 bg-muted/30">
              {templates.map((template) => (
                <div
                  key={template.id}
                  className="flex items-center gap-2 p-2 rounded border-2 bg-card hover:bg-muted/50 transition-colors"
                >
                  <File className="h-4 w-4 text-muted-foreground flex-shrink-0" />
                  <div className="flex-1 min-w-0">
                    <div className="font-mono text-sm font-semibold truncate">
                      {template.name}
                    </div>
                    <div className="font-mono text-xs text-muted-foreground">
                      {new Date(template.updatedAt).toLocaleDateString()} • {template.content.content.length} blocks
                    </div>
                  </div>
                  <div className="flex gap-1 flex-shrink-0">
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => handleLoad(template)}
                      className="h-8 border-[hsl(var(--terminal-green))] text-[hsl(var(--terminal-green))] hover:bg-[hsl(var(--terminal-green))]/10"
                    >
                      LOAD
                    </Button>
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => handleExport(template)}
                      className="h-8"
                    >
                      <Download className="h-3 w-3" />
                    </Button>
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => handleDelete(template.id, template.name)}
                      className="h-8 text-destructive border-destructive/50 hover:bg-destructive/10"
                    >
                      <Trash2 className="h-3 w-3" />
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {templates.length === 0 && (
          <div className="text-center py-8 text-muted-foreground font-mono text-sm">
            No saved templates
            <div className="text-xs mt-1 opacity-60">Create a template and save it above</div>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
