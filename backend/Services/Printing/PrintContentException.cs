namespace ThermalPrinterWeb.Services.Printing;

// A block the caller sent cannot be printed as given. The message goes back to
// the caller in the 400 body, so it must not repeat caller content.
internal sealed class PrintContentException(string message) : Exception(message);
