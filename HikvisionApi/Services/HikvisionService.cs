using HikvisionApi.Config;
using HikvisionApi.Data;
using HikvisionApi.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace HikvisionApi.Services
{
    public class HikvisionService
    {
        private readonly AppDbContext _db;
        private readonly AnprSettings _anprSettings;
        private readonly BarrierSettings _barrierSettings;
        private readonly string _rawPath;
        private readonly string _logPath;

        public HikvisionService(
            IOptions<AnprSettings> anprSettings,
            IOptions<BarrierSettings> barrierSettings,
            AppDbContext db)
        {
            _anprSettings = anprSettings.Value;
            _barrierSettings = barrierSettings.Value;
            _db = db;
            _rawPath = _anprSettings.RawFolder;
            _logPath = _anprSettings.LogsFolder;

           
        }
        public async Task GuardarRaw(IFormCollection form, string contentType, string method)
        {
            // ✅ asegurar carpetas correctas
            Directory.CreateDirectory(_rawPath);
            Directory.CreateDirectory(_logPath);

            var logFile = Path.Combine(_logPath, "api_log.txt");

            using var log = new StreamWriter(logFile, true);

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            await log.WriteLineAsync($"[{now}] ========= NUEVO REQUEST =========");
            await log.WriteLineAsync($"[{now}] Método: {method}");
            await log.WriteLineAsync($"[{now}] Content-Type: {contentType}");

            foreach (var file in form.Files)
            {
                // 🔥 limpiar nombre (evita rutas raras de Hikvision)
                var cleanName = Path.GetFileName(file.FileName);

                var filePath = Path.Combine(_rawPath, cleanName);

                // 🔥 sobrescribir SIEMPRE
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                await log.WriteLineAsync($"[{now}] Archivo: {file.Name} - {cleanName} ({file.Length} bytes)");
            }

            await log.WriteLineAsync(""); // salto de línea
        }
        public async Task ProcesarAcceso(

            string placa,
            string lane,
            string absTime,
            IFormFile? plateImage,
            IFormFile? fullImage)
        {
            Console.WriteLine($"📸 {placa} - carril {lane}");

            // 🔥 NORMALIZAR absTime
            if (string.IsNullOrEmpty(absTime) || absTime.Length < 17)
                absTime = DateTime.Now.ToString("yyyyMMddHHmmssfff");

            string fecha = absTime.Substring(0, 8); // 👈 clave

            string basePath = _anprSettings.TargetFolder;

            string carpetaPlaca = Path.Combine(basePath, $"Camara{lane}", fecha);
            string carpetaGeneral = Path.Combine(basePath, $"Camara{lane}X", fecha);

            Directory.CreateDirectory(carpetaPlaca);
            Directory.CreateDirectory(carpetaGeneral);
           

            string fileName = $"{absTime}_{placa}_{lane}.jpg";

            string pathPlaca = Path.Combine(carpetaPlaca, fileName);
            string pathGeneral = Path.Combine(carpetaGeneral, fileName);

            // 📸 Guardar imagen placa
            if (plateImage != null)
            {
                using var stream = new FileStream(pathPlaca, FileMode.Create);
                await plateImage.CopyToAsync(stream);
            }

            // 📸 Guardar imagen completa
            if (fullImage != null)
            {
                using var stream = new FileStream(pathGeneral, FileMode.Create);
                await fullImage.CopyToAsync(stream);
            }

            // 🔗 Ruta BD
            string imagenUrl = $"/anpr/Camara{lane}/{fecha}/{fileName}";

            await ValidarYRegistrar(placa, lane, imagenUrl);
        }

        private async Task AbrirBarrera(string lane)
        {
            string doorId = lane switch
            {
                "1" => _barrierSettings.Doors.Entrada1,
                "2" => _barrierSettings.Doors.Salida1,
                "3" => _barrierSettings.Doors.Entrada2,
                "4" => _barrierSettings.Doors.Salida2,
                _ => _barrierSettings.Doors.Entrada1
            };

            string url = _barrierSettings.BaseUrl + doorId;

            var handler = new HttpClientHandler()
            {
                Credentials = new NetworkCredential(
                    _barrierSettings.Username,
                    _barrierSettings.Password),
                PreAuthenticate = true,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };

            var client = new HttpClient(handler);

            // 🔥 handshake obligatorio Hikvision
            await client.GetAsync(
                _barrierSettings.BaseUrl.Replace(
                    "/AccessControl/RemoteControl/door/",
                    "/System/status"));

            var xml = @"<RemoteControlDoor>
                        <cmd>open</cmd>
                    </RemoteControlDoor>";

            var content = new StringContent(xml, Encoding.UTF8, "application/xml");

            var response = await client.PutAsync(url, content);

            Console.WriteLine($"🚪 Barrera {doorId} → {response.StatusCode}");

            var resp = await response.Content.ReadAsStringAsync();
            Console.WriteLine(resp);
        }

        private async Task ValidarYRegistrar(string placa, string lane, string imagenUrl)
        {
            var vehiculo = await _db.Vehiculos
                .FirstOrDefaultAsync(v => v.Placa == placa);

            bool autorizado = vehiculo != null && vehiculo.Activo;

            string tipoMovimiento =
                (lane == "1" || lane == "3") ? "ENTRADA" : "SALIDA";

            string motivo = autorizado ? "OK" : "NO AUTORIZADO";

            var acceso = new AccesoVehicular
            {
                Placa = placa,
                Carril = lane,
                FechaHora = DateTime.Now,
                Autorizado = autorizado,
                TipoMovimiento = tipoMovimiento,
                Motivo = motivo,
                ImagenUrl = imagenUrl
            };

            _db.AccesosVehiculares.Add(acceso);
            await _db.SaveChangesAsync();

            if (autorizado)
            {
                Console.WriteLine("✅ Vehículo autorizado");
                await AbrirBarrera(lane);
            }
            else
            {
                Console.WriteLine("⛔ Vehículo NO autorizado");
            }
        }
    }
}