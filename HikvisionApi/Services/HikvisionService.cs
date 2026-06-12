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

            // ── FILTRO DE PLACA ──────────────────────────────────────────
            // 1. Descartar placas no reconocidas por la cámara
            if (_anpr.PlacasDescartadas.Any(d =>
                    placa.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                    placa.Contains(d, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("🚫 Placa descartada (no reconocida): {Placa}", placa);
                return;
            }
            // 2. Descartar lecturas parciales o muy cortas
            if (placa.Length < _anpr.LongitudMinimaPlaca)
            {
                _logger.LogWarning("🚫 Placa descartada (muy corta {Len} chars): {Placa}",
                    placa.Length, placa);
                return;
            }
            // 3. Validar que la placa tenga solo caracteres válidos (letras y números)
            if (!placa.All(c => char.IsLetterOrDigit(c)))
            {
                _logger.LogWarning("🚫 Placa descartada (caracteres inválidos): {Placa}", placa);
                return;
            }

            // 4. Validar formato colombiano — descartar si no coincide con ningún
            //    patrón personalizado ni con los formatos estándar
            var tipoDetectado = DetectarTipoPlacaColombia(placa);
            if (tipoDetectado == null)
            {
                // Verificar si coincide con algún patrón personalizado en BD
                bool tienePatronPersonalizado = false;
                try
                {
                    var patrones = _db?.PatronPlacas?
                        .Where(p => p.Activo).ToList();
                    if (patrones != null)
                        tienePatronPersonalizado = patrones.Any(p =>
                            CoincidePatronLocal(placa, p.Patron));
                }
                catch { }

                if (!tienePatronPersonalizado)
                {
                    _logger.LogWarning("🚫 Placa descartada (formato no reconocido): {Placa}", placa);
                    return;
                }
            }
            // ────────────────────────────────────────────────────────────

            if (string.IsNullOrEmpty(absTime) || absTime.Length < 17)
                absTime = DateTime.Now.ToString("yyyyMMddHHmmssfff");

            var imagenUrl = await GuardarImagenes(placa, lane, absTime, plateImage, fullImage);

            // Determinar entrada/salida según configuración en appsettings
            bool esEntrada;
            if (_anpr.CarrilesEntrada.Contains(lane))
                esEntrada = true;
            else if (_anpr.CarrilesSalida.Contains(lane))
                esEntrada = false;
            else
            {
                // Lane no configurado — impar=entrada, par=salida
                esEntrada = int.TryParse(lane, out int n) ? n % 2 != 0 : true;
                _logger.LogWarning("⚠️ Carril {Lane} no configurado en appsettings — asumiendo {Tipo}",
                    lane, esEntrada ? "ENTRADA" : "SALIDA");
            }

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
            var tipoVehiculo = DetectarTipoVehiculo(placa);
            _logger.LogInformation("🚗 Entrada — Placa:{Placa} Tipo:{Tipo} Lane:{Lane}",
                placa, tipoVehiculo, lane);

            // ══════════════════════════════════════════════════════════
            // TODA LA LÓGICA DE VALIDACIÓN USA BD LOCAL → respuesta <5ms
            // ParkSky solo recibe el registro en background
            // ══════════════════════════════════════════════════════════

            // 1. ¿Restringido? → Bloquear inmediatamente
            try
            {
                var restringido = await _db.VehiculosRestringidos
                    .FirstOrDefaultAsync(v => v.Placa == placa && v.Activo);
                if (restringido != null)
                {
                    await RegistrarAccesoLocal(placa, lane, "ENTRADA", false,
                        "RESTRINGIDO", "BLOQUEADO", imagenUrl);
                    _logger.LogWarning("🚫 Restringido bloqueado: {Placa}", placa);
                    _ = Task.Run(async () => {
                        try
                        {
                            await _parkSky.RegistrarIngresoAsync(placa, lane, carrilNombre,
                            false, null, imagenUrl, tipoVehiculo);
                        }
                        catch { }
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ BD local no disponible para restringidos");
            }

            // 2. ¿Convenio activo en BD local?
            bool tieneConvenioActivo = false;
            int? convenioId = null;
            try
            {
                var convenio = await _db.ConveniosVehiculos
                    .Include(cv => cv.ConvenioMensualidad)
                    .FirstOrDefaultAsync(cv =>
                        cv.Placa == placa &&
                        cv.Activo &&
                        cv.ConvenioMensualidad.FechaFin >= DateTime.Today);

                tieneConvenioActivo = convenio != null;
                convenioId = convenio?.ConvenioId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ BD local no disponible para convenios — consultando VPS");
                // Fallback a VPS si BD local no disponible
                try
                {
                    var conv = await _parkSky.ValidarConvenioAsync(placa);
                    tieneConvenioActivo = conv.TieneConvenio && conv.Activo;
                    convenioId = conv.ConvenioId;
                }
                catch { }
            }

            if (tieneConvenioActivo)
            {
                // 3a. Mensualidad activa → abrir inmediatamente
                await EjecutarApertura(lane, "ENTRADA");
                await RegistrarIngresoLocal(placa, lane, true, convenioId);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true,
                    "CONVENIO_ACTIVO", "CONVENIO", imagenUrl);
                _logger.LogInformation("✅ Convenio activo: {Placa} → abriendo", placa);
                _ = Task.Run(async () => {
                    try
                    {
                        var ing = await _parkSky.RegistrarIngresoAsync(placa, lane, carrilNombre,
                            true, convenioId, imagenUrl, tipoVehiculo);
                    }
                    catch { }
                });
                return;
            }

            // 3b. Casual
            if (_parqueadero.AbrirTodo)
            {
                // AbrirTodo=true → abrir inmediatamente
                await EjecutarApertura(lane, "ENTRADA");
                await RegistrarIngresoLocal(placa, lane, false, null);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true,
                    "CASUAL", "CASUAL", imagenUrl);
                _ = Task.Run(async () => {
                    try
                    {
                        var ingreso = await _parkSky.RegistrarIngresoAsync(placa, lane, carrilNombre,
                            false, null, imagenUrl, tipoVehiculo);
                        if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
                            await ImprimirDesdeParkskyOLocal(impresora, placa,
                                tipoVehiculo, carrilNombre, ingreso.RegistroId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ Registro ParkSky casual: {Placa}", placa);
                    }
                });
            }
            else
            {
                // AbrirTodo=false → NO abrir, solo registrar e imprimir
                // El sensor óptico al tomar el tiquete abre la barrera
                await RegistrarIngresoLocal(placa, lane, false, null);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", false,
                    "ESPERANDO_TIQUETE", "CASUAL", imagenUrl);
                _ = Task.Run(async () => {
                    try
                    {
                        var ingreso = await _parkSky.RegistrarIngresoAsync(placa, lane, carrilNombre,
                            false, null, imagenUrl, tipoVehiculo);
                        _logger.LogInformation("📥 Registro ParkSky {Placa}: Id={Id}",
                            placa, ingreso.RegistroId);
                        if (!string.IsNullOrEmpty(impresora))
                            await ImprimirDesdeParkskyOLocal(impresora, placa,
                                tipoVehiculo, carrilNombre, ingreso.RegistroId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ Registro e impresión: {Placa}", placa);
                    }
                });
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

            // Verificar pago con tiempo de gracia
            var limiteGracia = DateTime.Now.AddMinutes(-_parqueadero.TiempoGraciaMinutos);
            var registro = await _db.Registros
                .Include(r => r.Vehiculo)
                .OrderByDescending(r => r.FechaEntrada)
                .FirstOrDefaultAsync(r => r.Vehiculo.Placa == placa && !r.Activo
                    && r.ValorPagado > 0
                    && r.FechaSalida.HasValue
                    && r.FechaSalida.Value >= limiteGracia);

            if (registro != null)
            {
                _logger.LogInformation(
                    "✅ Salida autorizada (pagó hace {Min} min): {Placa}",
                    (int)(DateTime.Now - registro.FechaSalida!.Value).TotalMinutes, placa);
                await RegistrarAccesoLocal(placa, lane, "SALIDA", true,
                    "PAGADO", "CASUAL", imagenUrl);
                await EjecutarApertura(lane, "SALIDA");
            }
            else
            {
                await RegistrarAccesoLocal(placa, lane, "SALIDA", false,
                    "NO_PAGADO", "BLOQUEADO", imagenUrl);
                _logger.LogWarning("⛔ Salida bloqueada (sin pago o gracia vencida): {Placa}", placa);
            }
        }

        // -- Salida nube --
        private async Task SalidaParqueaderoNube(
            string placa, string lane, string imagenUrl, string carrilNombre)
        {
            _logger.LogInformation("🚪 Salida — Placa:{Placa} Lane:{Lane}", placa, lane);

            // ══════════════════════════════════════════════════════════
            // SALIDA RÁPIDA: GET simple → mínima latencia
            // ══════════════════════════════════════════════════════════
            try
            {
                var gracia = _parqueadero.TiempoGraciaMinutos;
                var url = $"api/hikvision/salida-rapida" +
                             $"?placa={Uri.EscapeDataString(placa)}" +
                             $"&gracia={gracia}" +
                             $"&carril={Uri.EscapeDataString(lane)}" +
                             $"&carrilNombre={Uri.EscapeDataString(carrilNombre)}" +
                             $"&imagenUrl={Uri.EscapeDataString(imagenUrl ?? "")}";
                var r = await _parkSky.GetRawAsync(url);

                using var doc = System.Text.Json.JsonDocument.Parse(r);
                var root = doc.RootElement;
                bool autorizado = root.GetProperty("ok").GetBoolean();
                string motivo = root.TryGetProperty("motivo", out var m) ? m.GetString() ?? "" : "";

                _logger.LogInformation("🚪 Salida {Placa}: Auth={A} Motivo={M}",
                    placa, autorizado, motivo);

                if (autorizado)
                {
                    await EjecutarApertura(lane, "SALIDA");
                    await RegistrarAccesoLocal(placa, lane, "SALIDA", true,
                        motivo, "CASUAL", imagenUrl);
                    // Notificar VPS en background para cerrar registro
                    _ = Task.Run(async () => {
                        try { await _parkSky.ValidarSalidaAsync(placa, lane, imagenUrl, carrilNombre); }
                        catch { }
                    });
                }
                else
                {
                    await RegistrarAccesoLocal(placa, lane, "SALIDA", false,
                        "NO_PAGADO", "BLOQUEADO", imagenUrl);
                    _logger.LogWarning("⛔ Salida bloqueada: {Placa}", placa);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Error salida {Placa} — fallback", placa);
                if (_parqueadero.AbrirSiSinInternet)
                {
                    await EjecutarApertura(lane, "SALIDA");
                    await RegistrarAccesoLocal(placa, lane, "SALIDA", true,
                        "SIN_INTERNET", "LIBRE", imagenUrl);
                }
                else
                {
                    await RegistrarAccesoLocal(placa, lane, "SALIDA", false,
                        "SIN_INTERNET", "BLOQUEADO", imagenUrl);
                }
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

        // Método público para llamadas desde PrintController (ingreso manual)
        public async Task RegistrarQrPublico(int registroId, string qrToken)
            => await RegistrarQrEnControladora(registroId, qrToken);

        // =============================================
        // QR — REGISTRAR EN CONTROLADORA AL INGRESO
        // Usa PUT /ISAPI/AccessControl/CardInfo/record
        // =============================================

        // =============================================
        // QR — SINCRONIZAR DESDE VPS (ingreso manual)
        // Si ParkSky no devolvió QrToken, consultarlo
        // =============================================
        private async Task SincronizarQrDesdeVps(string placa)
        {
            if (!_barrier.UsarQR) return;
            try
            {
                var json = await _parkSky.GetRawAsync(
                    $"api/hikvision/qr-activo?placa={Uri.EscapeDataString(placa)}");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean() &&
                    root.TryGetProperty("registroId", out var rid) &&
                    root.TryGetProperty("qrToken", out var qt) &&
                    !string.IsNullOrEmpty(qt.GetString()))
                {
                    await RegistrarQrEnControladora(rid.GetInt32(), qt.GetString()!);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ No se pudo sincronizar QR para {Placa}", placa);
            }
        }

        private async Task RegistrarQrEnControladora(int registroId, string qrToken)
        {
            if (!_barrier.UsarQR || string.IsNullOrEmpty(qrToken)) return;

            try
            {
                var baseUrl = _barrier.BaseUrl
                    .Replace("/AccessControl/RemoteControl/door/", "");
                var endpoint = $"{baseUrl}/ISAPI/AccessControl/CardInfo/record";
                var puerta = _barrier.PuertaLectorQR;

                var xml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<CardInfo>
  <employeeNo>REG{registroId}</employeeNo>
  <cardNo>{qrToken}</cardNo>
  <cardType>normalCard</cardType>
  <leaderCard>false</leaderCard>
  <RightPlan>
    <doorNo>{puerta}</doorNo>
    <planTemplateNo>1</planTemplateNo>
  </RightPlan>
</CardInfo>";

                var handler = new HttpClientHandler
                {
                    Credentials = new NetworkCredential(_barrier.Username, _barrier.Password),
                    PreAuthenticate = true
                };
                using var client = new HttpClient(handler);
                var r = await client.PutAsync(endpoint,
                    new StringContent(xml, Encoding.UTF8, "application/xml"));

                _logger.LogInformation("📱 QR registrado en controladora: REG{Id} → {Status}",
                    registroId, r.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ No se pudo registrar QR en controladora para REG{Id}", registroId);
            }
        }

        // =============================================
        // QR — ELIMINAR DE CONTROLADORA AL SALIR
        // Usa DELETE /ISAPI/AccessControl/CardInfo/record
        // =============================================
        private async Task EliminarQrDeControladora(int registroId)
        {
            if (!_barrier.UsarQR) return;

            try
            {
                var baseUrl = _barrier.BaseUrl
                    .Replace("/AccessControl/RemoteControl/door/", "");
                var endpoint = $"{baseUrl}/ISAPI/AccessControl/CardInfo/record";

                var xml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<CardInfoDelCond>
  <EmployeeNoList>
    <employeeNo>REG{registroId}</employeeNo>
  </EmployeeNoList>
</CardInfoDelCond>";

                var handler = new HttpClientHandler
                {
                    Credentials = new NetworkCredential(_barrier.Username, _barrier.Password),
                    PreAuthenticate = true
                };
                using var client = new HttpClient(handler);
                var req = new HttpRequestMessage(HttpMethod.Delete, endpoint)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/xml")
                };
                var r = await client.SendAsync(req);

                _logger.LogInformation("🗑️ QR eliminado de controladora: REG{Id} → {Status}",
                    registroId, r.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ No se pudo eliminar QR de controladora para REG{Id}", registroId);
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

            // Leer ambas imágenes en memoria PRIMERO (los streams solo se leen una vez)
            byte[]? plateBytes = null;
            byte[]? fullBytes = null;

            if (plateImage != null)
            {
                using var ms = new MemoryStream();
                await plateImage.CopyToAsync(ms);
                plateBytes = ms.ToArray();
            }
            if (fullImage != null)
            {
                using var ms = new MemoryStream();
                await fullImage.CopyToAsync(ms);
                fullBytes = ms.ToArray();
            }

            _logger.LogInformation("📷 Bytes — plate:{P} full:{F}",
                plateBytes?.Length ?? 0, fullBytes?.Length ?? 0);

            // Guardar localmente como respaldo
            try
            {
                string carpeta = Path.Combine(_anpr.TargetFolder, $"Camara{lane}", fecha);
                string carpetaX = Path.Combine(_anpr.TargetFolder, $"Camara{lane}X", fecha);
                Directory.CreateDirectory(carpeta);
                Directory.CreateDirectory(carpetaX);

                if (plateBytes != null)
                    await File.WriteAllBytesAsync(Path.Combine(carpeta, nombre), plateBytes);
                if (fullBytes != null)
                    await File.WriteAllBytesAsync(Path.Combine(carpetaX, nombre), fullBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo guardar imagen local para {Placa}", placa);
            }

            // Subir al VPS — usar foto completa, si no hay usar recorte placa
            var imgBytes = fullBytes ?? plateBytes;
            var tipo = fullBytes != null ? "Completa" : "Placa";

            if (_parkSky != null && imgBytes != null && imgBytes.Length > 0)
            {
                try
                {
                    var base64 = Convert.ToBase64String(imgBytes);
                    var urlVps = await _parkSky.EnviarImagenAsync(placa, lane, tipo, base64);
                    if (!string.IsNullOrEmpty(urlVps))
                    {
                        _logger.LogInformation("🖼️ Imagen VPS OK: {Url}", urlVps);
                        return urlVps;
                    }
                    _logger.LogWarning("⚠️ VPS no devolvió URL para {Placa}", placa);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo subir imagen al VPS para {Placa}", placa);
                }
            }
            else
            {
                _logger.LogWarning("⚠️ Sin bytes de imagen para {Placa} — no se sube al VPS", placa);
            }

            return "";
        }

        private async Task RegistrarAccesoLocal(
            string placa, string carril, string tipo,
            bool autorizado, string motivo, string tipoAcceso, string? imagenUrl)
        {
            try
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
            catch (Exception ex)
            {
                // BD local no disponible — solo loguear, no interrumpir el flujo
                _logger.LogWarning("⚠️ BD local no disponible (AccesoVehicular no guardado): {Msg}", ex.Message);
            }
        }

        private async Task RegistrarIngresoLocal(
            string placa, string lane, bool esMensualidad, int? convenioId)
        {
            try
            {
                // 1. Buscar o crear vehículo
                var vehiculo = await _db.Vehiculos
                    .FirstOrDefaultAsync(v => v.Placa == placa);

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

                // 2. Buscar tarifa por tipo de vehículo
                var tipo = DetectarTipoVehiculo(placa);
                var tarifa = await _db.Tarifas
                    .FirstOrDefaultAsync(t =>
                        t.Activa &&
                        t.TipoVehiculo != null &&
                        t.TipoVehiculo.Contains(tipo));

                // Si no encuentra tarifa específica, usar la primera activa
                tarifa ??= await _db.Tarifas.FirstOrDefaultAsync(t => t.Activa);

                if (tarifa == null)
                {
                    _logger.LogWarning("⚠️ Sin tarifa disponible para {Placa} — no se crea registro local", placa);
                    return;
                }

                // 3. Crear el registro de ingreso
                var registro = new RegistroLocal
                {
                    VehiculoId = vehiculo.Id,
                    TarifaId = tarifa.Id,
                    FechaEntrada = DateTime.Now,
                    Activo = true,
                    EsMensualidad = esMensualidad,
                    ConvenioMensualidadId = convenioId
                };

                _db.Registros.Add(registro);
                await _db.SaveChangesAsync();

                _logger.LogInformation("✅ Registro local creado: {Placa} Id={Id}", placa, registro.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ BD local no disponible (RegistroLocal no guardado): {Msg}", ex.Message);
            }
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
                    var ticket =
                        await _parkSky.ObtenerTicketAsync(
                            registroId.Value);

                    if (ticket != null && ticket.Ok)
                    {
                        _logger.LogInformation(
                            "🖨️ Imprimiendo ticket directo RegistroId={Id}",
                            registroId.Value);

                        _print.ImprimirDesdeTicket(
                            impresora,
                            ticket);

                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Error obteniendo ticket desde ParkSky");
                }
            }

            // Fallback — imprimir con datos locales si ParkSky no disponible
            _print.ImprimirTiqueteLocal(
                impresora, placa, tipo, DateTime.Now, carrilNombre, false);
        }

        private async Task CerrarRegistroLocal(string placa)
        {
            _logger.LogInformation("Salida local registrada: {Placa}", placa);
        }

        private string DetectarTipoVehiculo(string placa)
        {
            // 1. Consultar patrones personalizados en BD (máxima prioridad)
            try
            {
                var patrones = _db?.PatronPlacas?
                    .Where(p => p.Activo)
                    .Include(p => p.Tarifa)
                    .ToList();

                if (patrones != null)
                {
                    foreach (var p in patrones)
                    {
                        if (CoincidePatronLocal(placa, p.Patron))
                            return p.Tarifa?.TipoVehiculo ?? p.Nombre ?? "Carro";
                    }
                }
            }
            catch { /* BD no disponible */ }

            // 2. Reglas colombianas estándar
            return DetectarTipoPlacaColombia(placa);
        }

        /// Detecta tipo de vehículo por formato de placa colombiana.
        /// Retorna "Carro", "Moto" o null si el formato no es reconocido.
        private static string? DetectarTipoPlacaColombia(string placa)
        {
            if (string.IsNullOrWhiteSpace(placa)) return null;
            placa = placa.ToUpper().Trim();
            int n = placa.Length;
            bool L(int i) => i < n && char.IsLetter(placa[i]);
            bool D(int i) => i < n && char.IsDigit(placa[i]);

            // AAA### (6) → Carro
            if (n == 6 && L(0) && L(1) && L(2) && D(3) && D(4) && D(5))
                return "Carro";

            // AAA##A (6) → Moto nueva
            if (n == 6 && L(0) && L(1) && L(2) && D(3) && D(4) && L(5))
                return "Moto";

            // AAA## (5) → Moto antigua (sin letra final)
            if (n == 5 && L(0) && L(1) && L(2) && D(3) && D(4))
                return "Moto";

            return null; // formato no reconocido → descartar en cámaras
        }
        

        /// Misma lógica que ControlController.CoincidePatron
        private static bool CoincidePatronLocal(string placa, string patron)
        {
            if (placa.Length != patron.Length) return false;
            for (int i = 0; i < patron.Length; i++)
            {
                if (patron[i] == 'A' && !char.IsLetter(placa[i])) return false;
                if (patron[i] == '#' && !char.IsDigit(placa[i])) return false;
            }
            return true;
        }
    }
}