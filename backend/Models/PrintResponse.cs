using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ThermalPrinterWeb.Models;

public record PrintResponse(bool Success, string? Error = null, string? Type = null)
{
    public const string ValidationType = "validation";
    public const string PrinterType = "printer";

    // One entry per bad field, e.g. "$.content[0].type: The JSON value could not be converted ...".
    public static PrintResponse FromModelState(ModelStateDictionary modelState)
    {
        var errors = modelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .SelectMany(entry => entry.Value!.Errors.Select(error =>
            {
                var message = string.IsNullOrEmpty(error.ErrorMessage) ? "The value is not valid." : error.ErrorMessage;
                return string.IsNullOrEmpty(entry.Key) ? message : $"{entry.Key}: {message}";
            }));

        var text = string.Join("; ", errors);
        return new PrintResponse(false, text.Length > 0 ? text : "The request is not valid.", ValidationType);
    }
}
