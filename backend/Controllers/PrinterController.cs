using Microsoft.AspNetCore.Mvc;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrinterController(IPrinterService printerService, PrintJobLog jobLog) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
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
            jobLog.WriteRejected(PrintJobLog.HttpTransport, request.Source);
            return BadRequest(new PrintResponse(false, "Request must have Content array or both Name and Message", PrintResponse.ValidationType));
        }

        var result = await printerService.PrintAsync(content, request.Options);
        jobLog.Write(PrintJobLog.HttpTransport, request.Source, result);

        if (result.Success)
            return Ok(new PrintResponse(true));

        // 503 tells clients to retry; a payload fault never gets better on retry.
        return result.Failure == PrintFailure.Validation
            ? BadRequest(new PrintResponse(false, result.Error, PrintResponse.ValidationType))
            : StatusCode(503, new PrintResponse(false, result.Error, PrintResponse.PrinterType));
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
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Beep([FromQuery] int count = 1, [FromQuery] int duration = 1)
    {
        var ok = await printerService.BeepAsync(count, duration);
        return ok
            ? Ok(new PrintResponse(true))
            : StatusCode(503, new PrintResponse(false, "Printer unreachable", PrintResponse.PrinterType));
    }
}
