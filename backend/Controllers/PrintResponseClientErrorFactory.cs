using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Controllers;

// Without this MVC answers a status with no body (415 for a wrong Content-Type) as ProblemDetails, not in the shape of every other print error.
internal sealed class PrintResponseClientErrorFactory : IClientErrorFactory
{
    private const string UnsupportedMediaTypeError = "Content-Type must be application/json";

    public IActionResult GetClientError(ActionContext actionContext, IClientErrorActionResult clientError)
    {
        var status = clientError.StatusCode ?? StatusCodes.Status400BadRequest;

        // Fixed text: the Content-Type that came in is caller content.
        var error = status == StatusCodes.Status415UnsupportedMediaType
            ? UnsupportedMediaTypeError
            : ReasonPhrases.GetReasonPhrase(status);

        return new ObjectResult(new PrintResponse(false, error, PrintResponse.ValidationType)) { StatusCode = status };
    }
}
