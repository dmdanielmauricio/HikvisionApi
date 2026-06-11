using HikvisionApi.Models;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Drawing.Printing;
using System.Net.Http;
using System.Text.Json;

namespace HikvisionApi.Services
{
    public class PrintService
    {
        private readonly ILogger<PrintService> _logger;
        private readonly IHttpClientFactory _httpFactory;

        public PrintService(
            ILogger<PrintService> logger,
            IHttpClientFactory httpFactory)
        {
            _logger = logger;
            _httpFactory = httpFactory;
        }

        public static string ObtenerImpresora(string lane, Microsoft.Extensions.Configuration.IConfiguration config)
        {
            return lane switch
            {
                "1" => config["Impresoras:Entrada1"] ?? "",
                "2" => config["Impresoras:Salida1"] ?? "",
                "3" => config["Impresoras:Entrada2"] ?? "",
                "4" => config["Impresoras:Salida2"] ?? "",
                _ => config["Impresoras:Entrada1"] ?? ""
            };
        }

        // =============================================
        // Imprime desde datos del ticket de ParkSky
        // =============================================
        public void ImprimirDesdeTicket(string impresora, TicketResponse ticket)
        {
            ImprimirTiquete(
                impresora: impresora,
                nombreParqueadero: ticket.NombreParqueadero ?? "PARQUEADERO",
                nit: ticket.Nit ?? "",
                direccion: ticket.Direccion ?? "",
                telefono: ticket.Telefono ?? "",
                mensajeEncabezado: ticket.MensajeEncabezado ?? "",
                mensajePie: ticket.MensajePie ?? "",
                mensajeObservacion: ticket.MensajeObservacion ?? "",
                placa: ticket.Placa ?? "",
                tipoVehiculo: ticket.TipoVehiculo ?? "Vehículo",
                fechaEntrada: DateTime.TryParse(ticket.FechaEntrada, out var dt) ? dt : DateTime.Now,
                tarifa: ticket.Tarifa ?? "Normal",
                esMensualidad: ticket.EsMensualidad,
                nombreConvenio: ticket.NombreConvenio,
                vigenciaFin: ticket.VigenciaFin,
                qrToken: ticket.QrToken
            );
        }

        // =============================================
        // Fallback local (sin datos de ParkSky)
        // =============================================
        public void ImprimirTiqueteLocal(
            string impresora, string placa, string tipoVehiculo,
            DateTime fechaEntrada, string carril, bool esMensualidad,
            string? nombreConvenio = null)
        {
            ImprimirTiquete(
                impresora: impresora,
                nombreParqueadero: "PARQUEADERO",
                nit: "",
                direccion: "",
                telefono: "",
                mensajeEncabezado: "",
                mensajePie: "Conserve este tiquete para su salida",
                mensajeObservacion: "",
                placa: placa,
                tipoVehiculo: tipoVehiculo,
                fechaEntrada: fechaEntrada,
                tarifa: "Normal",
                esMensualidad: esMensualidad,
                nombreConvenio: nombreConvenio,
                vigenciaFin: null,
                qrToken: null
            );
        }

        // =============================================
        // IMPRESIÓN PRINCIPAL — replica TicketEntrada.cshtml
        // Ancho ticket térmico: 80mm ≈ 302px a 96dpi
        // =============================================
        private void ImprimirTiquete(
            string impresora,
            string nombreParqueadero,
            string nit,
            string direccion,
            string telefono,
            string mensajeEncabezado,
            string mensajePie,
            string mensajeObservacion,
            string placa,
            string tipoVehiculo,
            DateTime fechaEntrada,
            string tarifa,
            bool esMensualidad,
            string? nombreConvenio,
            string? vigenciaFin,
            string? qrToken)
        {
            try
            {
                var doc = new PrintDocument();
                doc.PrinterSettings.PrinterName = impresora;

                if (!doc.PrinterSettings.IsValid)
                {
                    _logger.LogWarning("Impresora '{Imp}' no encontrada. Disponibles: {Lista}",
                        impresora,
                        string.Join(", ", PrinterSettings.InstalledPrinters.Cast<string>()));
                    return;
                }

                // Papel térmico 80mm
                doc.DefaultPageSettings.PaperSize = new PaperSize("Ticket", 283, 9999);
                doc.DefaultPageSettings.Margins = new Margins(10, 10, 8, 8);

                // QR bitmap (si hay token)
                Bitmap? qrBitmap = null;
                if (!string.IsNullOrEmpty(qrToken))
                {
                    try { qrBitmap = GenerarQR(qrToken, 150); } catch { }
                }

                doc.PrintPage += (_, e) =>
                {
                    var g = e.Graphics!;
                    float y = 6f;
                    float w = 263f; // ancho útil (283 - 20 márgenes)
                    float x = 10f;

                    var sfC = new StringFormat { Alignment = StringAlignment.Center };
                    var sfR = new StringFormat { Alignment = StringAlignment.Far };
                    var sfL = new StringFormat { Alignment = StringAlignment.Near };
                    var negro = Brushes.Black;
                    var gris = new SolidBrush(Color.FromArgb(68, 68, 68));
                    var grisC = new SolidBrush(Color.FromArgb(153, 153, 153));

                    // Fuentes — replica Inter + JetBrains Mono
                    // (usa Arial/Consolas disponibles en Windows)
                    var fNomPark = new Font("Arial", 14f, FontStyle.Bold);
                    var fInfo = new Font("Arial", 8.5f, FontStyle.Bold);
                    var fBadge = new Font("Arial", 8f, FontStyle.Bold);
                    var fLabel = new Font("Arial", 8.5f, FontStyle.Regular);
                    var fValue = new Font("Arial", 8.5f, FontStyle.Bold);
                    var fPlaca = new Font("Consolas", 24f, FontStyle.Bold);
                    var fPlacaLbl = new Font("Arial", 7f, FontStyle.Bold);
                    var fMens = new Font("Arial", 8f, FontStyle.Bold);
                    var fFooter = new Font("Arial", 11f, FontStyle.Bold);
                    var fTag = new Font("Arial", 6.5f, FontStyle.Bold);

                    // ── MENSAJE ENCABEZADO ──
                    if (!string.IsNullOrEmpty(mensajeEncabezado))
                    {
                        g.DrawString(mensajeEncabezado, fInfo, gris,
                            new RectangleF(x, y, w, 14), sfC); y += 14;
                    }

                    // ── NOMBRE PARQUEADERO ──
                    g.DrawString(nombreParqueadero.ToUpper(), fNomPark, negro,
                        new RectangleF(x, y, w, 22), sfC); y += 22;

                    if (!string.IsNullOrEmpty(nit))
                    {
                        g.DrawString($"NIT: {nit}", fInfo, negro,
                            new RectangleF(x, y, w, 13), sfC); y += 13;
                    }
                    if (!string.IsNullOrEmpty(direccion))
                    {
                        g.DrawString(direccion, fInfo, negro,
                            new RectangleF(x, y, w, 13), sfC); y += 13;
                    }
                    if (!string.IsNullOrEmpty(telefono))
                    {
                        g.DrawString($"TEL: {telefono}", fInfo, negro,
                            new RectangleF(x, y, w, 13), sfC); y += 13;
                    }

                    // ── BADGE "TICKET DE ENTRADA" ──
                    y += 6;
                    var badgeW = 140f; var badgeH = 18f;
                    var badgeX = x + (w - badgeW) / 2;
                    g.FillRectangle(Brushes.Black,
                        new RectangleF(badgeX, y, badgeW, badgeH));
                    g.DrawString("TICKET DE ENTRADA", fBadge, Brushes.White,
                        new RectangleF(badgeX, y + 2, badgeW, badgeH - 2), sfC);
                    y += badgeH + 8;

                    // ── DIVIDER ──
                    g.DrawLine(Pens.Black, x, y, x + w, y); y += 8;

                    // ── PLACA BOX (fondo gris claro) ──
                    var plBoxH = 56f;
                    g.FillRectangle(new SolidBrush(Color.FromArgb(240, 240, 240)),
                        new RectangleF(x, y, w, plBoxH));
                    g.DrawString("Vehículo Placa", fPlacaLbl,
                        new SolidBrush(Color.FromArgb(85, 85, 85)),
                        new RectangleF(x, y + 6, w, 12), sfC);
                    g.DrawString(placa, fPlaca, negro,
                        new RectangleF(x, y + 18, w, 34), sfC);
                    y += plBoxH + 8;

                    // ── DETALLES (líneas con separador) ──
                    void Detalle(string label, string value)
                    {
                        g.DrawString(label, fLabel, new SolidBrush(Color.FromArgb(51, 51, 51)), x + 2, y);
                        g.DrawString(value, fValue, negro,
                            new RectangleF(x, y, w - 2, 14), sfR);
                        y += 14;
                        g.DrawLine(new Pen(Color.FromArgb(238, 238, 238)), x, y, x + w, y);
                        y += 1;
                    }

                    Detalle("Fecha", fechaEntrada.ToString("dd/MM/yyyy"));
                    Detalle("Hora Ingreso", fechaEntrada.ToString("hh:mm tt"));
                    Detalle("Tipo Vehículo", tipoVehiculo);
                    Detalle("Tarifa", tarifa);
                    y += 4;

                    // ── CONVENIO (si aplica) ──
                    if (esMensualidad && !string.IsNullOrEmpty(nombreConvenio))
                    {
                        var boxH = 56f;
                        g.FillRectangle(new SolidBrush(Color.FromArgb(236, 253, 243)),
                            new RectangleF(x, y, w, boxH));
                        g.DrawRectangle(new Pen(Color.FromArgb(110, 231, 183), 1.5f),
                            x, y, w, boxH);
                        g.DrawString("CONVENIO MENSUAL ACTIVO", fMens,
                            new SolidBrush(Color.FromArgb(2, 122, 72)),
                            new RectangleF(x, y + 5, w, 14), sfC);

                        var diasRestantes = vigenciaFin != null &&
                            DateTime.TryParse(vigenciaFin, out var vf)
                            ? (vf.Date - DateTime.Now.Date).Days : 0;

                        g.DrawString("Convenio", fLabel,
                            new SolidBrush(Color.FromArgb(6, 95, 70)), x + 6, y + 22);
                        g.DrawString(nombreConvenio, fValue,
                            new SolidBrush(Color.FromArgb(6, 95, 70)),
                            new RectangleF(x, y + 22, w - 6, 14), sfR);

                        g.DrawString("Vigencia", fLabel,
                            new SolidBrush(Color.FromArgb(6, 95, 70)), x + 6, y + 38);
                        g.DrawString($"{diasRestantes} día(s)", fValue,
                            new SolidBrush(Color.FromArgb(6, 95, 70)),
                            new RectangleF(x, y + 38, w - 6, 14), sfR);

                        y += boxH + 8;
                    }

                    // ── QR ──
                    if (qrBitmap != null)
                    {
                        float qrSize = 110f;
                        float qrX = x + (w - qrSize) / 2;
                        g.DrawImage(qrBitmap, qrX, y, qrSize, qrSize);
                        y += qrSize + 4;
                        g.DrawString("Escanear para cobro rápido", fPlacaLbl,
                            new SolidBrush(Color.FromArgb(68, 68, 68)),
                            new RectangleF(x, y, w, 12), sfC);
                        y += 14;
                    }

                    // ── DIVIDER ──
                    g.DrawLine(Pens.Black, x, y, x + w, y); y += 8;

                    // ── OBSERVACIONES ──
                    if (!string.IsNullOrEmpty(mensajeObservacion))
                    {
                        g.DrawString(mensajeObservacion, fInfo, gris,
                            new RectangleF(x, y, w, 28), sfC); y += 30;
                    }

                    // ── FOOTER ──
                    g.DrawString("¡BIENVENIDO!", fFooter, negro,
                        new RectangleF(x, y, w, 20), sfC); y += 22;

                    if (!string.IsNullOrEmpty(mensajePie))
                    {
                        g.DrawString(mensajePie, fInfo, gris,
                            new RectangleF(x, y, w, 14), sfC); y += 16;
                    }

                    g.DrawString("POWERED BY SYSPARKING® CLOUD", fTag, grisC,
                        new RectangleF(x, y, w, 11), sfC);
                };

                doc.Print();
                _logger.LogInformation("✅ Ticket impreso: {Placa} en {Imp}", placa, impresora);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error imprimiendo ticket para {Placa}", placa);
            }
        }

        // =============================================
        // GENERADOR QR — usa QRCoder
        // =============================================
        private static Bitmap GenerarQR(string contenido, int pixeles)
        {
            // QRCoder nuget: Install-Package QRCoder
            var qrGenerator = new QRCoder.QRCodeGenerator();
            var qrData = qrGenerator.CreateQrCode(contenido,
                QRCoder.QRCodeGenerator.ECCLevel.M);
            var qrCode = new QRCoder.QRCode(qrData);
            return qrCode.GetGraphic(pixeles / 21);
        }
    }
}