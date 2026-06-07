using HikvisionApi.Config;
using HikvisionApi.Data;
using HikvisionApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;

namespace HikvisionApi.Services
{
    public class HikvisionService
    {
        private readonly AppDbContext _db;
        private readonly AnprSettings _anpr;
        private readonly BarrierSettings _barrier;
        private readonly PorteriaSettings _porteria;
        private readonly ParqueaderoSettings _parqueadero;
        private readonly ParkSkySettings _parkSkySettings;
        private readonly ParkSkyClient _parkSky;
        private readonly PrintService _print;
        private readonly IConfiguration _config;
        private readonly ILogger<HikvisionService> _logger;
        private readonly string _modo;

        public HikvisionService(
            IOptions<AnprSettings> anpr,
            IOptions<BarrierSettings> barrier,
            IOptions<PorteriaSettings> porteria,
            IOptions<ParqueaderoSettings> parqueadero,
            IOptions<ParkSkySettings> parkSkySettings,
            AppDbContext db,
            ParkSkyClient parkSky,
            PrintService print,
            IConfiguration config,
            ILogger<HikvisionService> logger)
        {
            _anpr = anpr.Value;
            _barrier = barrier.Value;
            _porteria = porteria.Value;
            _parqueadero = parqueadero.Value;
            _parkSkySettings = parkSkySettings.Value;
            _db = db;
            _parkSky = parkSky;
            _print = print;
            _config = config;
            _logger = logger;
            _modo = config["ModoOperacion"] ?? "Porteria";
        }

        // =============================================
        // GUARDAR RAW
        // =============================================
        public async Task GuardarRaw(
            IFormCollection form, string contentType, string method)
        {
            Directory.CreateDirectory(_anpr.RawFolder);
            Directory.CreateDirectory(_anpr.LogsFolder);

            var logFile = Path.Combine(_anpr.LogsFolder, "api_log.txt");
            using var log = new StreamWriter(logFile, true);
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            await log.WriteLineAsync($"[{now}] ========= NUEVO REQUEST =========");
            await log.WriteLineAsync($"[{now}] Modo: {_modo}");

            foreach (var file in form.Files)
            {
                var cleanName = Path.GetFileName(file.FileName);
                var filePath = Path.Combine(_anpr.RawFolder, cleanName);
                using (var s = new FileStream(filePath, FileMode.Create))
                    await file.CopyToAsync(s);
                await log.WriteLineAsync($"[{now}] {cleanName} ({file.Length} bytes)");
            }
            await log.WriteLineAsync("");
        }

        // =============================================
        // PROCESAR ACCESO — entrada principal
        // =============================================
        public async Task ProcesarAcceso(
            string placa, string lane, string absTime,
            IFormFile? plateImage, IFormFile? fullImage)
        {
            _logger.LogInformation("📸 {Placa} carril {Lane} modo {Modo}", placa, lane, _modo);

            if (string.IsNullOrEmpty(absTime) || absTime.Length < 17)
                absTime = DateTime.Now.ToString("yyyyMMddHHmmssfff");

            var imagenUrl = await GuardarImagenes(placa, lane, absTime, plateImage, fullImage);

            bool esEntrada = lane == "1" || lane == "3";
            string carrilNombre = lane switch
            {
                "1" => "Entrada1",
                "2" => "Salida1",
                "3" => "Entrada2",
                "4" => "Salida2",
                _ => $"Carril{lane}"
            };

            switch (_modo)
            {
                case "Porteria":
                    await ProcesarPorteria(placa, lane, imagenUrl, carrilNombre, esEntrada);
                    break;
                case "Parqueadero":
                    if (esEntrada)
                        await ProcesarParqueaderoEntrada(placa, lane, imagenUrl, carrilNombre);
                    else
                        await ProcesarParqueaderoSalida(placa, lane, imagenUrl, carrilNombre);
                    break;
                default:
                    _logger.LogWarning("ModoOperacion desconocido: {Modo}", _modo);
                    break;
            }
        }

        // =============================================
        // PORTERÍA — local o nube
        // =============================================
        private async Task ProcesarPorteria(
            string placa, string lane, string imagenUrl,
            string carrilNombre, bool esEntrada)
        {
            string tipo = esEntrada ? "ENTRADA" : "SALIDA";
            bool autorizado;

            if (_porteria.FuenteDatos == "Local")
            {
                // Validar contra BD local
                var vehiculo = await _db.Vehiculos
                    .FirstOrDefaultAsync(v => v.Placa == placa);
                autorizado = vehiculo != null && vehiculo.Activo;

                _logger.LogInformation("Portería LOCAL: {Placa} → {Auth}", placa, autorizado);
            }
            else
            {
                // Validar contra ParkSky nube
                try
                {
                    var conv = await _parkSky.ValidarConvenioAsync(placa);
                    autorizado = conv.TieneConvenio && conv.Activo;
                    _logger.LogInformation("Portería NUBE: {Placa} → {Auth}", placa, autorizado);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sin internet — portería nube fallback");
                    autorizado = _porteria.AbrirSiSinInternet;
                }
            }

            await RegistrarAccesoLocal(placa, lane, tipo, autorizado,
                autorizado ? "OK" : "NO_AUTORIZADO", "PORTERIA", imagenUrl);

            if (autorizado)
                await EjecutarApertura(lane, tipo);
            else
                _logger.LogWarning("⛔ Portería: {Placa} NO autorizado", placa);
        }

        // =============================================
        // PARQUEADERO ENTRADA
        // =============================================
        private async Task ProcesarParqueaderoEntrada(
            string placa, string lane, string imagenUrl, string carrilNombre)
        {
            string impresora = PrintService.ObtenerImpresora(lane, _config);

            if (_parqueadero.FuenteDatos == "Local")
            {
                await EntradaParqueaderoLocal(placa, lane, imagenUrl, carrilNombre, impresora);
            }
            else
            {
                await EntradaParqueaderoNube(placa, lane, imagenUrl, carrilNombre, impresora);
            }
        }

        // -- Entrada local --
        private async Task EntradaParqueaderoLocal(
            string placa, string lane, string imagenUrl,
            string carrilNombre, string impresora)
        {
            if (_parqueadero.AbrirTodo)
            {
                // Registrar en BD local y abrir
                await RegistrarIngresoLocal(placa, lane, false, null);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true, "ABRE_TODO", "LIBRE", imagenUrl);
                await EjecutarApertura(lane, "ENTRADA");
                return;
            }

            // Validar convenio en BD local
            var convenio = await _db.ConveniosVehiculos
                .Include(cv => cv.ConvenioMensualidad)
                .FirstOrDefaultAsync(cv =>
                    cv.Placa == placa &&
                    cv.Activo &&
                    cv.ConvenioMensualidad.FechaFin >= DateTime.Today);

            if (convenio != null)
            {
                await RegistrarIngresoLocal(placa, lane, true, convenio.ConvenioMensualidadId);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true, "CONVENIO_ACTIVO", "CONVENIO", imagenUrl);
                await EjecutarApertura(lane, "ENTRADA");
            }
            else
            {
                // Casual — imprimir tiquete
                await RegistrarIngresoLocal(placa, lane, false, null);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true, "CASUAL", "CASUAL", imagenUrl);

                if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
                    _print.ImprimirTiqueteLocal(impresora, placa, "Vehículo",
                        DateTime.Now, carrilNombre, false);

                await EjecutarApertura(lane, "ENTRADA");
            }
        }

        // -- Entrada nube --
        private async Task EntradaParqueaderoNube(
            string placa, string lane, string imagenUrl,
            string carrilNombre, string impresora)
        {
            if (_parqueadero.AbrirTodo)
            {
                try
                {
                    await _parkSky.RegistrarIngresoAsync(placa, lane, carrilNombre, false, null, imagenUrl);
                }
                catch { /* sin internet — igual abre */ }

                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true, "ABRE_TODO", "LIBRE", imagenUrl);
                await EjecutarApertura(lane, "ENTRADA");
                return;
            }

            try
            {
                var conv = await _parkSky.ValidarConvenioAsync(placa);

                if (conv.TieneConvenio && conv.Activo)
                {
                    await _parkSky.RegistrarIngresoAsync(placa, lane, carrilNombre,
                        true, conv.ConvenioId, imagenUrl);
                    await RegistrarAccesoLocal(placa, lane, "ENTRADA", true,
                        "CONVENIO_ACTIVO", "CONVENIO", imagenUrl);
                    await EjecutarApertura(lane, "ENTRADA");
                }
                else if (conv.TieneConvenio && !conv.Activo)
                {
                    // Convenio vencido → tarifa normal
                    await _parkSky.RegistrarIngresoAsync(placa, lane, carrilNombre,
                        false, null, imagenUrl);
                    await RegistrarAccesoLocal(placa, lane, "ENTRADA", true,
                        "CONVENIO_VENCIDO", "CASUAL", imagenUrl);

                    if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
                        await ImprimirDesdeParkskyOLocal(impresora, placa,
                            conv.TipoVehiculo ?? "Vehículo", carrilNombre, null);

                    await EjecutarApertura(lane, "ENTRADA");
                }
                else
                {
                    // Sin convenio — casual
                    var ingresoCs = await _parkSky.RegistrarIngresoAsync(placa, lane, carrilNombre,
                        false, null, imagenUrl);
                    await RegistrarAccesoLocal(placa, lane, "ENTRADA", true,
                        "SIN_CONVENIO", "CASUAL", imagenUrl);

                    if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
                        await ImprimirDesdeParkskyOLocal(impresora, placa,
                            "Vehículo", carrilNombre, ingresoCs.RegistroId);

                    await EjecutarApertura(lane, "ENTRADA");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sin internet — parqueadero entrada fallback");
                await RegistrarAccesoLocal(placa, lane, "ENTRADA",
                    _parqueadero.AbrirSiSinInternet, "SIN_INTERNET", "CASUAL", imagenUrl);

                if (_parqueadero.AbrirSiSinInternet)
                    await EjecutarApertura(lane, "ENTRADA");
            }
        }

        // =============================================
        // PARQUEADERO SALIDA
        // =============================================
        private async Task ProcesarParqueaderoSalida(
            string placa, string lane, string imagenUrl, string carrilNombre)
        {
            if (_parqueadero.FuenteDatos == "Local")
                await SalidaParqueaderoLocal(placa, lane, imagenUrl);
            else
                await SalidaParqueaderoNube(placa, lane, imagenUrl, carrilNombre);
        }

        // -- Salida local --
        private async Task SalidaParqueaderoLocal(
            string placa, string lane, string imagenUrl)
        {
            // Convenio activo local
            var convenio = await _db.ConveniosVehiculos?
                .Include(cv => cv.ConvenioMensualidad)
                .FirstOrDefaultAsync(cv =>
                    cv.Placa == placa && cv.Activo &&
                    cv.ConvenioMensualidad.FechaFin >= DateTime.Today);

            if (convenio != null)
            {
                await CerrarRegistroLocal(placa);
                await RegistrarAccesoLocal(placa, lane, "SALIDA", true,
                    "CONVENIO_ACTIVO", "CONVENIO", imagenUrl);
                await EjecutarApertura(lane, "SALIDA");
                return;
            }

            // Verificar pago (ValorPagado > 0 y Activo = false en registro reciente)
            var registro = await _db.Registros
                .OrderByDescending(r => r.FechaEntrada)
                .FirstOrDefaultAsync(r => r.Placa == placa && !r.Activo
                    && r.ValorPagado > 0
                    && r.FechaSalida.HasValue
                    && r.FechaSalida.Value >= DateTime.Today);

            if (registro != null)
            {
                await RegistrarAccesoLocal(placa, lane, "SALIDA", true,
                    "PAGADO", "CASUAL", imagenUrl);
                await EjecutarApertura(lane, "SALIDA");
            }
            else
            {
                await RegistrarAccesoLocal(placa, lane, "SALIDA", false,
                    "NO_PAGADO", "BLOQUEADO", imagenUrl);
                _logger.LogWarning("⛔ Salida bloqueada (sin pago local): {Placa}", placa);
            }
        }

        // -- Salida nube --
        private async Task SalidaParqueaderoNube(
            string placa, string lane, string imagenUrl, string carrilNombre)
        {
            try
            {
                // Convenio activo nube
                var conv = await _parkSky.ValidarConvenioAsync(placa);
                if (conv.TieneConvenio && conv.Activo)
                {
                    await _parkSky.ValidarSalidaAsync(placa, lane, imagenUrl, carrilNombre);
                    await RegistrarAccesoLocal(placa, lane, "SALIDA", true,
                        "CONVENIO_ACTIVO", "CONVENIO", imagenUrl);
                    await EjecutarApertura(lane, "SALIDA");
                    return;
                }

                var salida = await _parkSky.ValidarSalidaAsync(
                    placa, lane, imagenUrl, carrilNombre);

                if (salida.Autorizado)
                {
                    await RegistrarAccesoLocal(placa, lane, "SALIDA", true,
                        salida.Motivo ?? "PAGADO", "CASUAL", imagenUrl);
                    await EjecutarApertura(lane, "SALIDA");
                }
                else
                {
                    await RegistrarAccesoLocal(placa, lane, "SALIDA", false,
                        "NO_PAGADO", "BLOQUEADO", imagenUrl);
                    _logger.LogWarning("⛔ Salida bloqueada (sin pago nube): {Placa}", placa);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sin internet — salida fallback");
                await RegistrarAccesoLocal(placa, lane, "SALIDA",
                    _parqueadero.AbrirSiSinInternet, "SIN_INTERNET", "CASUAL", imagenUrl);

                if (_parqueadero.AbrirSiSinInternet)
                    await EjecutarApertura(lane, "SALIDA");
            }
        }

        // =============================================
        // APERTURA CON TIMER
        // =============================================
        private async Task EjecutarApertura(string lane, string tipo)
        {
            int ms;

            if (_modo == "Porteria")
            {
                // Portería usa timer de entrada para ambos sentidos
                ms = _porteria.TimerSegundos * 1000;
            }
            else
            {
                ms = tipo == "ENTRADA"
                    ? _parqueadero.TimerEntradaSegundos * 1000
                    : _parqueadero.TimerSalidaSegundos * 1000;
            }

            if (ms > 0)
            {
                _logger.LogInformation("⏱ Timer {Ms}ms antes de abrir", ms);
                await Task.Delay(ms);
            }

            await AbrirBarrera(lane);
        }

        private async Task AbrirBarrera(string lane)
        {
            string doorId = lane switch
            {
                "1" => _barrier.Doors.Entrada1,
                "2" => _barrier.Doors.Salida1,
                "3" => _barrier.Doors.Entrada2,
                "4" => _barrier.Doors.Salida2,
                _ => _barrier.Doors.Entrada1
            };

            try
            {
                var handler = new HttpClientHandler
                {
                    Credentials = new NetworkCredential(_barrier.Username, _barrier.Password),
                    PreAuthenticate = true,
                    UseCookies = true,
                    CookieContainer = new CookieContainer()
                };

                using var client = new HttpClient(handler);
                await client.GetAsync(_barrier.BaseUrl.Replace(
                    "/AccessControl/RemoteControl/door/", "/System/status"));

                var xml = "<RemoteControlDoor><cmd>open</cmd></RemoteControlDoor>";
                var r = await client.PutAsync(
                    _barrier.BaseUrl + doorId,
                    new StringContent(xml, Encoding.UTF8, "application/xml"));

                _logger.LogInformation("🚪 Barrera {Door} → {Status}", doorId, r.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error abriendo barrera {Door}", doorId);
            }
        }

        // =============================================
        // HELPERS
        // =============================================
        private async Task<string> GuardarImagenes(
            string placa, string lane, string absTime,
            IFormFile? plateImage, IFormFile? fullImage)
        {
            string fecha = absTime.Substring(0, 8);
            string nombre = $"{absTime}_{placa}_{lane}.jpg";
            string carpeta = Path.Combine(_anpr.TargetFolder, $"Camara{lane}", fecha);
            string carpetaX = Path.Combine(_anpr.TargetFolder, $"Camara{lane}X", fecha);

            Directory.CreateDirectory(carpeta);
            Directory.CreateDirectory(carpetaX);

            if (plateImage != null)
            {
                using var s = new FileStream(Path.Combine(carpeta, nombre), FileMode.Create);
                await plateImage.CopyToAsync(s);
            }
            if (fullImage != null)
            {
                using var s = new FileStream(Path.Combine(carpetaX, nombre), FileMode.Create);
                await fullImage.CopyToAsync(s);
            }

            return $"/anpr/Camara{lane}/{fecha}/{nombre}";
        }

        private async Task RegistrarAccesoLocal(
            string placa, string carril, string tipo,
            bool autorizado, string motivo, string tipoAcceso, string? imagenUrl)
        {
            _db.AccesosVehiculares.Add(new AccesoVehicular
            {
                Placa = placa,
                Carril = carril,
                FechaHora = DateTime.Now,
                Autorizado = autorizado,
                TipoMovimiento = tipo,
                Motivo = motivo,
                ImagenUrl = imagenUrl ?? ""
            });
            await _db.SaveChangesAsync();
        }

        private async Task RegistrarIngresoLocal(
            string placa, string lane, bool esMensualidad, int? convenioId)
        {
            var vehiculo = await _db.Vehiculos.FirstOrDefaultAsync(v => v.Placa == placa);
            if (vehiculo == null)
            {
                vehiculo = new Vehiculo
                {
                    Placa = placa,
                    Tipo = DetectarTipoVehiculo(placa),
                    Activo = true,
                    PropietarioId = 0,
                    EsPrivado = false
                };
                _db.Vehiculos.Add(vehiculo);
                await _db.SaveChangesAsync();
            }

            // Solo registrar en BD local — no tiene RegistroParqueo completo
            // El registro real está en ParkSky nube si FuenteDatos = "Nube"
            _logger.LogInformation("Ingreso local registrado: {Placa}", placa);
        }


        // =============================================
        // IMPRIMIR DESDE PARKSKY O FALLBACK LOCAL
        // =============================================
        private async Task ImprimirDesdeParkskyOLocal(
            string impresora, string placa, string tipo,
            string carrilNombre, int? registroId)
        {
            if (registroId.HasValue && registroId.Value > 0)
            {
                try
                {
                    var ticket = await _parkSky.ObtenerTicketAsync(registroId.Value);
                    if (ticket != null && ticket.Ok)
                    {
                        _print.ImprimirDesdeTicket(impresora, ticket);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo obtener ticket de ParkSky, usando fallback");
                }
            }

            // Fallback — imprimir con datos locales
            _print.ImprimirTiqueteLocal(
                impresora, placa, tipo, DateTime.Now, carrilNombre, false);
        }

        private async Task CerrarRegistroLocal(string placa)
        {
            _logger.LogInformation("Salida local registrada: {Placa}", placa);
        }

        private static string DetectarTipoVehiculo(string placa)
        {
            // Moto Colombia: AA000A (2 letras, 3 números, 1 letra)
            if (placa.Length == 6 &&
                char.IsLetter(placa[0]) && char.IsLetter(placa[1]) &&
                char.IsDigit(placa[2]) && char.IsDigit(placa[3]) &&
                char.IsDigit(placa[4]) && char.IsLetter(placa[5]))
                return "Moto";

            return "Carro";
        }
    }
}