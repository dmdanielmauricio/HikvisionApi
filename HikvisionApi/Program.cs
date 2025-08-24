using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using System.Xml.Linq;
using System.Drawing;
using System.Drawing.Imaging;

var builder = WebApplication.CreateBuilder(args);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

string targetFolder = @"C:\ANPR";
string rawFolder = Path.Combine(targetFolder, "raw");
string logFolder = Path.Combine(targetFolder, "logs");

// Método auxiliar para guardar logs
static void WriteLog(string message, string logFolder)
{
    string logFile = Path.Combine(logFolder, "api_log.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
    File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
}

app.MapPost("/anpr", async (HttpRequest request) =>
{
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

            // Guardar todos los archivos en raw
            foreach (var file in form.Files)
            {
                WriteLog($"Archivo: {file.Name} - {file.FileName} ({file.Length} bytes)", logFolder);

                string rawPath = Path.Combine(rawFolder, file.FileName);
                using var stream = new FileStream(rawPath, FileMode.Create);
                await file.CopyToAsync(stream);

                WriteLog($"✅ Guardado en {rawPath}", logFolder);

                if (file.FileName.EndsWith(".xml")) xmlPath = rawPath;
                if (file.FileName.Contains("licensePlatePicture")) licensePlatePicPath = rawPath;
                if (file.FileName.Contains("detectionPicture")) detectionPicPath = rawPath;
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

            // Crear carpetas específicas por cámara
            string camRecortadaFolder = Path.Combine(targetFolder, $"Camara{lane}");
            string camGeneralFolder = Path.Combine(targetFolder, $"CamaraX{lane}");

            Directory.CreateDirectory(camRecortadaFolder);
            Directory.CreateDirectory(camGeneralFolder);

            string finalFileName = $"{absTime}_{placaReconocida}_{lane}.jpg";

            // Imagen recortada con placa
            if (licensePlatePicPath != null && placaReconocida != "DESCONOCIDA")
            {
                string finalFilePath = Path.Combine(camRecortadaFolder, finalFileName);

                using (var bmp = new Bitmap(licensePlatePicPath))
                using (var g = Graphics.FromImage(bmp))
                {
                    var font = new Font("Arial", 20, FontStyle.Bold);
                    var brush = new SolidBrush(Color.Red);

                    g.DrawString(placaReconocida, font, brush, new PointF(5, 5));
                    bmp.Save(finalFilePath, ImageFormat.Jpeg);
                }

                WriteLog($"🖼️ Imagen recortada con placa guardada en {finalFilePath}", logFolder);
            }

            // Imagen general con placa
            if (detectionPicPath != null && placaReconocida != "DESCONOCIDA")
            {
                string generalFilePath = Path.Combine(camGeneralFolder, finalFileName);

                using (var bmp = new Bitmap(detectionPicPath))
                using (var g = Graphics.FromImage(bmp))
                {
                    var font = new Font("Arial", 30, FontStyle.Bold);
                    var brush = new SolidBrush(Color.Red);

                    g.DrawString(placaReconocida, font, brush, new PointF(10, 10));
                    bmp.Save(generalFilePath, ImageFormat.Jpeg);
                }

                WriteLog($"🖼️ Imagen general con placa guardada en {generalFilePath}", logFolder);
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

