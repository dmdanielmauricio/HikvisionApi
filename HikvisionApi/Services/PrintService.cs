using System.Drawing;
using System.Drawing.Printing;

namespace HikvisionApi.Services
{
    public class PrintService
    {
        private readonly ILogger<PrintService> _logger;

        public PrintService(ILogger<PrintService> logger)
        {
            _logger = logger;
        }

        // =============================================
        // IMPRIMIR CON DATOS DE PARKSKY (Opción A)
        // =============================================
        public void ImprimirDesdeTicket(string impresora, TicketResponse ticket)
        {
            ImprimirTiqueteIngreso(
                impresora: impresora,
                nombreParqueadero: ticket.NombreParqueadero ?? "PARQUEADERO",
                nit: ticket.Nit ?? "",
                direccion: ticket.Direccion ?? "",
                telefono: ticket.Telefono ?? "",
                placa: ticket.Placa ?? "",
                tipoVehiculo: ticket.TipoVehiculo ?? "Vehículo",
                fechaEntrada: DateTime.TryParse(ticket.FechaEntrada, out var dt) ? dt : DateTime.Now,
                carril: "",
                esMensualidad: ticket.EsMensualidad,
                nombreConvenio: ticket.NombreConvenio,
                vigenciaFin: ticket.VigenciaFin,
                tarifa: ticket.Tarifa
            );
        }

        // =============================================
        // FALLBACK LOCAL (si ParkSky no responde)
        // =============================================
        public void ImprimirTiqueteLocal(
            string impresora,
            string placa,
            string tipoVehiculo,
            DateTime fechaEntrada,
            string carril,
            bool esMensualidad,
            string? nombreConvenio = null)
        {
            ImprimirTiqueteIngreso(
                impresora: impresora,
                nombreParqueadero: "PARQUEADERO",
                nit: "",
                direccion: "",
                telefono: "",
                placa: placa,
                tipoVehiculo: tipoVehiculo,
                fechaEntrada: fechaEntrada,
                carril: carril,
                esMensualidad: esMensualidad,
                nombreConvenio: nombreConvenio,
                vigenciaFin: null,
                tarifa: null
            );
        }

        // =============================================
        // IMPRESIÓN REAL (PrintDocument)
        // =============================================
        internal void ImprimirTiqueteIngreso(
            string impresora,
            string nombreParqueadero,
            string nit,
            string direccion,
            string telefono,
            string placa,
            string tipoVehiculo,
            DateTime fechaEntrada,
            string carril,
            bool esMensualidad,
            string? nombreConvenio,
            string? vigenciaFin,
            string? tarifa)
        {
            try
            {
                var doc = new PrintDocument();
                doc.PrinterSettings.PrinterName = impresora;

                if (!doc.PrinterSettings.IsValid)
                {
                    _logger.LogWarning(
                        "Impresora '{Imp}' no encontrada. Disponibles: {Lista}",
                        impresora,
                        string.Join(", ", PrinterSettings.InstalledPrinters.Cast<string>()));
                    return;
                }

                doc.PrintPage += (_, e) =>
                {
                    var g = e.Graphics!;
                    float y = 8f;
                    float ancho = e.PageBounds.Width - 20;
                    var centro = new StringFormat { Alignment = StringAlignment.Center };
                    var negro = Brushes.Black;

                    var fTitulo = new Font("Courier New", 11, FontStyle.Bold);
                    var fGrande = new Font("Courier New", 20, FontStyle.Bold);
                    var fNormal = new Font("Courier New", 9);
                    var fBold = new Font("Courier New", 9, FontStyle.Bold);
                    var fPeq = new Font("Courier New", 8);

                    // ── ENCABEZADO ──
                    g.DrawString(nombreParqueadero, fTitulo, negro,
                        new RectangleF(10, y, ancho, 20), centro); y += 20;

                    if (!string.IsNullOrEmpty(nit))
                    {
                        g.DrawString($"NIT: {nit}", fPeq, negro,
                            new RectangleF(10, y, ancho, 14), centro); y += 14;
                    }
                    if (!string.IsNullOrEmpty(direccion))
                    {
                        g.DrawString(direccion, fPeq, negro,
                            new RectangleF(10, y, ancho, 14), centro); y += 14;
                    }
                    if (!string.IsNullOrEmpty(telefono))
                    {
                        g.DrawString($"TEL: {telefono}", fPeq, negro,
                            new RectangleF(10, y, ancho, 14), centro); y += 14;
                    }

                    y += 4;
                    g.DrawString(new string('-', 32), fPeq, negro,
                        new RectangleF(10, y, ancho, 12), centro); y += 14;

                    // ── TIPO DE TICKET ──
                    string tipoLabel = esMensualidad ? "MENSUALIDAD" : "TIQUETE DE INGRESO";
                    g.DrawString(tipoLabel, fBold, negro,
                        new RectangleF(10, y, ancho, 16), centro); y += 18;

                    g.DrawString(new string('-', 32), fPeq, negro,
                        new RectangleF(10, y, ancho, 12), centro); y += 14;

                    // ── PLACA ──
                    g.DrawString(placa, fGrande, negro,
                        new RectangleF(10, y, ancho, 32), centro); y += 36;

                    g.DrawString(new string('-', 32), fPeq, negro,
                        new RectangleF(10, y, ancho, 12), centro); y += 14;

                    // ── DATOS ──
                    g.DrawString($"Tipo   : {tipoVehiculo}", fNormal, negro, 10, y); y += 15;
                    g.DrawString($"Fecha  : {fechaEntrada:dd/MM/yyyy}", fNormal, negro, 10, y); y += 15;
                    g.DrawString($"Hora   : {fechaEntrada:HH:mm:ss}", fNormal, negro, 10, y); y += 15;

                    if (!string.IsNullOrEmpty(carril))
                    {
                        g.DrawString($"Carril : {carril}", fNormal, negro, 10, y); y += 15;
                    }
                    if (!string.IsNullOrEmpty(tarifa) && !esMensualidad)
                    {
                        g.DrawString($"Tarifa : {tarifa}", fNormal, negro, 10, y); y += 15;
                    }

                    // ── CONVENIO ──
                    if (esMensualidad && !string.IsNullOrEmpty(nombreConvenio))
                    {
                        y += 4;
                        g.DrawString(new string('-', 32), fPeq, negro,
                            new RectangleF(10, y, ancho, 12), centro); y += 14;
                        g.DrawString($"Convenio: {nombreConvenio}", fBold, negro, 10, y); y += 15;
                        if (!string.IsNullOrEmpty(vigenciaFin))
                        {
                            g.DrawString($"Vigencia: {vigenciaFin}", fNormal, negro, 10, y); y += 15;
                        }
                    }

                    y += 6;
                    g.DrawString(new string('-', 32), fPeq, negro,
                        new RectangleF(10, y, ancho, 12), centro); y += 14;

                    if (!esMensualidad)
                    {
                        g.DrawString("Conserve este tiquete", fPeq, negro,
                            new RectangleF(10, y, ancho, 14), centro); y += 13;
                        g.DrawString("para su salida", fPeq, negro,
                            new RectangleF(10, y, ancho, 14), centro); y += 16;
                    }

                    g.DrawString("PARKSKY® CLOUD SYSTEM", fPeq, negro,
                        new RectangleF(10, y, ancho, 14), centro); y += 13;
                    g.DrawString(DateTime.Now.ToString("dd/MM/yy HH:mm"), fPeq, negro,
                        new RectangleF(10, y, ancho, 14), centro);
                };

                doc.Print();
                _logger.LogInformation("✅ Ticket impreso: {Placa} en {Imp}", placa, impresora);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error imprimiendo ticket {Placa}", placa);
            }
        }

        public static string ObtenerImpresora(string carril, IConfiguration config)
        {
            return carril switch
            {
                "1" => config["Impresoras:Entrada1"] ?? "",
                "3" => config["Impresoras:Entrada2"] ?? "",
                _ => config["Impresoras:Entrada1"] ?? ""
            };
        }
    }
}
