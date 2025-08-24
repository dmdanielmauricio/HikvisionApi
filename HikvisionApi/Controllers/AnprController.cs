using Microsoft.AspNetCore.Mvc;

namespace HikvisionApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AnprController : ControllerBase
    {
        private readonly string _savePath = @"C:\ANPR";

        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No se recibió ninguna imagen.");

            if (!Directory.Exists(_savePath))
            {
                Directory.CreateDirectory(_savePath);
            }

            string filePath = Path.Combine(_savePath, file.FileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            return Ok(new { message = "Imagen guardada correctamente", path = filePath });
        }
    }
}
