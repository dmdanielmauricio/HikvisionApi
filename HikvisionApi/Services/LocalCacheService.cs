using HikvisionApi.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;

namespace HikvisionApi.Services
{
    // Singleton que mantiene en memoria:
    //   - Placas restringidas (HashSet, refresh 60s)
    //   - Convenios activos por placa (Dictionary, refresh 60s)
    //
    // Usar en HikvisionService para eliminar consultas SQL del camino síncrono.
    // EsRestringido + TieneConvenioActivo son operaciones O(1) sin red ni BD.
    //
    // Registrar en Program.cs:
    //   builder.Services.AddSingleton<LocalCacheService>();
    //   builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalCacheService>());
    // El segundo registro hace que ASP.NET Core llame StartAsync al arrancar,
    // llenando el caché antes del primer request.
    public class LocalCacheService : IHostedService, IDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LocalCacheService> _logger;
        private Timer? _timer;

        // Reemplazadas atómicamente en cada refresh — lecturas lock-free
        private volatile ImmutableHashSet<string> _restringidos =
            ImmutableHashSet<string>.Empty;
        private volatile ImmutableDictionary<string, ConvenioCacheDto> _convenios =
            ImmutableDictionary<string, ConvenioCacheDto>.Empty;
        // Placas con PagoDía vigente (refresh 60s)
        private volatile ImmutableHashSet<string> _pagoDia =
            ImmutableHashSet<string>.Empty;
        private volatile HikvisionApi.Models.ConfiguracionLocal? _configuracion;

        public LocalCacheService(
            IServiceScopeFactory scopeFactory,
            ILogger<LocalCacheService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        // ── IHostedService ────────────────────────────────────────────
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("LocalCacheService: cargando caché inicial...");
            _ = Task.Run(RefreshAllAsync, cancellationToken);

            // Refresh cada 60 segundos en background
            _timer = new Timer(
                _ => _ = Task.Run(RefreshAllAsync),
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose() => _timer?.Dispose();

        // ── API pública — O(1), sin awaits ───────────────────────────

        // Devuelve true si la placa está actualmente restringida.
        public bool EsRestringido(string placa)
            => _restringidos.Contains(placa.ToUpper().Trim());

        // Devuelve la configuración del parqueadero cacheada desde la tabla Configuraciones.
        // Nunca retorna null — si la BD no responde usa un objeto vacío como fallback.
        public HikvisionApi.Models.ConfiguracionLocal GetConfiguracion()
            => _configuracion ?? new HikvisionApi.Models.ConfiguracionLocal
            {
                NombreParqueadero = "PARQUEADERO",
                MensajePie = "Conserve este tiquete para su salida"
            };

        // Devuelve true si la placa tiene convenio vigente hoy.
        // Si true, rellena convenioId con el id del convenio.
        //
        // Vigente incluye el tiempo de prórroga: el convenio sigue operando
        // hasta FechaFin + DiasProrroga. Misma regla que aplica el VPS en
        // ConvenioMensualidad.EstaVigente — si estas dos se desincronizan,
        // la talanquera y el registro del VPS dejan de coincidir.
        public bool TieneConvenioActivo(string placa, out int? convenioId)
        {
            if (_convenios.TryGetValue(placa.ToUpper().Trim(), out var c)
                && c.FechaLimite >= DateTime.Today)
            {
                convenioId = c.ConvenioId;
                return true;
            }
            convenioId = null;
            return false;
        }

        // True si la placa está vigente pero ya pasó su FechaFin — es decir,
        // está corriendo los días de prórroga para pagar.
        public bool EstaEnProrroga(string placa)
            => _convenios.TryGetValue(placa.ToUpper().Trim(), out var c)
               && c.FechaFin < DateTime.Today
               && c.FechaLimite >= DateTime.Today;

        // Fuerza un refresh inmediato — útil después de cambios admin
        /// Retorna true si la placa tiene Pago Día activo y no vencido.
        /// O(1) sin red ni BD — leído de caché en memoria.
        public bool TienePagoDiaVigente(string placa)
            => _pagoDia.Contains(placa.ToUpper().Trim());

        public Task RefreshNowAsync() => RefreshAllAsync();

        // ── Refresh interno ───────────────────────────────────────────
        private async Task RefreshAllAsync()
        {
            await RefreshRestringidosAsync();
            await RefreshConveniosAsync();
            await RefreshPagoDiaAsync();
            await RefreshConfiguracionAsync();
            _logger.LogDebug("LocalCacheService: caché refrescado — {R} restringidos, {C} convenios",
                _restringidos.Count, _convenios.Count);
        }

        private async Task RefreshRestringidosAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var placas = await db.VehiculosRestringidos
                    .Where(v => v.Activo)
                    .Select(v => v.Placa.ToUpper().Trim())
                    .ToListAsync();

                _restringidos = placas.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // Mantener caché anterior en caso de falla de BD
                _logger.LogWarning(ex, "LocalCacheService: no se pudo refrescar restringidos — manteniendo caché anterior");
            }
        }

        private async Task RefreshConveniosAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var hoy = DateTime.Today;

                // Incluye los convenios en prórroga: DateDiffDay(FechaFin, hoy)
                // <= DiasProrroga cubre a la vez la vigencia normal (diferencia
                // negativa) y los días de gracia posteriores a FechaFin.
                var convenios = await db.ConveniosVehiculos
                    .Include(cv => cv.ConvenioMensualidad)
                    .Where(cv => cv.Activo &&
                        EF.Functions.DateDiffDay(
                            cv.ConvenioMensualidad.FechaFin, hoy)
                            <= cv.ConvenioMensualidad.DiasProrroga)
                    .Select(cv => new ConvenioCacheDto
                    {
                        Placa = cv.Placa.ToUpper().Trim(),
                        ConvenioId = cv.ConvenioMensualidadId,
                        FechaFin = cv.ConvenioMensualidad.FechaFin,
                        DiasProrroga = cv.ConvenioMensualidad.DiasProrroga
                    })
                    .ToListAsync();

                // En caso de placas duplicadas, tomar el convenio que cubra hasta
                // más lejos — contando la prórroga, no solo FechaFin.
                var dic = convenios
                    .GroupBy(c => c.Placa, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableDictionary(
                        g => g.Key,
                        g => g.OrderByDescending(c => c.FechaLimite).First(),
                        StringComparer.OrdinalIgnoreCase);

                _convenios = dic;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocalCacheService: no se pudo refrescar convenios — manteniendo caché anterior");
            }
        }

        private async Task RefreshPagoDiaAsync()
        {
            // EsPagoDia y PagoDiaHasta son campos del VPS (RegistroParqueo),
            // no del DB local (RegistroLocal). Consultamos el VPS via ParkSkyClient.
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var parkSky = scope.ServiceProvider.GetRequiredService<ParkSkyClient>();

                var raw = await parkSky.GetRawAsync("api/hikvision/placas-pago-dia");
                using var doc = System.Text.Json.JsonDocument.Parse(raw);

                var placas = doc.RootElement
                    .GetProperty("placas")
                    .EnumerateArray()
                    .Select(p => p.GetString()?.ToUpper().Trim() ?? "")
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                _pagoDia = placas.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                _logger.LogDebug("LocalCacheService: {N} placas con PagoDía vigente", _pagoDia.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LocalCacheService: no se pudo refrescar PagoDía — manteniendo caché anterior");
            }
        }

        private async Task RefreshConfiguracionAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                _configuracion = await db.Configuraciones.FirstOrDefaultAsync();
                if (_configuracion != null)
                    _logger.LogDebug("LocalCacheService: configuración cargada — {Nombre}",
                        _configuracion.NombreParqueadero);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocalCacheService: no se pudo refrescar configuración");
            }
        }
    }

    public class ConvenioCacheDto
    {
        public string Placa { get; set; } = "";
        public int? ConvenioId { get; set; }
        public DateTime FechaFin { get; set; }
        public int DiasProrroga { get; set; }

        /// Último día en que el convenio sigue operando (fin + prórroga).
        public DateTime FechaLimite => FechaFin.Date.AddDays(DiasProrroga);
    }
}