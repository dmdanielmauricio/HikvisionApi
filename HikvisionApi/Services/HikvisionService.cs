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
        private readonly LocalCacheService _cache;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<HikvisionService> _logger;
        private readonly string _modo;
        private readonly string _name;

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
            LocalCacheService cache,
            IServiceScopeFactory scopeFactory,
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
            _cache = cache;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _modo = config["ModoOperacion"] ?? "Porteria";
            _name = config["Name"] ?? "HikvisionService";
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
            // ─────────────────────────────────────────────────────────────

            if (string.IsNullOrEmpty(absTime) || absTime.Length < 17)
                absTime = DateTime.Now.ToString("yyyyMMddHHmmssfff");

            // GuardarImagenes guarda local de forma SÍNCRONA (rápido) y arranca
            // la subida al VPS SIN esperarla — devuelve la Task ya en marcha.
            // Quien necesite la URL la awaitea en su momento (ver EntradaParqueaderoNube).
            var imagenUrlTask = await GuardarImagenes(placa, lane, absTime, plateImage, fullImage);

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
                    await ProcesarPorteria(placa, lane, imagenUrlTask, carrilNombre, esEntrada);
                    break;
                case "Parqueadero":
                    if (esEntrada)
                        await ProcesarParqueaderoEntrada(placa, lane, imagenUrlTask, carrilNombre);
                    else
                        await ProcesarParqueaderoSalida(placa, lane, imagenUrlTask, carrilNombre);
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
            string placa, string lane, Task<string> imagenUrlTask,
            string carrilNombre, bool esEntrada)
        {
            var imagenUrl = await imagenUrlTask;
            string tipo = esEntrada ? "ENTRADA" : "SALIDA";
            bool autorizado;

            if (_porteria.FuenteDatos == "Local")
            {
                var vehiculo = await _db.Vehiculos
                    .FirstOrDefaultAsync(v => v.Placa == placa);
                autorizado = vehiculo != null && vehiculo.Activo;

                _logger.LogInformation("Portería LOCAL: {Placa} → {Auth}", placa, autorizado);
            }
            else
            {
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
            string placa, string lane, Task<string> imagenUrlTask, string carrilNombre)
        {
            string impresora = PrintService.ObtenerImpresora(lane, _config);

            if (_parqueadero.FuenteDatos == "Local")
            {
                await EntradaParqueaderoLocal(placa, lane, imagenUrlTask, carrilNombre, impresora);
            }
            else
            {
                await EntradaParqueaderoNube(placa, lane, imagenUrlTask, carrilNombre, impresora);
            }
        }

        // -- Entrada local --
        private async Task EntradaParqueaderoLocal(
            string placa, string lane, Task<string> imagenUrlTask,
            string carrilNombre, string impresora)
        {
            var imagenUrl = await imagenUrlTask;

            if (_parqueadero.AbrirTodo)
            {
                await RegistrarIngresoLocal(placa, lane, false, null);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true, "ABRE_TODO", "LIBRE", imagenUrl);
                await EjecutarApertura(lane, "ENTRADA");
                return;
            }

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
                await RegistrarIngresoLocal(placa, lane, false, null);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true, "CASUAL", "CASUAL", imagenUrl);

                if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
                {
                    var configTiquete = await ObtenerConfiguracionLocal();
                    _print.ImprimirTiqueteLocal(impresora, placa, "Vehículo",
                        DateTime.Now, carrilNombre, false, configTiquete);
                }
                else if (_parqueadero.EntregarTiquete)
                {
                    _logger.LogWarning(
                        "⚠️ No se imprimió tiquete para {Placa} — impresora vacía o no configurada " +
                        "para carril {Lane} ({Carril}). Revisar clave Impresoras en appsettings.json.",
                        placa, lane, carrilNombre);
                }

                await EjecutarApertura(lane, "ENTRADA");
            }
        }

        // -- Entrada nube --
        private async Task EntradaParqueaderoNube(
            string placa, string lane, Task<string> imagenUrlTask,
            string carrilNombre, string impresora)
        {
            var tipoVehiculo = DetectarTipoVehiculo(placa);
            var horaEntrada = DateTime.Now;
            _logger.LogInformation("🚗 Entrada — Placa:{Placa} Tipo:{Tipo} Lane:{Lane}",
                placa, tipoVehiculo, lane);

            // Generar QrToken local — mismo algoritmo que VPS ControlController.GenerarQrToken.
            const string qrChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var qrBytes = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(qrBytes);
            var qrTokenLocal = $"{placa}-{horaEntrada:yyyyMMdd}-{horaEntrada:HHmmss}-" +
                new string(qrBytes.Select(b => qrChars[b % qrChars.Length]).ToArray());

            // 1. Restringido → caché, 0ms
            if (_cache.EsRestringido(placa))
            {
                _logger.LogWarning("🚫 Restringido bloqueado (caché): {Placa}", placa);
                EncolarEvento(placa, lane, carrilNombre, "ENTRADA", false,
                    "RESTRINGIDO", tipoVehiculo, false, null, imagenUrlTask);
                return;
            }

            // 2. Convenio activo → caché, 0ms
            bool tieneConvenio = _cache.TieneConvenioActivo(placa, out int? convenioId);
            var motivo = tieneConvenio ? "CONVENIO_ACTIVO" : "CASUAL";

            if (!_parqueadero.AbrirTodo && !tieneConvenio)
            {
                // AbrirTodo=false y sin convenio → sensor óptico de tiquete
                EncolarEvento(placa, lane, carrilNombre, "ENTRADA", false,
                    "ESPERANDO_TIQUETE", tipoVehiculo, false, null, imagenUrlTask, qrTokenLocal);
                if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
                {
                    var configTiquete = await ObtenerConfiguracionLocal();
                    _print.ImprimirTiqueteLocal(impresora, placa, tipoVehiculo,
                        horaEntrada, carrilNombre, false, configTiquete, qrToken: qrTokenLocal);
                }
                else if (_parqueadero.EntregarTiquete)
                {
                    _logger.LogWarning(
                        "⚠️ No se imprimió tiquete para {Placa} — impresora vacía o no configurada " +
                        "para carril {Lane} ({Carril}). Revisar clave Impresoras en appsettings.json.",
                        placa, lane, carrilNombre);
                }
                return;
            }

            // 3. ABRIR BARRERA — único paso de red en el camino síncrono (~50ms LAN)
            await EjecutarApertura(lane, "ENTRADA");
            _logger.LogInformation("✅ Barrera abierta: {Placa} ({Motivo})", placa, motivo);

            // 4. Imprimir tiquete INMEDIATAMENTE con datos locales.
            if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
            {
                var configTiquete = await ObtenerConfiguracionLocal();
                _print.ImprimirTiqueteLocal(impresora, placa, tipoVehiculo,
                    horaEntrada, carrilNombre, tieneConvenio, configTiquete, qrToken: qrTokenLocal);
            }
            else if (_parqueadero.EntregarTiquete)
            {
                _logger.LogWarning(
                    "⚠️ No se imprimió tiquete para {Placa} — impresora vacía o no configurada " +
                    "para carril {Lane} ({Carril}). Revisar clave Impresoras en appsettings.json.",
                    placa, lane, carrilNombre);
            }

            // 5. Encolar evento (background, scope propio, nunca bloquea)
            EncolarEvento(placa, lane, carrilNombre, "ENTRADA", true,
                motivo, tipoVehiculo, tieneConvenio, convenioId, imagenUrlTask, qrTokenLocal);
        }

        // ════════════════════════════════════════════════════════════════
        // CONFIGURACIÓN LOCAL — para imprimir con los datos reales del
        // parqueadero (nombre, NIT, dirección, teléfono, mensajes), leídos
        // de la tabla Configuraciones local (copia exacta de la del VPS).
        // Falla silenciosa: si la BD local no responde, ImprimirTiqueteLocal
        // ya sabe caer a los valores por defecto de siempre con config=null.
        // ════════════════════════════════════════════════════════════════
        private async Task<ConfiguracionLocal?> ObtenerConfiguracionLocal()
        {
            try
            {
                return await _db.Configuraciones.FirstOrDefaultAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ No se pudo leer Configuraciones local — usando valores por defecto");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // ENCOLAR EVENTO — inserta en EventosLocales con scope propio.
        // ════════════════════════════════════════════════════════════════
        private void EncolarEvento(
            string placa, string carril, string? carrilNombre,
            string tipoMovimiento, bool autorizado, string motivo,
            string? tipoVehiculo, bool esMensualidad, int? convenioId,
            Task<string>? imagenUrlTask = null,
            string? qrToken = null)
        {
            _ = Task.Run(async () =>
            {
                string imagenUrl = "";
                if (imagenUrlTask != null)
                {
                    try { imagenUrl = await imagenUrlTask; } catch { }
                }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    db.EventosLocales.Add(new EventoLocal
                    {
                        Placa = placa,
                        Carril = carril,
                        CarrilNombre = carrilNombre,
                        TipoMovimiento = tipoMovimiento,
                        Autorizado = autorizado,
                        Motivo = motivo,
                        TipoVehiculo = tipoVehiculo,
                        EsMensualidad = esMensualidad,
                        ConvenioId = convenioId,
                        ImagenUrl = imagenUrl,
                        QrToken = qrToken,
                        FechaHora = DateTime.Now,
                        Sincronizado = false,
                        IntentosSincronizacion = 0
                    });
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[EncolarEvento] Error: {Placa} {Tipo}", placa, tipoMovimiento);
                }
            });
        }


        // =============================================
        // PARQUEADERO SALIDA
        // =============================================
        private async Task ProcesarParqueaderoSalida(
            string placa, string lane, Task<string> imagenUrlTask, string carrilNombre)
        {
            if (_parqueadero.FuenteDatos == "Local")
                await SalidaParqueaderoLocal(placa, lane, imagenUrlTask);
            else
                await SalidaParqueaderoNube(placa, lane, imagenUrlTask, carrilNombre);
        }

        // -- Salida local --
        private async Task SalidaParqueaderoLocal(
            string placa, string lane, Task<string> imagenUrlTask)
        {
            var imagenUrl = await imagenUrlTask;

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
            string placa, string lane, Task<string> imagenUrlTask, string carrilNombre)
        {
            _logger.LogInformation("🚪 Salida — Placa:{Placa} Lane:{Lane}", placa, lane);

            // 1. Convenio activo → caché, 0ms
            if (_cache.TieneConvenioActivo(placa, out _))
            {
                await EjecutarApertura(lane, "SALIDA");
                EncolarEvento(placa, lane, carrilNombre, "SALIDA", true,
                    "CONVENIO_ACTIVO", null, true, null, imagenUrlTask);
                _logger.LogInformation("✅ Salida convenio: {Placa}", placa);
                return;
            }

            // 2. Pago confirmado → BD local, single AnyAsync (~20ms)
            try
            {
                var pagadoLocal = await _db.PagosConfirmados
                    .AnyAsync(p => p.Placa == placa && p.FechaExpira >= DateTime.Now);

                if (pagadoLocal)
                {
                    await EjecutarApertura(lane, "SALIDA");
                    EncolarEvento(placa, lane, carrilNombre, "SALIDA", true,
                        "PAGADO", null, false, null, imagenUrlTask);
                    _logger.LogInformation("✅ Salida pagada (local): {Placa}", placa);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ PagosConfirmados no disponible — fallback VPS: {Placa}", placa);
            }

            // 3. Fallback → VPS (hasta que webhook confirmar-pago esté activo)
            string imagenUrl = "";
            try { imagenUrl = await imagenUrlTask; } catch { }

            try
            {
                var gracia = _parqueadero.TiempoGraciaMinutos;
                var url = $"api/hikvision/salida-rapida" +
                             $"?placa={Uri.EscapeDataString(placa)}" +
                             $"&gracia={gracia}" +
                             $"&carril={Uri.EscapeDataString(lane)}" +
                             $"&carrilNombre={Uri.EscapeDataString(carrilNombre)}" +
                             $"&imagenUrl={Uri.EscapeDataString(imagenUrl)}";
                var r = await _parkSky.GetRawAsync(url);

                using var doc = System.Text.Json.JsonDocument.Parse(r);
                var root = doc.RootElement;
                bool autorizado = root.GetProperty("ok").GetBoolean();
                string motivo = root.TryGetProperty("motivo", out var m) ? m.GetString() ?? "" : "";

                _logger.LogInformation("🚪 Salida VPS {Placa}: Auth={A} Motivo={M}",
                    placa, autorizado, motivo);

                if (autorizado)
                {
                    await EjecutarApertura(lane, "SALIDA");
                    EncolarEvento(placa, lane, carrilNombre, "SALIDA", true,
                        motivo, null, false, null, Task.FromResult(imagenUrl));
                }
                else
                {
                    EncolarEvento(placa, lane, carrilNombre, "SALIDA", false,
                        "NO_PAGADO", null, false, null, Task.FromResult(imagenUrl));
                    _logger.LogWarning("⛔ Salida bloqueada: {Placa}", placa);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Error salida {Placa} — fallback AbrirSiSinInternet", placa);
                if (_parqueadero.AbrirSiSinInternet)
                {
                    await EjecutarApertura(lane, "SALIDA");
                    EncolarEvento(placa, lane, carrilNombre, "SALIDA", true,
                        "SIN_INTERNET", null, false, null, Task.FromResult(imagenUrl));
                }
                else
                {
                    EncolarEvento(placa, lane, carrilNombre, "SALIDA", false,
                        "SIN_INTERNET", null, false, null, Task.FromResult(imagenUrl));
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

        private static HttpClient? _barrierClient;
        private static readonly object _barrierClientLock = new();

        private HttpClient ObtenerClienteBarrera()
        {
            if (_barrierClient != null) return _barrierClient;
            lock (_barrierClientLock)
            {
                if (_barrierClient == null)
                {
                    var handler = new HttpClientHandler
                    {
                        Credentials = new NetworkCredential(_barrier.Username, _barrier.Password),
                        PreAuthenticate = true,
                        UseCookies = true,
                        CookieContainer = new CookieContainer()
                    };
                    _barrierClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                }
            }
            return _barrierClient;
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
                var client = ObtenerClienteBarrera();

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

        public async Task RegistrarQrPublico(int registroId, string qrToken)
            => await RegistrarQrEnControladora(registroId, qrToken);

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

        private string ObtenerRaizControladora()
        {
            var uri = new Uri(_barrier.BaseUrl);
            return $"{uri.Scheme}://{uri.Host}";
        }

        private async Task<HttpClient> CrearClienteHikvision()
        {
            var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(_barrier.Username, _barrier.Password),
                PreAuthenticate = true,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            try
            {
                await client.GetAsync($"{ObtenerRaizControladora()}/System/status");
            }
            catch { }

            return client;
        }

        private async Task RegistrarQrEnControladora(int registroId, string qrToken)
        {
            if (!_barrier.UsarQR || string.IsNullOrEmpty(qrToken)) return;
            if (_barrier.PuertaLectorQR == null || !_barrier.PuertaLectorQR.Any()) return;

            try
            {
                var baseUrl = ObtenerRaizControladora();
                using var client = await CrearClienteHikvision();
                var empNo = $"REG{registroId}";

                var rightPlan = _barrier.PuertaLectorQR
                    .Select(p => new { doorNo = p, planTemplateNo = "1" })
                    .ToArray();

                var userJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    UserInfo = new
                    {
                        employeeNo = empNo,
                        name = $"QR-REG{registroId}",
                        userType = "normal",
                        Valid = new
                        {
                            enable = true,
                            beginTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                            endTime = DateTime.Now.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ss")
                        },
                        doorRight = "1",
                        RightPlan = rightPlan
                    }
                });

                var r1 = await client.PostAsync(
                    $"{baseUrl}/ISAPI/AccessControl/UserInfo/Record?format=json",
                    new StringContent(userJson, Encoding.UTF8, "application/json"));

                var body1 = await r1.Content.ReadAsStringAsync();
                _logger.LogInformation("📱 QR usuario creado: REG{Id} puertas=[{Puertas}] → {Status}",
                    registroId,
                    string.Join(",", _barrier.PuertaLectorQR),
                    r1.StatusCode);

                if (!r1.IsSuccessStatusCode)
                {
                    _logger.LogWarning("⚠️ Error creando usuario QR REG{Id}: {Body}",
                        registroId, body1);
                    return;
                }

                var cardJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    CardInfo = new
                    {
                        employeeNo = empNo,
                        cardNo = qrToken,
                        cardType = "normalCard"
                    }
                });

                var r2 = await client.PostAsync(
                    $"{baseUrl}/ISAPI/AccessControl/CardInfo/Record?format=json",
                    new StringContent(cardJson, Encoding.UTF8, "application/json"));

                var body2 = await r2.Content.ReadAsStringAsync();
                _logger.LogInformation("📱 QR tarjeta asociada: REG{Id} → {Status}",
                    registroId, r2.StatusCode);

                if (!r2.IsSuccessStatusCode)
                    _logger.LogWarning("⚠️ Error asociando tarjeta QR REG{Id}: {Body}",
                        registroId, body2);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "⚠️ No se pudo registrar QR en controladora para REG{Id}", registroId);
            }
        }

        private async Task EliminarQrDeControladora(int registroId)
        {
            if (!_barrier.UsarQR) return;

            try
            {
                var baseUrl = ObtenerRaizControladora();

                using var client = await CrearClienteHikvision();

                var empNo = $"REG{registroId}";

                var cardDelJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    CardInfoDelCond = new
                    {
                        EmployeeNoList = new[]
                        {
                            new { employeeNo = empNo }
                        }
                    }
                });

                var req1 = new HttpRequestMessage(HttpMethod.Delete,
                    $"{baseUrl}/ISAPI/AccessControl/CardInfo/Record?format=json")
                {
                    Content = new StringContent(cardDelJson, Encoding.UTF8, "application/json")
                };
                var r1 = await client.SendAsync(req1);
                var body1 = await r1.Content.ReadAsStringAsync();

                _logger.LogInformation("🗑️ QR tarjeta eliminada: REG{Id} → {Status} {Body}",
                    registroId, r1.StatusCode, body1);

                var userDelJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    UserInfoDetail = new
                    {
                        mode = "byEmployeeNo",
                        EmployeeNoList = new[]
                        {
                            new { employeeNo = empNo }
                        }
                    }
                });

                var req2 = new HttpRequestMessage(HttpMethod.Delete,
                    $"{baseUrl}/ISAPI/AccessControl/UserInfo/Record?format=json")
                {
                    Content = new StringContent(userDelJson, Encoding.UTF8, "application/json")
                };
                var r2 = await client.SendAsync(req2);
                var body2 = await r2.Content.ReadAsStringAsync();

                _logger.LogInformation("🗑️ QR usuario eliminado: REG{Id} → {Status} {Body}",
                    registroId, r2.StatusCode, body2);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "⚠️ No se pudo eliminar QR de controladora para REG{Id}", registroId);
            }
        }


        // =============================================
        // HELPERS
        // =============================================
        private async Task<Task<string>> GuardarImagenes(
            string placa, string lane, string absTime,
            IFormFile? plateImage, IFormFile? fullImage)
        {
            string fecha = absTime.Substring(0, 8);
            string nombre = $"{absTime}_{placa}_{lane}.jpg";

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

            var imgBytes = fullBytes ?? plateBytes;
            var tipo = fullBytes != null ? "Completa" : "Placa";

            if (_parkSky != null && imgBytes != null && imgBytes.Length > 0)
                return SubirImagenVpsAsync(placa, lane, tipo, imgBytes);

            _logger.LogWarning("⚠️ Sin bytes de imagen para {Placa} — no se sube al VPS", placa);
            return Task.FromResult("");
        }

        private async Task<string> SubirImagenVpsAsync(
            string placa, string lane, string tipo, byte[] imgBytes)
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
                _logger.LogWarning("⚠️ BD local no disponible (AccesoVehicular no guardado): {Msg}", ex.Message);
            }
        }

        private async Task RegistrarIngresoLocal(
            string placa, string lane, bool esMensualidad, int? convenioId)
        {
            try
            {
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

                var tipo = DetectarTipoVehiculo(placa);
                var tarifa = await _db.Tarifas
                    .FirstOrDefaultAsync(t =>
                        t.Activa &&
                        t.TipoVehiculo != null &&
                        t.TipoVehiculo.Contains(tipo));

                tarifa ??= await _db.Tarifas.FirstOrDefaultAsync(t => t.Activa);

                if (tarifa == null)
                {
                    _logger.LogWarning("⚠️ Sin tarifa disponible para {Placa} — no se crea registro local", placa);
                    return;
                }

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
        // IMPRIMIR — 100% local, sin llamar al VPS.
        // CAMBIO: antes intentaba pedir el tiquete a ParkSky primero y
        // solo caía a local si fallaba. Ahora imprime directo con datos
        // locales siempre, para que el proceso sea rápido y no dependa de
        // la conexión al VPS. registroId queda sin usar — se mantiene en
        // la firma para no romper llamadas existentes desde algún punto
        // que no tengamos visibilidad.
        // =============================================
        private async Task ImprimirDesdeParkskyOLocal(
            string impresora, string placa, string tipo,
            string carrilNombre, int? registroId)
        {
            var configTiquete = await ObtenerConfiguracionLocal();
            _print.ImprimirTiqueteLocal(
                impresora, placa, tipo, DateTime.Now, carrilNombre, false, configTiquete);
        }

        private async Task CerrarRegistroLocal(string placa)
        {
            _logger.LogInformation("Salida local registrada: {Placa}", placa);
        }

        private string DetectarTipoVehiculo(string placa)
        {
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

            return DetectarTipoPlacaColombia(placa);
        }

        private static string? DetectarTipoPlacaColombia(string placa)
        {
            if (string.IsNullOrWhiteSpace(placa)) return null;
            placa = placa.ToUpper().Trim();
            int n = placa.Length;
            bool L(int i) => i < n && char.IsLetter(placa[i]);
            bool D(int i) => i < n && char.IsDigit(placa[i]);

            if (n == 6 && L(0) && L(1) && L(2) && D(3) && D(4) && D(5))
                return "Carro";

            if (n == 6 && L(0) && L(1) && L(2) && D(3) && D(4) && L(5))
                return "Moto";

            if (n == 5 && L(0) && L(1) && L(2) && D(3) && D(4))
                return "Moto";

            return null;
        }

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