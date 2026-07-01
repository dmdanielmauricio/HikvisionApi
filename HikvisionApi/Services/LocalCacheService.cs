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

        // Devuelve true si la placa tiene convenio vigente hoy.
        // Si true, rellena convenioId con el id del convenio.
        public bool TieneConvenioActivo(string placa, out int? convenioId)
        {
            if (_convenios.TryGetValue(placa.ToUpper().Trim(), out var c)
                && c.FechaFin >= DateTime.Today)
            {
                convenioId = c.ConvenioId;
                return true;
            }
            convenioId = null;
            return false;
        }

        // Fuerza un refresh inmediato — útil después de cambios admin
        public Task RefreshNowAsync() => RefreshAllAsync();

        // ── Refresh interno ───────────────────────────────────────────
        private async Task RefreshAllAsync()
        {
            await RefreshRestringidosAsync();
            await RefreshConveniosAsync();
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

                var convenios = await db.ConveniosVehiculos
                    .Include(cv => cv.ConvenioMensualidad)
                    .Where(cv => cv.Activo && cv.ConvenioMensualidad.FechaFin >= hoy)
                    .Select(cv => new ConvenioCacheDto
                    {
                        Placa = cv.Placa.ToUpper().Trim(),
                        ConvenioId = cv.ConvenioMensualidadId,
                        FechaFin = cv.ConvenioMensualidad.FechaFin
                    })
                    .ToListAsync();

                // En caso de placas duplicadas, tomar el convenio con FechaFin más lejana
                var dic = convenios
                    .GroupBy(c => c.Placa, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableDictionary(
                        g => g.Key,
                        g => g.OrderByDescending(c => c.FechaFin).First(),
                        StringComparer.OrdinalIgnoreCase);

                _convenios = dic;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocalCacheService: no se pudo refrescar convenios — manteniendo caché anterior");
            }
        }
    }

    public class ConvenioCacheDto
    {
        public string Placa { get; set; } = "";
        public int? ConvenioId { get; set; }
        public DateTime FechaFin { get; set; }
    }
}
