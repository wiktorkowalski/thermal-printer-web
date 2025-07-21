using Microsoft.AspNetCore.Mvc;

namespace ThermalPrinterWeb.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PrinterController : ControllerBase
{
    private readonly ILogger<PrinterController> _logger;
    private readonly IPrinterService _printerService;

    public PrinterController(ILogger<PrinterController> logger, IPrinterService printerService)
    {
        _logger = logger;
        _printerService = printerService;
    }

    [HttpPost]
    public IActionResult Post([FromBody] PrinterRequest request)
    {
        _logger.LogInformation("Received print request for {Name} with message {Message}", request.Name, request.Message);
        _printerService.Print(request.Name, request.Message);
        return Ok();
    }

    [HttpPost("image")]
    public async Task<IActionResult> PostImage([FromForm] ImagePrinterRequest request)
    {
        _logger.LogInformation("Received print request with image for {Name} with {Message}", request.Name, request.Message);

        if (request.Image == null || request.Image.Length == 0)
        {
            await _printerService.Print(request.Name, request.Message);
            return Ok("No image provided, printed message only.");
        }
        
        if (request.Image.Length > 1024 * 1024 * 50) // Limit image size to 50MB
        {
            _logger.LogError("Image file is too large: {FileSize} bytes", request.Image.Length);
            return BadRequest("Image file is too large.");
        }
        
        // Log the actual content type for debugging
        _logger.LogInformation("Image file {FileName} received with ContentType: '{ContentType}', size {FileSize} bytes", 
            request.Image.FileName, request.Image.ContentType, request.Image.Length);
        
        // More flexible image validation - check both content type and file extension
        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff" };
        var fileExtension = Path.GetExtension(request.Image.FileName).ToLower();
        
        bool isValidContentType = !string.IsNullOrEmpty(request.Image.ContentType) && 
                                request.Image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        bool isValidExtension = allowedExtensions.Contains(fileExtension);
        
        if (!isValidContentType && !isValidExtension)
        {
            _logger.LogError("Invalid image file. ContentType: '{ContentType}', Extension: '{Extension}'", 
                request.Image.ContentType, fileExtension);
            return BadRequest($"Invalid image file type. ContentType: '{request.Image.ContentType}', Extension: '{fileExtension}'");
        }
        using (var memoryStream = new MemoryStream())
        {
            await request.Image.OpenReadStream().CopyToAsync(memoryStream);
            await _printerService.Print(request.Name, request.Message, memoryStream.ToArray());
        }
        return Ok();
}

[HttpPost("custom")]
public async Task<IActionResult> PrintCustom([FromBody] CustomPrintRequest request)
{
    try
    {
        await _printerService.PrintCustomContent(request.Content);
        return Ok();
    }
    catch (Exception e)
    {
        return BadRequest(e.Message);
    }
}
}

public class CustomPrintRequest
{
    public List<CustomPrintContent> Content { get; set; }
    public string Source { get; set; }
}

public class CustomPrintContent
{
    public CustomPrintContentType Type { get; set; }
    public string Content { get; set; }
    public CustomPrintAlignment Alignment { get; set; } = CustomPrintAlignment.Center;
    public CustomPrintStyle Style { get; set; } = CustomPrintStyle.Bold | CustomPrintStyle.DoubleHeight | CustomPrintStyle.DoubleWidth;
}

public enum CustomPrintContentType
{
    Text,
    Image,
    Barcode,
    Qrcode
}

public enum CustomPrintAlignment
{
    Left,
    Center,
    Right
}

public enum CustomPrintStyle
{
    Normal,
    Bold,
    Italic,
    Underline,
    DoubleHeight,
    DoubleWidth
}

public class PrinterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class ImagePrinterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public IFormFile? Image { get; set; }
}
