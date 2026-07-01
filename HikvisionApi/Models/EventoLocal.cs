namespace HikvisionApi.Models
{
    // Registro local de cada movimiento vehicular.
    // Se inserta en el momento del acceso (dentro de EncolarEvento, background)
    // y se sincroniza al VPS mediante SyncBackgroundService cada 5 segundos.
    // La barrera abre sin esperar que este registro exista — EncolarEvento es
    // fire-and-forget con scope propio para evitar DbContext disposed.
    public class EventoLocal
    {
        public int Id { get; set; }
        public string Placa { get; set; } = "";
        public string Carril { get; set; } = "";
        public string? CarrilNombre { get; set; }

        // ENTRADA | SALIDA
        public string TipoMovimiento { get; set; } = "";
        public bool Autorizado { get; set; }
        public string Motivo { get; set; } = "";
        public string? TipoVehiculo { get; set; }
        public bool EsMensualidad { get; set; }
        public int? ConvenioId { get; set; }

        // URL de la foto en el VPS — se resuelve en background junto con
        // la subida de imagen. EncolarEvento espera este valor antes de
        // insertar, para que SyncBackgroundService lo incluya en la llamada
        // al VPS y AccesosHikvision quede con la foto correcta.
        public string? ImagenUrl { get; set; }

        // Token QR generado localmente al entrar — mismo algoritmo que VPS.
        // SyncBackgroundService lo envía a procesar-entrada para que el
        // RegistroParqueo del VPS quede con el mismo token que el tiquete.
        public string? QrToken { get; set; }

        public DateTime FechaHora { get; set; } = DateTime.Now;

        // Control de sincronización con VPS
        public bool Sincronizado { get; set; } = false;
        public DateTime? FechaSincronizado { get; set; }
        public int? RegistroVpsId { get; set; }
        public string? ErrorSincronizacion { get; set; }
        public int IntentosSincronizacion { get; set; } = 0;
    }
}