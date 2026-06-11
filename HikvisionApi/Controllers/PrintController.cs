using HikvisionApi.Config;
using HikvisionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HikvisionApi.Controllers
{
    [ApiController]
    [Route("api/print")]
    public class PrintController : ControllerBase
    {
        private readonly PrintService _print;
        private readonly ParkSkyClient _parkSky;
        private readonly IConfiguration _config;
        private readonly HikvisionService _hikvisionService;
        private readonly ILogger<PrintController> _logger;

        public PrintController(
            PrintService print,
            ParkSkyClient parkSky,
            IConfiguration config,
            HikvisionService hikvisionService,
            ILogger<PrintController> logger)
        {
            _print = print;
            _parkSky = parkSky;
            _config = config;
            _hikvisionService = hikvisionService;
            _logger = logger;
        }

        // =============================================
        // POST /api/print/ticket
        // Llamado por ParkSky VPS después del ingreso
        // Imprime directo en impresora local sin pantalla
        // =============================================
        [HttpPost("ticket")]
        public async Task<IActionResult> ImprimirTicket([FromBody] PrintTicketDto dto)
        {
            // Validar API Key
            Request.Headers.TryGetValue("X-Api-Key", out var key);
            var keyConfig = _config["HikvisionApi:ApiKey"] ?? "";
            if (!string.IsNullOrEmpty(keyConfig) && key != keyConfig)
                return Unauthorized(new { ok = false });

            if (dto.RegistroId <= 0)
                return BadRequest(new { ok = false, mensaje = "RegistroId inválido" });

            try
            {
                // Obtener datos del ticket desde ParkSky
                var ticket = await _parkSky.ObtenerTicketAsync(dto.RegistroId);

                if (ticket == null || !ticket.Ok)
                    return NotFound(new { ok = false, mensaje = "Ticket no encontrado" });

                // Determinar impresora por carril
                var lane = dto.Lane ?? "1";
                var impresora = PrintService.ObtenerImpresora(lane, _config);

                // Si viene impresora específica, usarla
                if (!string.IsNullOrEmpty(dto.Impresora))
                    impresora = dto.Impresora;

                _logger.LogInformation("🖨️ Imprimiendo ticket {Id} en {Imp}", dto.RegistroId, impresora);

                // Imprimir directamente sin pantalla
                _print.ImprimirDesdeTicket(impresora, ticket);

                return Ok(new { ok = true, mensaje = $"Impreso en {impresora}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error imprimiendo ticket {Id}", dto.RegistroId);
                return StatusCode(500, new { ok = false, mensaje = ex.Message });
            }
        }

        // =============================================
        // POST /api/print/registrar-qr
        // Llamado por ParkSky en ingreso manual
        // Registra el QR en la controladora K2600
        // =============================================
        [HttpPost("registrar-qr")]
        public async Task<IActionResult> RegistrarQr([FromBody] RegistrarQrDto dto)
        {
            Request.Headers.TryGetValue("X-Api-Key", out var key);
            var keyConfig = _config["HikvisionApi:ApiKey"] ?? "";
            if (!string.IsNullOrEmpty(keyConfig) && key != keyConfig)
                return Unauthorized(new { ok = false });

            if (dto.RegistroId <= 0 || string.IsNullOrEmpty(dto.QrToken))
                return BadRequest(new { ok = false, mensaje = "Datos incompletos" });

            try
            {
                await _hikvisionService.RegistrarQrPublico(dto.RegistroId, dto.QrToken);
                _logger.LogInformation("📱 QR manual registrado: {Placa} Id={Id}",
                    dto.Placa, dto.RegistroId);
                return Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registrando QR manual");
                return StatusCode(500, new { ok = false, mensaje = ex.Message });
            }
        }

        // GET /api/print/impresoras
        // Lista las impresoras disponibles en el PC local
        [HttpGet("impresoras")]
        public IActionResult ListarImpresoras()
        {
            var impresoras = System.Drawing.Printing.PrinterSettings
                .InstalledPrinters
                .Cast<string>()
                .ToList();
            return Ok(new { ok = true, impresoras });
        }
    }

    public class PrintTicketDto
    {
        public int RegistroId { get; set; }
        public string? Lane { get; set; }
        public string? Impresora { get; set; }
    }
}

public class RegistrarQrDto
{
    public int RegistroId { get; set; }
    public string QrToken { get; set; } = "";
    public string Placa { get; set; } = "";
}