using Microsoft.AspNetCore.Mvc;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrinterController(IPrinterService printerService, PrintJobLog jobLog) : ControllerBase
{
    [HttpPost]
    [Journaled]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Print([FromBody] PrintRequest request)
    {
        List<PrintContent> content;

        if (request.Content != null && request.Content.Count > 0)
        {
            content = request.Content;
        }
        else if (!string.IsNullOrEmpty(request.Name) && !string.IsNullOrEmpty(request.Message))
        {
            content = SimpleNote.Build(request.Name, request.Message, request.ImageBase64);
        }
        else
        {
            // No job to send, but the caller is known: the same line, so a broken client shows up by name.
            var rejected = PrintResult.Invalid("Request must have Content array or both Name and Message");
            jobLog.Write(PrintJobLog.HttpTransport, request.Source, rejected, content: null, request.Options);
            return BadRequest(new PrintResponse(false, rejected.Error, PrintResponse.ValidationType));
        }

        var result = await printerService.PrintAsync(content, request.Options);
        jobLog.Write(PrintJobLog.HttpTransport, request.Source, result, content, request.Options);

        return this.ToResponse(result);
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(PrinterStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrinterStatus), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetStatus()
    {
        var status = await printerService.GetStatusAsync();
        return status.Reachable ? Ok(status) : StatusCode(503, status);
    }

    [HttpPost("beep")]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Beep([FromQuery] int count = 1, [FromQuery] int duration = 1)
    {
        var ok = await printerService.BeepAsync(count, duration);
        return ok
            ? Ok(new PrintResponse(true))
            : StatusCode(503, new PrintResponse(false, "Printer unreachable", PrintResponse.PrinterType));
    }
}
