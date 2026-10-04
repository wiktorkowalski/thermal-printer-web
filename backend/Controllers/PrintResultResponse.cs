using Microsoft.AspNetCore.Mvc;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Controllers;

// The HTTP answer for a print result: the same for a new job and for a reprint.
internal static class PrintResultResponse
{
    // A full decode queue clears in a few seconds; so does a reprint that runs.
    private const string BusyRetryAfterSeconds = "5";

    public static IActionResult ToResponse(this ControllerBase controller, PrintResult result)
    {
        if (result.Success)
            return controller.Ok(new PrintResponse(true));

        // 503 tells clients to retry; a payload fault never gets better on retry.
        switch (result.Failure)
        {
            case PrintFailure.Validation:
                return controller.BadRequest(new PrintResponse(false, result.Error, PrintResponse.ValidationType));
            case PrintFailure.Busy:
                controller.Response.Headers.RetryAfter = BusyRetryAfterSeconds;
                return controller.StatusCode(StatusCodes.Status503ServiceUnavailable, new PrintResponse(false, result.Error, PrintResponse.BusyType));
            default:
                return controller.StatusCode(StatusCodes.Status503ServiceUnavailable, new PrintResponse(false, result.Error, PrintResponse.PrinterType));
        }
    }
}
