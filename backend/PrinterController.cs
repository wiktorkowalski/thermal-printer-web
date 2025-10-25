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
        await _printerService.PrintCustomContent(request.Content, request.Options);
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
    public List<CustomPrintContent> Content { get; set; } = new();
    public string? Source { get; set; }
    public PrintOptions? Options { get; set; }
}

public class PrintOptions
{
    public string? CodePage { get; set; } // "PC437", "PC858_EURO", "GBK"
    public int? DefaultLineSpacing { get; set; } // In dots
    public bool? AutoCut { get; set; } = true;
    public int? FeedLinesAfterPrint { get; set; } = 3;
}

public class CustomPrintContent
{
    // Common properties
    public CustomPrintContentType Type { get; set; }
    public string? Content { get; set; }
    public CustomPrintAlignment Alignment { get; set; } = CustomPrintAlignment.Center;

    // Text-specific
    public List<CustomPrintStyle>? Style { get; set; }

    // Type-specific options
    public BarcodeOptions? BarcodeOptions { get; set; }
    public QRCodeOptions? QRCodeOptions { get; set; }
    public ImageOptions? ImageOptions { get; set; }

    // LineFeed-specific
    public int? Lines { get; set; } = 1;

    // Cut-specific
    public bool? PartialCut { get; set; } = false;

    // Separator-specific
    public string? SeparatorChar { get; set; } = "=";
    public int? SeparatorLength { get; set; } = 32;
}

public class BarcodeOptions
{
    public BarcodeType Type { get; set; } = BarcodeType.CODE128;
    public int? HeightInDots { get; set; } = 100;
    public BarWidth? Width { get; set; } = BarWidth.Default;
    public BarLabelPosition? LabelPosition { get; set; } = BarLabelPosition.Below;
    public bool? UseFontB { get; set; } = false;
}

public class QRCodeOptions
{
    public QRCodeModel Model { get; set; } = QRCodeModel.Model2;
    public QRCodeSize Size { get; set; } = QRCodeSize.Normal;
    public QRCodeCorrectionLevel CorrectionLevel { get; set; } = QRCodeCorrectionLevel.Percent7;
}

public class ImageOptions
{
    public int? MaxWidth { get; set; } = 500;
    public int? MaxHeight { get; set; } = 500;
    public bool PreserveAspectRatio { get; set; } = true;
    public bool UseLegacyMode { get; set; } = true;
}

public enum CustomPrintContentType
{
    Text,
    Image,
    Barcode,
    QRCode,
    LineFeed,
    Cut,
    Separator,
    CodePage
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
    DoubleWidth,
    FontB
}

public enum BarcodeType
{
    UPC_A,
    UPC_E,
    EAN13,
    EAN8,
    CODE39,
    CODE128,
    ITF,
    CODABAR,
    GS1_128,
    GS1_DATABAR_OMNIDIRECTIONAL
}

public enum BarWidth
{
    Thin,
    Default,
    Thick
}

public enum BarLabelPosition
{
    None,
    Above,
    Below,
    Both
}

public enum QRCodeModel
{
    Model1,
    Model2,
    Micro
}

public enum QRCodeSize
{
    Normal,
    Large,
    ExtraLarge
}

public enum QRCodeCorrectionLevel
{
    Percent7,   // L - 7% recovery
    Percent15,  // M - 15% recovery
    Percent25,  // Q - 25% recovery
    Percent30   // H - 30% recovery
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
