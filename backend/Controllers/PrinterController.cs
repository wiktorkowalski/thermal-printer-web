using Microsoft.AspNetCore.Mvc;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;

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
            content = SimpleNote.Build(request.Name, request.Message, request.ImageBase64);
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

    [HttpPost("beep")]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Beep([FromQuery] int count = 1, [FromQuery] int duration = 1)
    {
        var ok = await printerService.BeepAsync(count, duration);
        return ok
            ? Ok(new PrintResponse(true))
            : StatusCode(503, new PrintResponse(false, "Printer unreachable", "printer"));
    }
}
