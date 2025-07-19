using Microsoft.AspNetCore.Mvc;
using ThermalPrinterWeb;

namespace ThermalPrinterWeb.Controllers
{
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
    }

    public class PrinterRequest
    {
        public string Name { get; set; }
        public string Message { get; set; }
    }
}
