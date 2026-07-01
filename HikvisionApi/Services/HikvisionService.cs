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
        // PROCESAR ACCESO \u2014 entrada principal
        // =============================================
        public async Task ProcesarAcceso(
            string placa, string lane, string absTime,
            IFormFile? plateImage, IFormFile? fullImage)
        {
            _logger.LogInformation("\ud83d\udcf8 {Placa} carril {Lane} modo {Modo}", placa, lane, _modo);

            // \u2500\u2500 FILTRO DE PLACA \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
            // 1. Descartar placas no reconocidas por la c\u00e1mara
            if (_anpr.PlacasDescartadas.Any(d =>
                    placa.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                    placa.Contains(d, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("\ud83d\udeab Placa descartada (no reconocida): {Placa}", placa);
                return;
            }
            // 2. Descartar lecturas parciales o muy cortas
            if (placa.Length < _anpr.LongitudMinimaPlaca)
            {
                _logger.LogWarning("\ud83d\udeab Placa descartada (muy corta {Len} chars): {Placa}",
                    placa.Length, placa);
                return;
            }
            // 3. Validar que la placa tenga solo caracteres v\u00e1lidos (letras y n\u00fameros)
            if (!placa.All(c => char.IsLetterOrDigit(c)))
            {
                _logger.LogWarning("\ud83d\udeab Placa descartada (caracteres inv\u00e1lidos): {Placa}", placa);
                return;
            }

            // 4. Validar formato colombiano \u2014 descartar si no coincide con ning\u00fan
            //    patr\u00f3n personalizado ni con los formatos est\u00e1ndar
            var tipoDetectado = DetectarTipoPlacaColombia(placa);
            if (tipoDetectado == null)
            {
                // Verificar si coincide con alg\u00fan patr\u00f3n personalizado en BD
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
                    _logger.LogWarning("\ud83d\udeab Placa descartada (formato no reconocido): {Placa}", placa);
                    return;
                }
            }
            // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

            if (string.IsNullOrEmpty(absTime) || absTime.Length < 17)
                absTime = DateTime.Now.ToString("yyyyMMddHHmmssfff");

            // GuardarImagenes guarda local de forma S\u00cdNCRONA (r\u00e1pido) y arranca
            // la subida al VPS SIN esperarla \u2014 devuelve la Task ya en marcha.
            // Quien necesite la URL la awaitea en su momento (ver EntradaParqueaderoNube).
            var imagenUrlTask = await GuardarImagenes(placa, lane, absTime, plateImage, fullImage);

            // Determinar entrada/salida seg\u00fan configuraci\u00f3n en appsettings
            bool esEntrada;
            if (_anpr.CarrilesEntrada.Contains(lane))
                esEntrada = true;
            else if (_anpr.CarrilesSalida.Contains(lane))
                esEntrada = false;
            else
            {
                // Lane no configurado \u2014 impar=entrada, par=salida
                esEntrada = int.TryParse(lane, out int n) ? n % 2 != 0 : true;
                _logger.LogWarning("\u26a0\ufe0f Carril {Lane} no configurado en appsettings \u2014 asumiendo {Tipo}",
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
        // PORTER\u00cdA \u2014 local o nube
        // =============================================
        private async Task ProcesarPorteria(
            string placa, string lane, Task<string> imagenUrlTask,
            string carrilNombre, bool esEntrada)
        {
            // Sin cambios de comportamiento: Porter\u00eda no tiene el problema de
            // throughput reportado, as\u00ed que sigue esperando la imagen aqu\u00ed
            // mismo, igual que antes.
            var imagenUrl = await imagenUrlTask;
            string tipo = esEntrada ? "ENTRADA" : "SALIDA";
            bool autorizado;

            if (_porteria.FuenteDatos == "Local")
            {
                // Validar contra BD local
                var vehiculo = await _db.Vehiculos
                    .FirstOrDefaultAsync(v => v.Placa == placa);
                autorizado = vehiculo != null && vehiculo.Activo;

                _logger.LogInformation("Porter\u00eda LOCAL: {Placa} \u2192 {Auth}", placa, autorizado);
            }
            else
            {
                // Validar contra ParkSky nube
                try
                {
                    var conv = await _parkSky.ValidarConvenioAsync(placa);
                    autorizado = conv.TieneConvenio && conv.Activo;
                    _logger.LogInformation("Porter\u00eda NUBE: {Placa} \u2192 {Auth}", placa, autorizado);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sin internet \u2014 porter\u00eda nube fallback");
                    autorizado = _porteria.AbrirSiSinInternet;
                }
            }

            await RegistrarAccesoLocal(placa, lane, tipo, autorizado,
                autorizado ? "OK" : "NO_AUTORIZADO", "PORTERIA", imagenUrl);

            if (autorizado)
                await EjecutarApertura(lane, tipo);
            else
                _logger.LogWarning("\u26d4 Porter\u00eda: {Placa} NO autorizado", placa);
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
            // Sin cambios de comportamiento: FuenteDatos=Local no es el modo
            // activo en producci\u00f3n y no tiene el problema reportado.
            var imagenUrl = await imagenUrlTask;

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
                // Casual \u2014 imprimir tiquete
                await RegistrarIngresoLocal(placa, lane, false, null);
                await RegistrarAccesoLocal(placa, lane, "ENTRADA", true, "CASUAL", "CASUAL", imagenUrl);

                if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
                    _print.ImprimirTiqueteLocal(impresora, placa, "Veh\u00edculo",
                        DateTime.Now, carrilNombre, false);

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
            _logger.LogInformation("\ud83d\ude97 Entrada \u2014 Placa:{Placa} Tipo:{Tipo} Lane:{Lane}",
                placa, tipoVehiculo, lane);

            // Generar QrToken local — mismo algoritmo que VPS ControlController.GenerarQrToken.
            // Garantiza que el QR del tiquete coincida con el token que al cobrar
            // se carga en la K2600 vía /api/print/registrar-qr.
            // Se incluye en EventoLocal para que SyncBackgroundService lo envíe al VPS
            // y el RegistroParqueo quede con el mismo token.
            const string qrChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var qrBytes = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(qrBytes);
            var qrTokenLocal = $"{placa}-{horaEntrada:yyyyMMdd}-{horaEntrada:HHmmss}-" +
                new string(qrBytes.Select(b => qrChars[b % qrChars.Length]).ToArray());

            // ════════════════════════════════════════════════════════════════
            // NUEVA RUTA CRÍTICA — caché en memoria + HTTP LAN a controladora
            // Sin SQL, sin VPS en el camino síncrono.
            //
            // LocalCacheService (Singleton) mantiene en memoria:
            //   - Placas restringidas (HashSet, refresh 60s)
            //   - Convenios activos por placa (Dictionary, refresh 60s)
            // Las dos consultas que antes eran SQL (~100ms cada una) son
            // ahora O(1) sin red ni I/O — 0ms.
            //
            // EncolarEvento escribe en EventosLocales usando scope propio
            // (IServiceScopeFactory) → el DbContext del request no se
            // captura en background, eliminando el riesgo de ObjectDisposed.
            //
            // SyncBackgroundService sincroniza EventosLocales al VPS c/5s.
            // El tiquete se imprime cuando el VPS confirma el RegistroId.
            // ════════════════════════════════════════════════════════════════

            // 1. Restringido → caché, 0ms
            if (_cache.EsRestringido(placa))
            {
                _logger.LogWarning("\ud83d\udeab Restringido bloqueado (caché): {Placa}", placa);
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
                    _print.ImprimirTiqueteLocal(impresora, placa, tipoVehiculo,
                        horaEntrada, carrilNombre, false, qrToken: qrTokenLocal);
                return;
            }

            // 3. ABRIR BARRERA — único paso de red en el camino síncrono (~50ms LAN)
            await EjecutarApertura(lane, "ENTRADA");
            _logger.LogInformation("\u2705 Barrera abierta: {Placa} ({Motivo})", placa, motivo);

            // 4. Imprimir tiquete INMEDIATAMENTE con datos locales.
            //    El QR del tiquete de entrada es solo informativo — el QR que
            //    activa la talanquera de salida se carga en K2600 al momento del
            //    cobro (registrar-qr), no aquí. No hay razón para esperar al VPS.
            if (_parqueadero.EntregarTiquete && !string.IsNullOrEmpty(impresora))
                _print.ImprimirTiqueteLocal(impresora, placa, tipoVehiculo,
                    horaEntrada, carrilNombre, tieneConvenio, qrToken: qrTokenLocal);

            // 5. Encolar evento (background, scope propio, nunca bloquea)
            EncolarEvento(placa, lane, carrilNombre, "ENTRADA", true,
                motivo, tipoVehiculo, tieneConvenio, convenioId, imagenUrlTask, qrTokenLocal);
        }

        // ════════════════════════════════════════════════════════════════
        // ENCOLAR EVENTO — inserta en EventosLocales con scope propio.
        // Fire-and-forget seguro: IServiceScopeFactory crea su propio
        // DbContext independiente del scope del request (que puede estar
        // disposed cuando el Task.Run ejecute).
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
                // Esperar URL de imagen antes de insertar — la misma Task<string>
                // que ya está corriendo en background desde GuardarImagenes.
                // Múltiples awaits sobre la misma Task son seguros: el resultado
                // queda cacheado. Si la subida falla, imagenUrl queda "".
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
            // Sin cambios de comportamiento.
            var imagenUrl = await imagenUrlTask;

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
                    "\u2705 Salida autorizada (pag\u00f3 hace {Min} min): {Placa}",
                    (int)(DateTime.Now - registro.FechaSalida!.Value).TotalMinutes, placa);
                await RegistrarAccesoLocal(placa, lane, "SALIDA", true,
                    "PAGADO", "CASUAL", imagenUrl);
                await EjecutarApertura(lane, "SALIDA");
            }
            else
            {
                await RegistrarAccesoLocal(placa, lane, "SALIDA", false,
                    "NO_PAGADO", "BLOQUEADO", imagenUrl);
                _logger.LogWarning("\u26d4 Salida bloqueada (sin pago o gracia vencida): {Placa}", placa);
            }
        }

        // -- Salida nube --
        private async Task SalidaParqueaderoNube(
            string placa, string lane, Task<string> imagenUrlTask, string carrilNombre)
        {
            _logger.LogInformation("\ud83d\udeaa Salida \u2014 Placa:{Placa} Lane:{Lane}", placa, lane);

            // \u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550
            // NUEVA RUTA \u2014 local-first sin VPS en el camino s\u00edncrono.
            //
            // 1. Convenio activo \u2192 cach\u00e9 (0ms, sin SQL)
            // 2. PagosConfirmados \u2192 BD local (~20ms SQL, sin red)
            //    Poblado por webhook POST /api/print/confirmar-pago (Fase 3).
            // 3. Fallback \u2192 VPS salida-rapida (~2-5s) \u2014 activo hasta Fase 3.
            // \u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550

            // 1. Convenio activo \u2192 cach\u00e9, 0ms
            if (_cache.TieneConvenioActivo(placa, out _))
            {
                await EjecutarApertura(lane, "SALIDA");
                EncolarEvento(placa, lane, carrilNombre, "SALIDA", true,
                    "CONVENIO_ACTIVO", null, true, null, imagenUrlTask);
                _logger.LogInformation("\u2705 Salida convenio: {Placa}", placa);
                return;
            }

            // 2. Pago confirmado \u2192 BD local, single AnyAsync (~20ms)
            //    FechaExpira = FechaPago + TiempoGracia + 5min margen
            try
            {
                var pagadoLocal = await _db.PagosConfirmados
                    .AnyAsync(p => p.Placa == placa && p.FechaExpira >= DateTime.Now);

                if (pagadoLocal)
                {
                    await EjecutarApertura(lane, "SALIDA");
                    EncolarEvento(placa, lane, carrilNombre, "SALIDA", true,
                        "PAGADO", null, false, null, imagenUrlTask);
                    _logger.LogInformation("\u2705 Salida pagada (local): {Placa}", placa);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "\u26a0\ufe0f PagosConfirmados no disponible \u2014 fallback VPS: {Placa}", placa);
            }

            // 3. Fallback \u2192 VPS (hasta que webhook confirmar-pago est\u00e9 activo)
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

                _logger.LogInformation("\ud83d\udeaa Salida VPS {Placa}: Auth={A} Motivo={M}",
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
                    _logger.LogWarning("\u26d4 Salida bloqueada: {Placa}", placa);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "\u26a0\ufe0f Error salida {Placa} \u2014 fallback AbrirSiSinInternet", placa);
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
                // Porter\u00eda usa timer de entrada para ambos sentidos
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
                _logger.LogInformation("\u23f1 Timer {Ms}ms antes de abrir", ms);
                await Task.Delay(ms);
            }

            await AbrirBarrera(lane);
        }

        // Cliente HTTP de la talanquera, reutilizado entre TODAS las
        // aperturas. Antes AbrirBarrera creaba un HttpClientHandler nuevo
        // en cada llamada, forzando renegociar Digest (401 → reintento)
        // y abrir TCP nuevo en CADA carro. Con PreAuthenticate=true y
        // cliente reutilizado, el segundo carro en adelante evita el 401.
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

                _logger.LogInformation("\ud83d\udeaa Barrera {Door} \u2192 {Status}", doorId, r.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error abriendo barrera {Door}", doorId);
            }
        }

        // M\u00e9todo p\u00fablico para llamadas desde PrintController (ingreso manual)
        public async Task RegistrarQrPublico(int registroId, string qrToken)
            => await RegistrarQrEnControladora(registroId, qrToken);

        // =============================================
        // QR \u2014 REGISTRAR EN CONTROLADORA AL INGRESO
        // Usa PUT /ISAPI/AccessControl/CardInfo/record
        // =============================================

        // =============================================
        // QR \u2014 SINCRONIZAR DESDE VPS (ingreso manual)
        // Si ParkSky no devolvi\u00f3 QrToken, consultarlo
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
                _logger.LogWarning(ex, "\u26a0\ufe0f No se pudo sincronizar QR para {Placa}", placa);
            }
        }
        // =============================================
        // QR \u2014 CREAR CLIENTE HTTP CON DIGEST AUTH
        // Mismo patr\u00f3n que AbrirBarrera: GET /System/status
        // para forzar el challenge antes de POST/DELETE
        // =============================================
        // Extrae la ra\u00edz http://host de cualquier BaseUrl
        // Ej: "http://192.168.1.130/ISAPI/AccessControl/RemoteControl/door/"
        //   \u2192 "http://192.168.1.130"
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

            // Forzar challenge Digest igual que hace AbrirBarrera
            try
            {
                await client.GetAsync($"{ObtenerRaizControladora()}/System/status");
            }
            catch { }

            return client;
        }

        // =============================================
        // QR \u2014 REGISTRAR EN CONTROLADORA
        // Paso 1: crear usuario  POST /ISAPI/AccessControl/UserInfo/Record
        // Paso 2: asociar tarjeta POST /ISAPI/AccessControl/CardInfo/Record
        // =============================================
        private async Task RegistrarQrEnControladora(int registroId, string qrToken)
        {
            if (!_barrier.UsarQR || string.IsNullOrEmpty(qrToken)) return;
            if (_barrier.PuertaLectorQR == null || !_barrier.PuertaLectorQR.Any()) return;

            try
            {
                var baseUrl = ObtenerRaizControladora();
                using var client = await CrearClienteHikvision();
                var empNo = $"REG{registroId}";

                // \u2500\u2500 PASO 1: crear usuario con permiso en TODAS las puertas de salida \u2500\u2500
                // La controladora abre SOLO la puerta donde se presenta el QR f\u00edsicamente
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
                _logger.LogInformation("\ud83d\udcf1 QR usuario creado: REG{Id} puertas=[{Puertas}] \u2192 {Status}",
                    registroId,
                    string.Join(",", _barrier.PuertaLectorQR),
                    r1.StatusCode);

                if (!r1.IsSuccessStatusCode)
                {
                    _logger.LogWarning("\u26a0\ufe0f Error creando usuario QR REG{Id}: {Body}",
                        registroId, body1);
                    return;
                }

                // \u2500\u2500 PASO 2: asociar tarjeta QR \u2500\u2500
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
                _logger.LogInformation("\ud83d\udcf1 QR tarjeta asociada: REG{Id} \u2192 {Status}",
                    registroId, r2.StatusCode);

                if (!r2.IsSuccessStatusCode)
                    _logger.LogWarning("\u26a0\ufe0f Error asociando tarjeta QR REG{Id}: {Body}",
                        registroId, body2);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "\u26a0\ufe0f No se pudo registrar QR en controladora para REG{Id}", registroId);
            }
        }

        // =============================================
        // QR \u2014 ELIMINAR DE CONTROLADORA AL SALIR
        // Paso 1: eliminar tarjeta DELETE /ISAPI/AccessControl/CardInfo/Record
        // Paso 2: eliminar usuario  DELETE /ISAPI/AccessControl/UserInfo/Record
        // =============================================
        private async Task EliminarQrDeControladora(int registroId)
        {
            if (!_barrier.UsarQR) return;

            try
            {
                var baseUrl = ObtenerRaizControladora();

                using var client = await CrearClienteHikvision();

                var empNo = $"REG{registroId}";

                // \u2500\u2500 PASO 1: eliminar tarjeta \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
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

                _logger.LogInformation("\ud83d\uddd1\ufe0f QR tarjeta eliminada: REG{Id} \u2192 {Status} {Body}",
                    registroId, r1.StatusCode, body1);

                // \u2500\u2500 PASO 2: eliminar usuario \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
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

                _logger.LogInformation("\ud83d\uddd1\ufe0f QR usuario eliminado: REG{Id} \u2192 {Status} {Body}",
                    registroId, r2.StatusCode, body2);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "\u26a0\ufe0f No se pudo eliminar QR de controladora para REG{Id}", registroId);
            }
        }


        // =============================================
        // HELPERS
        // =============================================
        // Guarda imagen en disco LOCAL de forma s\u00edncrona (r\u00e1pido, I/O local)
        // y ARRANCA la subida al VPS sin esperarla \u2014 devuelve la Task ya en
        // marcha para que quien la necesite la awaitee cuando le toque.
        // Antes, esta misma llamada esperaba la subida completa (red) antes
        // de devolver el control a ProcesarAcceso, bloqueando entrada/salida
        // de TODOS los modos detr\u00e1s de cada subida de imagen.
        private async Task<Task<string>> GuardarImagenes(
            string placa, string lane, string absTime,
            IFormFile? plateImage, IFormFile? fullImage)
        {
            string fecha = absTime.Substring(0, 8);
            string nombre = $"{absTime}_{placa}_{lane}.jpg";

            // Leer ambas im\u00e1genes en memoria PRIMERO (los streams solo se leen una vez)
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

            _logger.LogInformation("\ud83d\udcf7 Bytes \u2014 plate:{P} full:{F}",
                plateBytes?.Length ?? 0, fullBytes?.Length ?? 0);

            // Guardar localmente como respaldo \u2014 I/O local, r\u00e1pido, se mantiene s\u00edncrono
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

            // Subir al VPS \u2014 usar foto completa, si no hay usar recorte placa.
            // NO se espera (await) esta llamada aqu\u00ed \u2014 se arranca y se
            // devuelve la Task en marcha. Antes este punto bloqueaba
            // ProcesarAcceso completo (~15-19s con varios carros seguidos)
            // antes incluso de abrir la talanquera.
            var imgBytes = fullBytes ?? plateBytes;
            var tipo = fullBytes != null ? "Completa" : "Placa";

            if (_parkSky != null && imgBytes != null && imgBytes.Length > 0)
                return SubirImagenVpsAsync(placa, lane, tipo, imgBytes);

            _logger.LogWarning("\u26a0\ufe0f Sin bytes de imagen para {Placa} \u2014 no se sube al VPS", placa);
            return Task.FromResult("");
        }

        // Sube la imagen al VPS de forma independiente. Mismo manejo de
        // errores que antes (silencioso, solo log): perder la foto nunca
        // debe bloquear el registro del acceso ni la apertura de la talanquera.
        private async Task<string> SubirImagenVpsAsync(
            string placa, string lane, string tipo, byte[] imgBytes)
        {
            try
            {
                var base64 = Convert.ToBase64String(imgBytes);
                var urlVps = await _parkSky.EnviarImagenAsync(placa, lane, tipo, base64);
                if (!string.IsNullOrEmpty(urlVps))
                {
                    _logger.LogInformation("\ud83d\uddbc\ufe0f Imagen VPS OK: {Url}", urlVps);
                    return urlVps;
                }
                _logger.LogWarning("\u26a0\ufe0f VPS no devolvi\u00f3 URL para {Placa}", placa);
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
                // BD local no disponible \u2014 solo loguear, no interrumpir el flujo
                _logger.LogWarning("\u26a0\ufe0f BD local no disponible (AccesoVehicular no guardado): {Msg}", ex.Message);
            }
        }

        private async Task RegistrarIngresoLocal(
            string placa, string lane, bool esMensualidad, int? convenioId)
        {
            try
            {
                // 1. Buscar o crear veh\u00edculo
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

                // 2. Buscar tarifa por tipo de veh\u00edculo
                var tipo = DetectarTipoVehiculo(placa);
                var tarifa = await _db.Tarifas
                    .FirstOrDefaultAsync(t =>
                        t.Activa &&
                        t.TipoVehiculo != null &&
                        t.TipoVehiculo.Contains(tipo));

                // Si no encuentra tarifa espec\u00edfica, usar la primera activa
                tarifa ??= await _db.Tarifas.FirstOrDefaultAsync(t => t.Activa);

                if (tarifa == null)
                {
                    _logger.LogWarning("\u26a0\ufe0f Sin tarifa disponible para {Placa} \u2014 no se crea registro local", placa);
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

                _logger.LogInformation("\u2705 Registro local creado: {Placa} Id={Id}", placa, registro.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("\u26a0\ufe0f BD local no disponible (RegistroLocal no guardado): {Msg}", ex.Message);
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
                            "\ud83d\udda8\ufe0f Imprimiendo ticket directo RegistroId={Id}",
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

            // Fallback \u2014 imprimir con datos locales si ParkSky no disponible
            _print.ImprimirTiqueteLocal(
                impresora, placa, tipo, DateTime.Now, carrilNombre, false);
        }

        private async Task CerrarRegistroLocal(string placa)
        {
            _logger.LogInformation("Salida local registrada: {Placa}", placa);
        }

        private string DetectarTipoVehiculo(string placa)
        {
            // 1. Consultar patrones personalizados en BD (m\u00e1xima prioridad)
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

            // 2. Reglas colombianas est\u00e1ndar
            return DetectarTipoPlacaColombia(placa);
        }

        /// Detecta tipo de veh\u00edculo por formato de placa colombiana.
        /// Retorna "Carro", "Moto" o null si el formato no es reconocido.
        private static string? DetectarTipoPlacaColombia(string placa)
        {
            if (string.IsNullOrWhiteSpace(placa)) return null;
            placa = placa.ToUpper().Trim();
            int n = placa.Length;
            bool L(int i) => i < n && char.IsLetter(placa[i]);
            bool D(int i) => i < n && char.IsDigit(placa[i]);

            // AAA### (6) \u2192 Carro
            if (n == 6 && L(0) && L(1) && L(2) && D(3) && D(4) && D(5))
                return "Carro";

            // AAA##A (6) \u2192 Moto nueva
            if (n == 6 && L(0) && L(1) && L(2) && D(3) && D(4) && L(5))
                return "Moto";

            // AAA## (5) \u2192 Moto antigua (sin letra final)
            if (n == 5 && L(0) && L(1) && L(2) && D(3) && D(4))
                return "Moto";

            return null; // formato no reconocido \u2192 descartar en c\u00e1maras
        }


        /// Misma l\u00f3gica que ControlController.CoincidePatron
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