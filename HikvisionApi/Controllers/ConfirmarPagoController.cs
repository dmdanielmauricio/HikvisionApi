using HikvisionApi.Data;
using HikvisionApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HikvisionApi.Controllers
{
    // ─────────────────────────────────────────────────────────────────────
    // Endpoint llamado desde el VPS (ControlController.Cobrar) al confirmar
    // un pago. Registra el pago en la tabla local PagosConfirmados para que
    // SalidaParqueaderoNube pueda autorizar la salida sin llamar al VPS.
    //
    // VPS llama: POST http://10.20.34.158:85/api/print/confirmar-pago
    // Header:    X-ApiKey: parksky-hik-2024
    // Body JSON: { placa, registroId, valorPagado, qrToken, datosTiqueteJson }
    //
    // [IgnoreAntiforgeryToken] requerido porque el VPS llama sin cookie
    // de sesión (misma razón que HikvisionApiController en el VPS).
    // ─────────────────────────────────────────────────────────────────────
    [ApiController]
    [Route("api/print")]
    [IgnoreAntiforgeryToken]
    public class ConfirmarPagoController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly ILogger<ConfirmarPagoController> _logger;

        public ConfirmarPagoController(
            AppDbContext db,
            IConfiguration config,
            ILogger<ConfirmarPagoController> logger)
        {
            _db = db;
            _config = config;
            _logger = logger;
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /api/print/confirmar-pago
        // Registra el pago en PagosConfirmados.
        // FechaExpira = FechaPago + TiempoGracia + 5 min de margen, para
        // que el vehículo pueda salir hasta ese momento sin consultar VPS.
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("confirmar-pago")]
        public async Task<IActionResult> ConfirmarPago([FromBody] ConfirmarPagoDto dto)
        {
            // Validar ApiKey
            var apiKey = Request.Headers["X-ApiKey"].FirstOrDefault()
                      ?? Request.Query["apiKey"].FirstOrDefault();
            var apiKeyEsperado = _config["ParkSkySettings:ApiKey"] ?? "parksky-hik-2024";

            if (apiKey != apiKeyEsperado)
            {
                _logger.LogWarning("\ud83d\udeab confirmar-pago rechazado: ApiKey inválida");
                return Unauthorized(new { ok = false, error = "ApiKey inválida" });
            }

            if (string.IsNullOrWhiteSpace(dto.Placa))
                return BadRequest(new { ok = false, error = "Placa requerida" });

            var graciaMinutos = _config.GetValue<int>("Parqueadero:TiempoGraciaMinutos", 15);
            var fechaPago = dto.FechaPago ?? DateTime.Now;
            var fechaExpira = fechaPago.AddMinutes(graciaMinutos + 5);

            try
            {
                _db.PagosConfirmados.Add(new PagoConfirmado
                {
                    Placa = dto.Placa.ToUpper().Trim(),
                    RegistroVpsId = dto.RegistroId,
                    ValorPagado = dto.ValorPagado,
                    FechaPago = fechaPago,
                    FechaExpira = fechaExpira,
                    QrToken = dto.QrToken,
                    DatosTiqueteJson = dto.DatosTiqueteJson
                });
                await _db.SaveChangesAsync();

                _logger.LogInformation(
                    "\u2705 PagoConfirmado: {Placa} Reg={Reg} Valor={Valor} Expira={Exp}",
                    dto.Placa, dto.RegistroId, dto.ValorPagado, fechaExpira);

                return Ok(new
                {
                    ok = true,
                    placa = dto.Placa.ToUpper().Trim(),
                    fechaExpira
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error guardando PagoConfirmado: {Placa}", dto.Placa);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/print/pagos-pendientes (diagnóstico)
        // Muestra los pagos confirmados localmente aún vigentes.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("pagos-pendientes")]
        public async Task<IActionResult> PagosPendientes()
        {
            var vigentes = await _db.PagosConfirmados
                .Where(p => p.FechaExpira >= DateTime.Now)
                .OrderByDescending(p => p.FechaPago)
                .Select(p => new
                {
                    p.Placa,
                    p.RegistroVpsId,
                    p.ValorPagado,
                    p.FechaPago,
                    p.FechaExpira,
                    minutosRestantes = (int)(p.FechaExpira - DateTime.Now).TotalMinutes
                })
                .ToListAsync();

            return Ok(new { ok = true, count = vigentes.Count, pagos = vigentes });
        }
    }

    public class ConfirmarPagoDto
    {
        public string Placa { get; set; } = "";
        public int? RegistroId { get; set; }
        public decimal ValorPagado { get; set; }
        public DateTime? FechaPago { get; set; }
        public string? QrToken { get; set; }
        // JSON serializado de TicketResponse — para impresión offline (Fase 4)
        public string? DatosTiqueteJson { get; set; }
    }
}