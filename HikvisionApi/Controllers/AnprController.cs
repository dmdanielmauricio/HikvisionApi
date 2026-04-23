using HikvisionApi.Config;
using HikvisionApi.Models;
using HikvisionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text;
using System.Xml.Linq;

namespace HikvisionApi.Controllers
{
    [ApiController]
    [Route("api/anpr")]
    public class AnprController : ControllerBase
    {
        private readonly string _savePath;
        private readonly HikvisionService _hikvisionService;

        public AnprController(
            IOptions<AnprSettings> settings,
            HikvisionService hikvisionService)
        {
            _savePath = settings.Value.TargetFolder;
            _hikvisionService = hikvisionService;
        }

        [HttpPost]
        public async Task<IActionResult> RecibirAnpr()
        {
            string placa = "DESCONOCIDA";
            string lane = "0";
            string absTime = DateTime.Now.ToString("yyyyMMddHHmmssfff");

            IFormFile? plateImage = null;
            IFormFile? fullImage = null;

            if (!Request.HasFormContentType)
                return BadRequest("No es multipart/form-data");

            // ✅ LEER FORM UNA SOLA VEZ
            var form = await Request.ReadFormAsync();

            // ✅ GUARDAR RAW + LOG (YA NO usa Request)
            await _hikvisionService.GuardarRaw(
                form,
                Request.ContentType ?? "",
                Request.Method
            );

            // 🔍 PROCESAR ARCHIVOS
            foreach (var file in form.Files)
            {
                var fileName = file.FileName.ToLower();

                // 📸 identificar imágenes
                if (fileName.Contains("licenseplate"))
                {
                    plateImage = file;
                }
                else if (fileName.Contains("detection"))
                {
                    fullImage = file;
                }
                // 📄 XML metadata
                else if (fileName.Contains(".xml") || file.Name == "metadata")
                {
                    using var stream = file.OpenReadStream();
                    var xml = XDocument.Load(stream);

                    XNamespace ns = "http://www.isapi.org/ver20/XMLSchema";

                    placa = xml.Descendants(ns + "licensePlate")
                               .FirstOrDefault()?.Value ?? "DESCONOCIDA";

                    lane = xml.Descendants(ns + "line")
                              .FirstOrDefault()?.Value ?? "0";

                    absTime = xml.Descendants(ns + "absTime")
                                 .FirstOrDefault()?.Value
                                 ?? DateTime.Now.ToString("yyyyMMddHHmmssfff");
                }
            }

            // 🧠 NORMALIZAR
            placa = placa.ToUpper().Trim();
            lane = lane.Trim();

            Console.WriteLine($"📥 {placa} - carril {lane} - {absTime}");

            // 🔥 PROCESAR (guardar imágenes + BD + abrir barrera)
            await _hikvisionService.ProcesarAcceso(
                placa,
                lane,
                absTime,
                plateImage,
                fullImage
            );

            return Ok(new { placa, lane });
        }


        [HttpGet("capturas")]
        public IActionResult GetCapturas()
        {
            if (!Directory.Exists(_savePath))
                return Ok(new List<CapturaDto>());

            var files = Directory.GetFiles(_savePath, "*.jpg", SearchOption.AllDirectories);

            var capturas = files.Select(file =>
            {
                var parts = Path.GetFileNameWithoutExtension(file).Split('_');
                if (parts.Length < 3) return null;

                return new CapturaDto
                {
                    AbsTime = parts[0],
                    Placa = parts[1],
                    Lane = parts[2],
                    ImageUrl = "/anpr" + file.Replace(_savePath, "").Replace("\\", "/")
                };
            })
            .Where(x => x != null)
            .OrderByDescending(x => x!.AbsTime)
            .ToList();

            return Ok(capturas);
        }
    }
}