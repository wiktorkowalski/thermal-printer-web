using Microsoft.AspNetCore.Mvc;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;

namespace ThermalPrinterWeb.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrinterController(ILogger<PrinterController> logger, IPrinterService printerService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Print([FromBody] PrintRequest request)
    {
        logger.LogInformation("Received print request");

        List<PrintContent> content;

        if (request.Content != null && request.Content.Count > 0)
        {
            content = request.Content;
        }
        else if (!string.IsNullOrEmpty(request.Name) && !string.IsNullOrEmpty(request.Message))
        {
            content = BuildSimpleContent(request.Name, request.Message, request.ImageBase64);
        }
        else
        {
            return BadRequest(new PrintResponse(false, "Request must have Content array or both Name and Message", "validation"));
        }

        var result = await printerService.PrintAsync(content, request.Options);

        if (!result.Success)
            return StatusCode(503, new PrintResponse(false, result.Error, "printer"));

        return Ok(new PrintResponse(true));
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(PrinterStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrinterStatus), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetStatus()
    {
        var status = await printerService.GetStatusAsync();
        logger.LogInformation(
            "Printer status requested: ready={Ready} ({Reason})",
            status.Ready, status.NotReadyReason ?? "ok");
        return status.Reachable ? Ok(status) : StatusCode(503, status);
    }

    private static List<PrintContent> BuildSimpleContent(string name, string message, string? imageBase64)
    {
        var content = new List<PrintContent>
        {
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
        };

        if (!string.IsNullOrEmpty(imageBase64))
        {
            content.Add(new PrintContent
            {
                Type = ContentType.Image,
                Content = imageBase64,
                ImageOptions = new ImageOptions { MaxWidth = 500, MaxHeight = 500 }
            });
            content.Add(new() { Type = ContentType.Separator });
        }

        content.Add(new() { Type = ContentType.LineFeed, Lines = 3 });
        content.Add(new() { Type = ContentType.Cut });

        return content;
    }
}
