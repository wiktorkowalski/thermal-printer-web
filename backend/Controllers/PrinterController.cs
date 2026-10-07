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
    internal const string NoJobError = "Request must have Content array, Text or both Name and Message";

    internal static readonly string InvalidModeError = $"mode must be {SignalCommand.ModeNames}";

    [HttpPost]
    [Journaled]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Print([FromBody] PrintRequest request, [FromServices] TimeProvider clock)
    {
        List<PrintContent> content;

        if (!string.IsNullOrEmpty(request.Text))
        {
            if (HasAnotherMode(request))
                return Reject(request, StripMarkup.ConflictError);
            if (!StripMarkup.TryParseAlign(request.Align, out var align))
                return Reject(request, StripMarkup.AlignError);

            content = StripMarkup.Compile(request.Text, align);
            // Spaces only: no line to print. Without this the job is an empty strip and a success.
            if (content.Count == 0)
                return Reject(request, NoJobError);
        }
        else if (request.Content != null && request.Content.Count > 0)
        {
            content = request.Content;
        }
        else if (!string.IsNullOrEmpty(request.Name) && !string.IsNullOrEmpty(request.Message))
        {
            content = SimpleNote.Build(request.Name, request.Message, SimpleNote.Today(clock), request.ImageBase64);
        }
        else
        {
            return Reject(request, NoJobError);
        }

        var result = await printerService.PrintAsync(content, request.Options);
        jobLog.Write(PrintJobLog.HttpTransport, request.Source, result, content, request.Options);

        return this.ToResponse(result);
    }

    // Text mode goes alone: a field of another mode would not be printed, and the caller would not know.
    private static bool HasAnotherMode(PrintRequest request)
        => request.Content is { Count: > 0 }
            || !string.IsNullOrEmpty(request.Name)
            || !string.IsNullOrEmpty(request.Message)
            || !string.IsNullOrEmpty(request.ImageBase64);

    // No job to send, but the caller is known: the same line, so a broken client shows up by name.
    // The error is a fixed text: no caller content.
    private BadRequestObjectResult Reject(PrintRequest request, string error)
    {
        var rejected = PrintResult.Invalid(error);
        jobLog.Write(PrintJobLog.HttpTransport, request.Source, rejected, content: null, request.Options);
        return BadRequest(new PrintResponse(false, rejected.Error, PrintResponse.ValidationType));
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
    public async Task<IActionResult> Beep([FromQuery] int count = 1, [FromQuery] int duration = 1, [FromQuery] string? mode = null)
    {
        // Text, not the enum: the enum binder takes a number with no name and its error repeats the value.
        if (!SignalCommand.TryParseMode(mode, out var signalMode))
            return BadRequest(new PrintResponse(false, InvalidModeError, PrintResponse.ValidationType));

        var ok = await printerService.BeepAsync(count, duration, signalMode);
        return ok
            ? Ok(new PrintResponse(true))
            : StatusCode(503, new PrintResponse(false, "Printer unreachable", PrintResponse.PrinterType));
    }
}
