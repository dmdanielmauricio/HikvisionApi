namespace HikvisionApi.Models
{
    /// Mapeado a la tabla PatronPlacas de la BD compartida con ParkSky
    public class PatronPlacaLocal
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = ""; // "Carro", "Moto"
        public string Patron { get; set; } = ""; // "AAA###", "AAA##A"
        public int TarifaId { get; set; }
        public TarifaLocal? Tarifa { get; set; }
        public bool Activo { get; set; }
    }

    public class TarifaLocal
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string TipoVehiculo { get; set; } = "Carro";
        public bool Activa { get; set; }
    }
}

namespace HikvisionApi.Models
{
    /// Mapeado a VehiculosRestringidos de la BD compartida con ParkSky
    public class VehiculoRestringidoLocal
    {
        public int Id { get; set; }
        public string Placa { get; set; } = "";
        public bool Activo { get; set; }
    }
}