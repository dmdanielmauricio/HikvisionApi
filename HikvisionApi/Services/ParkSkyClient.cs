using HikvisionApi.Config;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace HikvisionApi.Services
{
    public class ParkSkyClient
    {
        private readonly HttpClient _http;
        private readonly ParkSkySettings _settings;
        private readonly ILogger<ParkSkyClient> _logger;

        public ParkSkyClient(
            HttpClient http,
            IOptions<ParkSkySettings> settings,
            ILogger<ParkSkyClient> logger)
        {
            _http = http;
            _settings = settings.Value;
            _logger = logger;

            if (!string.IsNullOrEmpty(_settings.ApiUrl))
            {
                _http.BaseAddress = new Uri(_settings.ApiUrl.TrimEnd('/') + "/");
                _http.DefaultRequestHeaders.Add("X-Api-Key", _settings.ApiKey);
            }
            _http.Timeout = TimeSpan.FromSeconds(10);
        }

        // =============================================
        // VALIDAR CONVENIO
        // =============================================
        public async Task<ConvenioResponse> ValidarConvenioAsync(string placa)
        {
            try
            {
                var r = await _http.GetAsync($"api/hikvision/validar-convenio?placa={Uri.EscapeDataString(placa)}");
                var json = await r.Content.ReadAsStringAsync();
                _logger.LogInformation("ValidarConvenio {Placa}: {Json}", placa, json);
                return Deserializar<ConvenioResponse>(json) ?? new ConvenioResponse();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validando convenio {Placa}", placa);
                return new ConvenioResponse { Ok = false };
            }
        }

        // =============================================
        // REGISTRAR INGRESO
        // CAMBIO: agrega qrToken opcional — generado localmente en EntradaParqueaderoNube
        // con el mismo algoritmo que usa el VPS. El VPS lo recibe y, si no es null,
        // lo usa como QrToken del RegistroParqueo en lugar de generar uno propio.
        // Esto garantiza que el QR del tiquete de entrada coincida con el que se
        // carga en K2600 al cobrar (via /api/print/registrar-qr).
        // =============================================
        public async Task<IngresoResponse> RegistrarIngresoAsync(
            string placa, string carril, string carrilNombre,
            bool esMensualidad, int? convenioId, string? imagenUrl,
            string? tipoVehiculo = null,
            string? qrToken = null)
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    placa,
                    carril,
                    carrilNombre,
                    esMensualidad,
                    convenioId,
                    imagenUrl,
                    tipoVehiculo,
                    qrToken
                });
                var r = await _http.PostAsync("api/hikvision/procesar-entrada",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var json = await r.Content.ReadAsStringAsync();
                _logger.LogInformation("ProcesarEntrada {Placa}: {Json}", placa, json);
                return Deserializar<IngresoResponse>(json) ?? new IngresoResponse();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registrando ingreso {Placa}", placa);
                return new IngresoResponse { Ok = false };
            }
        }

        // =============================================
        // VALIDAR SALIDA
        // =============================================
        public async Task<SalidaResponse> ValidarSalidaAsync(
            string placa, string carril, string? imagenUrl, string? carrilNombre)
        {
            try
            {
                var gracia = _settings.TiempoGraciaMinutos;
                var body = JsonSerializer.Serialize(new
                {
                    placa,
                    carril,
                    carrilNombre,
                    imagenUrl,
                    tiempoGraciaMinutos = gracia
                });
                var r = await _http.PostAsync("api/hikvision/procesar-salida",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var json = await r.Content.ReadAsStringAsync();
                _logger.LogInformation("ProcesarSalida {Placa}: {Json}", placa, json);
                return Deserializar<SalidaResponse>(json) ?? new SalidaResponse();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validando salida {Placa}", placa);
                return new SalidaResponse { Ok = false, Autorizado = false };
            }
        }

        // =============================================
        // OBTENER DATOS TICKET
        // =============================================
        public async Task<TicketResponse?> ObtenerTicketAsync(int registroId)
        {
            try
            {
                var r = await _http.GetAsync($"api/hikvision/ticket?registroId={registroId}");
                var json = await r.Content.ReadAsStringAsync();
                _logger.LogInformation("ObtenerTicket {Id}: {Json}", registroId, json);
                return Deserializar<TicketResponse>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo ticket {Id}", registroId);
                return null;
            }
        }

        // =============================================
        // ENVIAR IMAGEN AL VPS (Base64 temporal)
        // =============================================
        public async Task<string?> EnviarImagenAsync(
            string placa, string carril, string tipoImagen, string base64)
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    placa,
                    carril,
                    tipoImagen,
                    imagenBase64 = base64
                });
                var r = await _http.PostAsync("api/hikvision/imagen",
                    new StringContent(body, Encoding.UTF8, "application/json"));

                if (r.IsSuccessStatusCode)
                {
                    var json = await r.Content.ReadAsStringAsync();
                    _logger.LogInformation("\ud83d\udce4 ImgVPS respuesta: {Json}", json);
                    var obj = JsonSerializer.Deserialize<JsonElement>(json);
                    if (obj.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetInt32();
                        var baseUrl = _settings.ApiUrl.TrimEnd('/');
                        return $"{baseUrl}/api/hikvision/imagen/{id}";
                    }
                    _logger.LogWarning("\ud83d\udce4 ImgVPS sin 'id' en respuesta: {Json}", json);
                }
                else
                {
                    var err = await r.Content.ReadAsStringAsync();
                    _logger.LogWarning("\ud83d\udce4 ImgVPS error {Status}: {Err}", r.StatusCode, err);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo enviar imagen al VPS para {Placa}", placa);
            }
            return null;
        }

        /// GET simple — devuelve JSON crudo. Para endpoints de lectura r\u00e1pida.
        public async Task<string> GetRawAsync(string relativeUrl)
        {
            var r = await _http.GetAsync(relativeUrl);
            return await r.Content.ReadAsStringAsync();
        }

        // =============================================
        // PAGOS RECIENTES — polling para PagosConfirmados local
        // Reemplaza la llamada browser→local (bloqueada por CSP y mixed content).
        // SyncBackgroundService llama esto cada ciclo y puebla PagosConfirmados.
        // =============================================
        public async Task<List<PagoRecienteDto>> GetPagosRecientesAsync(DateTime desde)
        {
            try
            {
                var url = $"api/hikvision/pagos-recientes?desde={desde:o}";
                var r = await _http.GetAsync(url);
                var json = await r.Content.ReadAsStringAsync();
                var resp = Deserializar<PagosRecientesResponse>(json);
                return resp?.Pagos ?? new List<PagoRecienteDto>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudieron obtener pagos recientes del VPS");
                return new List<PagoRecienteDto>();
            }
        }

        private static T? Deserializar<T>(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return default; }
        }
    }

    // =============================================
    // DTOs
    // =============================================
    public class ConvenioResponse
    {
        public bool Ok { get; set; }
        public bool TieneConvenio { get; set; }
        public bool Activo { get; set; }
        public string? NombreConvenio { get; set; }
        public int? ConvenioId { get; set; }
        public string? TipoVehiculo { get; set; }
        public string? Mensaje { get; set; }
    }

    public class IngresoResponse
    {
        public bool Ok { get; set; }
        public bool Autorizado { get; set; }
        public int? RegistroId { get; set; }
        public string? Placa { get; set; }
        public string? Tipo { get; set; }
        public string? Motivo { get; set; }
        public string? Mensaje { get; set; }
        public string? QrToken { get; set; }
    }

    public class SalidaResponse
    {
        public bool Ok { get; set; }
        public bool Autorizado { get; set; }
        public string? Motivo { get; set; }
        public string? Mensaje { get; set; }
        public int? RegistroId { get; set; }
        public string? Entrada { get; set; }
    }

    public class TicketResponse
    {
        public bool Ok { get; set; }
        public string? NombreParqueadero { get; set; }
        public string? Nit { get; set; }
        public string? Direccion { get; set; }
        public string? Telefono { get; set; }
        public string? MensajeEncabezado { get; set; }
        public string? MensajePie { get; set; }
        public string? MensajeObservacion { get; set; }
        public string? Placa { get; set; }
        public string? TipoVehiculo { get; set; }
        public int RegistroId { get; set; }
        public string? FechaEntrada { get; set; }
        public string? Tarifa { get; set; }
        public bool EsMensualidad { get; set; }
        public string? NombreConvenio { get; set; }
        public string? VigenciaFin { get; set; }
        public string? QrToken { get; set; }
    }

    // Respuesta de GET /api/hikvision/pagos-recientes
    public class PagoRecienteDto
    {
        public string Placa { get; set; } = "";
        public int RegistroId { get; set; }
        public decimal ValorPagado { get; set; }
        public DateTime FechaPago { get; set; }
        public string? QrToken { get; set; }
    }

    public class PagosRecientesResponse
    {
        public bool Ok { get; set; }
        public List<PagoRecienteDto>? Pagos { get; set; }
    }
}