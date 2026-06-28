using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// Shared by the HTTP simple-print path and the MCP print_note tool so the two
// render identically.
internal static class SimpleNote
{
    private const int SimpleImageMaxSize = 500;

    public static List<PrintContent> Build(string name, string message, string? imageBase64 = null)
    {
        List<PrintContent> content =
        [
            new() { Type = ContentType.Separator, SeparatorChar = "=", SeparatorLength = 32 },
            new()
            {
                Type = ContentType.Text,
                Content = name,
                Alignment = Alignment.Center,
                Style = [PrintStyle.DoubleWidth, PrintStyle.DoubleHeight]
            },
            new() { Type = ContentType.Separator },
            new()
            {
                Type = ContentType.Text,
                Content = message,
                Alignment = Alignment.Center
            },
            new() { Type = ContentType.Separator }
        ];

        if (!string.IsNullOrEmpty(imageBase64))
        {
            content.Add(new PrintContent
            {
                Type = ContentType.Image,
                Content = imageBase64,
                ImageOptions = new ImageOptions { MaxWidth = SimpleImageMaxSize, MaxHeight = SimpleImageMaxSize }
            });
            content.Add(new() { Type = ContentType.Separator });
        }

        content.Add(new() { Type = ContentType.LineFeed, Lines = 3 });
        content.Add(new() { Type = ContentType.Cut });

        return content;
    }
}
