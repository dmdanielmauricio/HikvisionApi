using Microsoft.OpenApi.Models;
using System.Xml.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using HikvisionApi.Config;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Configuración fuerte tipada
builder.Services.Configure<AnprSettings>(builder.Configuration.GetSection("AnprSettings"));

// Registrar controladores
builder.Services.AddControllers();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ANPR API",  // 👈 Igual que producción
        Version = "v1"
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "ANPR API v1");
});

// 🔹 Habilitar archivos estáticos (sirve imágenes desde C:\ANPR en /anpr/…)
var settings = app.Services.GetRequiredService<IOptions<AnprSettings>>().Value;
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(settings.TargetFolder),
    RequestPath = "/anpr"
});

// 🔹 Mapear controladores (GET /api/anpr/capturas)
app.MapControllers();

// Método auxiliar para guardar logs
static void WriteLog(string message, string logFolder)
{
    string logFile = Path.Combine(logFolder, "api_log.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
    File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
}

// 🔹 Endpoint principal POST /anpr
app.MapPost("/anpr", async (HttpRequest request, IOptions<AnprSettings> settings) =>
{
    string targetFolder = settings.Value.TargetFolder;
    string rawFolder = Path.Combine(targetFolder, "raw");
    string logFolder = Path.Combine(targetFolder, "logs");

    Directory.CreateDirectory(rawFolder);
    Directory.CreateDirectory(logFolder);

    WriteLog("========= NUEVO REQUEST =========", logFolder);
    WriteLog($"Método: {request.Method}", logFolder);
    WriteLog($"Content-Type: {request.ContentType}", logFolder);

    string? xmlPath = null;
    string? licensePlatePicPath = null;
    string? detectionPicPath = null;
    string placaReconocida = "DESCONOCIDA";
    string absTime = DateTime.Now.ToString("yyyyMMddHHmmssfff");
    string lane = "0";

    try
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();

            foreach (var field in form)
                WriteLog($"Campo: {field.Key} = {field.Value}", logFolder);

            foreach (var file in form.Files)
            {
                WriteLog($"Archivo: {file.Name} - {file.FileName} ({file.Length} bytes)", logFolder);

                string rawPath = Path.Combine(rawFolder, file.FileName);
                using var stream = new FileStream(rawPath, FileMode.Create);
                await file.CopyToAsync(stream);

                WriteLog($"✅ Guardado en {rawPath}", logFolder);

                if (file.Name == "metadata") xmlPath = rawPath;
                if (file.Name == "licensePlatePicture") licensePlatePicPath = rawPath;
                if (file.Name == "detectionPicture") detectionPicPath = rawPath;
            }

            // Leer XML
            if (xmlPath != null)
            {
                var xml = XDocument.Load(xmlPath);
                XNamespace ns = "http://www.isapi.org/ver20/XMLSchema";

                var plateElement = xml.Descendants(ns + "licensePlate").FirstOrDefault();
                if (plateElement != null) placaReconocida = plateElement.Value;

                var absTimeElement = xml.Descendants(ns + "absTime").FirstOrDefault();
                if (absTimeElement != null) absTime = absTimeElement.Value;

                var laneElement = xml.Descendants(ns + "line").FirstOrDefault();
                if (laneElement != null) lane = laneElement.Value;
            }

            // Fecha actual en formato yyyyMMdd
            string fecha = DateTime.Now.ToString("yyyyMMdd");

            // Asegurar que la raíz (C:\ANPR) exista
            Directory.CreateDirectory(targetFolder);

            // Crear estructura base para todas las cámaras (1 a 4 por ejemplo)
            for (int i = 1; i <= 4; i++)
            {
                Directory.CreateDirectory(Path.Combine(targetFolder, $"Camara{i}", fecha));
                Directory.CreateDirectory(Path.Combine(targetFolder, $"Camara{i}X", fecha));
            }

            string finalFileName = $"{absTime}_{placaReconocida}_{lane}.jpg";

            // 🔹 Imagen recortada (placa) → Camara#
            if (licensePlatePicPath != null && placaReconocida != "DESCONOCIDA")
            {
                string finalFilePath = Path.Combine(
                    Path.Combine(targetFolder, $"Camara{lane}", fecha),
                    finalFileName
                );

                using (var bmp = new Bitmap(licensePlatePicPath))
                using (var g = Graphics.FromImage(bmp))
                {
                   
                    bmp.Save(finalFilePath, ImageFormat.Jpeg);
                }

                WriteLog($"🖼️ Imagen RECORTADA guardada en {finalFilePath}", logFolder);
            }

            // 🔹 Imagen general → Camara#X
            if (detectionPicPath != null && placaReconocida != "DESCONOCIDA")
            {
                string generalFilePath = Path.Combine(
                    Path.Combine(targetFolder, $"Camara{lane}X", fecha),
                    finalFileName
                );

                using (var bmp = new Bitmap(detectionPicPath))
                using (var g = Graphics.FromImage(bmp))
                {
                   
                   bmp.Save(generalFilePath, ImageFormat.Jpeg);
                }

                WriteLog($"🖼️ Imagen GENERAL guardada en {generalFilePath}", logFolder);
            }
        }
        else
        {
            WriteLog("⚠️ El request no tiene 'multipart/form-data'", logFolder);
        }
    }
    catch (Exception ex)
    {
        WriteLog("❌ Error: " + ex.ToString(), logFolder);
    }

    return Results.Ok(new { message = "Request procesado", placa = placaReconocida, absTime, lane });
})
.DisableAntiforgery();

app.Run("http://0.0.0.0:5000");
