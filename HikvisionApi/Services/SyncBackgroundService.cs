using HikvisionApi.Data;
using HikvisionApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HikvisionApi.Services
{
    // BackgroundService que sincroniza EventosLocales al VPS cada 5 segundos.
    // Crea su propio scope por ciclo → DbContext y ParkSkyClient siempre frescos.
    //
    // Lógica de sincronización:
    //   ENTRADA autorizada → api/hikvision/procesar-entrada → guarda RegistroVpsId
    //   SALIDA  autorizada → api/hikvision/procesar-salida  → cierra RegistroParqueo en VPS
    //   Cualquier no-autorizada → marca Sincronizado=true sin llamar al VPS
    //     (accesos bloqueados no generan RegistroParqueo en VPS, solo AccesosHikvision)
    //
    // Reintentos: máximo 5 por evento. Después se descarta (ErrorSincronizacion queda
    // visible en la tabla local para diagnóstico).
    //
    // Registrar en Program.cs:
    //   builder.Services.AddHostedService<SyncBackgroundService>();
    public class SyncBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SyncBackgroundService> _logger;

        private const int IntervaloMs = 5_000; // 5 segundos
        private const int MaxReintentos = 5;
        private const int LotePorCiclo = 10;    // no saturar al VPS

        public SyncBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<SyncBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SyncBackgroundService iniciado — ciclo cada {Seg}s", IntervaloMs / 1000);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SincronizarLoteAsync();
                    await SincronizarPagosAsync(); // polling pagos confirmados en VPS
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SyncBackgroundService: error en ciclo de sincronización");
                }

                await Task.Delay(IntervaloMs, stoppingToken);
            }
        }

        // ── POLLING PAGOS CONFIRMADOS ─────────────────────────────────────
        // El browser no puede llamar a la API local desde la página HTTPS del
        // VPS (bloqueado por CSP connect-src 'self' y mixed content HTTPS→HTTP).
        // Solución: la API local consulta al VPS (dirección ya establecida).
        // Cada ciclo trae los pagos confirmados desde la última revisión e
        // inserta en PagosConfirmados los que no existan aún.
        // SalidaParqueaderoNube lee PagosConfirmados local (~20ms) en vez de
        // llamar al VPS (~2-5s) para autorizar cada salida.
        // ─────────────────────────────────────────────────────────────────
        private DateTime _ultimaRevisionPagos = DateTime.Now.AddMinutes(-30);

        private async Task SincronizarPagosAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var parkSky = scope.ServiceProvider.GetRequiredService<ParkSkyClient>();
                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

                var graciaMinutos = config.GetValue<int>("Parqueadero:TiempoGraciaMinutos", 15);

                var pagos = await parkSky.GetPagosRecientesAsync(_ultimaRevisionPagos);
                if (!pagos.Any()) return;

                int insertados = 0;
                foreach (var pago in pagos)
                {
                    // Evitar duplicados por RegistroId
                    bool existe = await db.PagosConfirmados
                        .AnyAsync(p => p.RegistroVpsId == pago.RegistroId);
                    if (existe) continue;

                    db.PagosConfirmados.Add(new HikvisionApi.Models.PagoConfirmado
                    {
                        Placa = pago.Placa.ToUpper().Trim(),
                        RegistroVpsId = pago.RegistroId,
                        ValorPagado = pago.ValorPagado,
                        FechaPago = pago.FechaPago,
                        FechaExpira = pago.FechaPago.AddMinutes(graciaMinutos + 5),
                        QrToken = pago.QrToken
                    });
                    insertados++;
                }

                if (insertados > 0)
                {
                    await db.SaveChangesAsync();
                    _logger.LogInformation(
                        "✅ PagosConfirmados: {N} nuevos desde VPS", insertados);
                }

                // Avanzar el cursor para el próximo ciclo
                _ultimaRevisionPagos = DateTime.Now.AddSeconds(-10); // 10s de solapamiento
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Error sincronizando PagosConfirmados");
            }
        }

        private async Task SincronizarLoteAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var parkSky = scope.ServiceProvider.GetRequiredService<ParkSkyClient>();

            var pendientes = await db.EventosLocales
                .Where(e => !e.Sincronizado && e.IntentosSincronizacion < MaxReintentos)
                .OrderBy(e => e.FechaHora)
                .Take(LotePorCiclo)
                .ToListAsync();

            if (!pendientes.Any()) return;

            _logger.LogInformation("SyncBackgroundService: sincronizando {N} eventos", pendientes.Count);

            foreach (var ev in pendientes)
            {
                try
                {
                    bool ok;
                    bool errorTransporte = false;
                    int? registroVpsId = null;

                    if (!ev.Autorizado)
                    {
                        // Accesos bloqueados: registrados localmente pero no generan
                        // RegistroParqueo en VPS — marcar como procesado sin llamada
                        ok = true;
                    }
                    else if (ev.TipoMovimiento == "ENTRADA")
                    {
                        // ── ANTI-DUPLICADO SYNC ────────────────────────────────────
                        // Antes de crear registro en VPS, verificar si ya existe otro
                        // evento ENTRADA para la misma placa que ya fue sincronizado
                        // recientemente (< 10 min). Esto cubre el escenario:
                        //   cámara dispara → sync pendiente → operador ingresa manual
                        //   → sync llega → NO debe crear duplicado en VPS.
                        var yaSync = await db.EventosLocales
                            .Where(e => e.Placa == ev.Placa
                                     && e.TipoMovimiento == "ENTRADA"
                                     && e.Sincronizado
                                     && e.Id != ev.Id
                                     && e.FechaSincronizado.HasValue
                                     && e.FechaSincronizado.Value > DateTime.Now.AddMinutes(-10))
                            .Select(e => e.RegistroVpsId)
                            .FirstOrDefaultAsync();

                        if (yaSync.HasValue)
                        {
                            _logger.LogWarning(
                                "⛔ Anti-dup Sync: {Placa} ya sincronizada recientemente (VpsId={Id}), omitiendo duplicado",
                                ev.Placa, yaSync.Value);
                            ev.Sincronizado = true;
                            ev.FechaSincronizado = DateTime.Now;
                            ev.RegistroVpsId = yaSync.Value;
                            await db.SaveChangesAsync();
                            continue;
                        }
                        // ──────────────────────────────────────────────────────────

                        var resp = await parkSky.RegistrarIngresoAsync(
                            ev.Placa, ev.Carril, ev.CarrilNombre ?? "",
                            ev.EsMensualidad, ev.ConvenioId,
                            ev.ImagenUrl,
                            ev.TipoVehiculo,
                            ev.QrToken); // token local → VPS lo usa en RegistroParqueo

                        ok = resp.Ok;
                        errorTransporte = resp.ErrorTransporte;
                        registroVpsId = resp.RegistroId;
                    }
                    else // SALIDA autorizada
                    {
                        var resp = await parkSky.ValidarSalidaAsync(
                            ev.Placa, ev.Carril, ev.ImagenUrl, ev.CarrilNombre);

                        ok = resp.Ok;
                        errorTransporte = resp.ErrorTransporte;
                        registroVpsId = resp.RegistroId;
                    }

                    // El VPS no contestó (401, 5xx, timeout). Eso NO es un
                    // rechazo de negocio: dejar el evento pendiente sin gastar
                    // reintentos. Si contáramos estos fallos, una API Key mal
                    // configurada o media hora de VPS caído borraría en 25s
                    // todos los ingresos de esa ventana — sin dejar rastro
                    // salvo ErrorSincronizacion en la tabla local.
                    if (errorTransporte)
                    {
                        ev.ErrorSincronizacion =
                            "VPS inalcanzable o rechazó la autenticación — pendiente de reintento";
                        _logger.LogWarning(
                            "🔌 Evento {Id} ({Placa} {Tipo}) queda pendiente: no se pudo " +
                            "contactar al VPS. No se consume reintento.",
                            ev.Id, ev.Placa, ev.TipoMovimiento);
                        continue;
                    }

                    if (ok)
                    {
                        ev.Sincronizado = true;
                        ev.FechaSincronizado = DateTime.Now;
                        ev.RegistroVpsId = registroVpsId;
                        ev.ErrorSincronizacion = null;
                        _logger.LogDebug("✅ Evento {Id} sincronizado: {Placa} {Tipo} → VpsId={VId}",
                            ev.Id, ev.Placa, ev.TipoMovimiento, registroVpsId);
                    }
                    else
                    {
                        ev.IntentosSincronizacion++;
                        ev.ErrorSincronizacion = "VPS rechazó el evento";
                        _logger.LogWarning("⚠️ VPS rechazó evento {Id}: {Placa} {Tipo} (intento {N})",
                            ev.Id, ev.Placa, ev.TipoMovimiento, ev.IntentosSincronizacion);
                    }
                }
                catch (Exception ex)
                {
                    ev.IntentosSincronizacion++;
                    ev.ErrorSincronizacion = ex.Message.Length > 200
                        ? ex.Message[..200]
                        : ex.Message;
                    _logger.LogWarning(ex, "⚠️ Error sincronizando evento {Id}: {Placa} (intento {N})",
                        ev.Id, ev.Placa, ev.IntentosSincronizacion);
                }
            }

            await db.SaveChangesAsync();
        }
    }
}