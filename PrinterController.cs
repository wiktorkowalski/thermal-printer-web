using Microsoft.AspNetCore.Mvc;

namespace ThermalPrinterWeb.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PrinterController : ControllerBase
    {
        [HttpPost]
        public IActionResult Post([FromBody] PrinterRequest request)
        {
            // TODO: Implement printer logic here
            return Ok();
        }
    }

    public class PrinterRequest
    {
        public string Name { get; set; }
        public string Message { get; set; }
    }
}
