using HikvisionApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace HikvisionApi.Controllers
{
    /// <summary>
    /// POST /api/print/abrir-caja
    /// Llamado desde el browser (LAN) después de confirmar un cobro.
    /// Envía el comando ESC/POS a la impresora para abrir la caja RJ11.
    /// </summary>
    [ApiController]
    [Route("api/print")]
    public class AbrirCajaController : ControllerBase
    {
        private readonly CajaRegistradoraService _caja;
        private readonly IConfiguration _config;

        public AbrirCajaController(
            CajaRegistradoraService caja,
            IConfiguration config)
        {
            _caja = caja;
            _config = config;
        }

        [HttpPost("abrir-caja")]
        public IActionResult AbrirCaja()
        {
            // Validar ApiKey igual que el resto de endpoints
            var apiKeyEsperada = _config["ParkSkySettings:ApiKey"] ?? "parksky-hik-2024";
            var apiKeyRecibida = Request.Headers["X-ApiKey"].FirstOrDefault() ?? "";

            if (apiKeyRecibida != apiKeyEsperada)
                return Unauthorized(new { ok = false, mensaje = "ApiKey inválida." });

            var ok = _caja.AbrirCaja();
            return Ok(new { ok, mensaje = ok ? "Caja abierta." : "No se pudo abrir la caja — revisa PrinterSettings:Nombre." });
        }
    }
}
