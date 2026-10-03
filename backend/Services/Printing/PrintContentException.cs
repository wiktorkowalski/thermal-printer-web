namespace ThermalPrinterWeb.Services.Printing;

// A block the caller sent cannot be printed as given. The message goes back to
// the caller in the 400 body, so it must not repeat caller content.
internal sealed class PrintContentException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static PrintContentException OverLimit(string what, long value, long limit)
        => new($"{what} {value} is over the limit of {limit}");
}
