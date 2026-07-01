namespace HikvisionApi.Models
{
    // Registro de pago confirmado, enviado desde el VPS al momento del cobro.
    // SalidaParqueaderoNube consulta esta tabla (AnyAsync local ~20ms)
    // en vez de llamar al VPS (salida-rapida ~2-5s con latencia de red).
    //
    // El VPS (ControlController.Cobrar) llama POST /api/print/confirmar-pago
    // después de confirmar el pago — eso crea este registro.
    // FechaExpira = FechaPago + TiempoGraciaMinutos + 5min de margen.
    public class PagoConfirmado
    {
        public int Id { get; set; }
        public string Placa { get; set; } = "";
        public int? RegistroVpsId { get; set; }
        public decimal ValorPagado { get; set; }
        public DateTime FechaPago { get; set; }

        // La salida hace AnyAsync(p.FechaExpira >= DateTime.Now)
        // Incluye gracia + 5min de margen para evitar falsos negativos
        public DateTime FechaExpira { get; set; }

        // QrToken generado en el VPS — incluido para referencia,
        // la K2600 lo recibe por la llamada /api/print/registrar-qr
        public string? QrToken { get; set; }

        // JSON serializado de TicketResponse — para impresión sin llamar al VPS.
        // Poblado por el webhook confirmar-pago del VPS (Fase 3).
        public string? DatosTiqueteJson { get; set; }
    }
}