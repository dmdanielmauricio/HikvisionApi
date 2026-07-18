namespace HikvisionApi.Models
{
    /// Mapeado a la tabla Configuraciones de ParqueaderoDB — la misma tabla
    /// que lee/escribe ParqueaderoApp.Models.Configuracion en el VPS. Solo
    /// se incluyen los campos que realmente se usan al imprimir el tiquete
    /// localmente; EF Core ignora sin problema las demás columnas de la
    /// tabla real que no están mapeadas aquí (logo, QR, cobro simple, etc.
    /// no hacen falta para esto).
    public class ConfiguracionLocal
    {
        public int Id { get; set; }
        public string? NombreParqueadero { get; set; }
        public string? Nit { get; set; }
        public string? Direccion { get; set; }
        public string? Telefono { get; set; }
        public string? MensajeEncabezado { get; set; }
        public string? MensajeObservacion { get; set; }
        public string? MensajePie { get; set; }
    }
}