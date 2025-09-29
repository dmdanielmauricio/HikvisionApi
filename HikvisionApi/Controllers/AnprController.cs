using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HikvisionApi.Config;
using HikvisionApi.Models;

namespace HikvisionApi.Controllers
{
    [ApiController]
    [Route("api/anpr")]
    [Tags("HikvisionApi")] // 👈 Forzamos que Swagger use el mismo tag que el POST /anpr
    public class AnprController : ControllerBase
    {
        private readonly string _savePath;

        public AnprController(IOptions<AnprSettings> settings)
        {
            _savePath = settings.Value.TargetFolder;
        }

        // 🔹 GET /api/anpr/capturas
        [HttpGet("capturas")]
        public IActionResult GetCapturas()
        {
            if (!Directory.Exists(_savePath))
                return Ok(new List<CapturaDto>());

            var files = Directory.GetFiles(_savePath, "*.jpg", SearchOption.AllDirectories);

            var capturas = files
                .Select(file =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    // Ejemplo: 20250925165706970_KGB015_1
                    var parts = fileName.Split('_');
                    if (parts.Length < 3) return null;

                    string absTime = parts[0];
                    string placa = parts[1];
                    string lane = parts[2];

                    // Convertir ruta absoluta en URL relativa (/anpr/...)
                    string relativePath = file.Replace(_savePath, "").Replace("\\", "/");

                    return new CapturaDto
                    {
                        AbsTime = absTime,
                        Placa = placa,
                        Lane = lane,
                        ImageUrl = "/anpr" + relativePath
                    };
                })
                .Where(x => x != null)
                .OrderByDescending(x => x!.AbsTime)
                .ToList();

            return Ok(capturas);
        }
    }
}

