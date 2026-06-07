namespace HikvisionApi.Models
{
    /// <summary>
    /// Registro de parqueo LOCAL (solo para modo FuenteDatos=Local)
    /// Cuando FuenteDatos=Nube, el registro vive en ParkSky
    /// </summary>
    public class RegistroLocal
    {
        public int Id { get; set; }
        public string Placa { get; set; } = "";
        public string TipoVehiculo { get; set; } = "Carro";
        public DateTime FechaEntrada { get; set; } = DateTime.Now;
        public DateTime? FechaSalida { get; set; }
        public bool Activo { get; set; } = true;
        public decimal? ValorPagado { get; set; }
        public bool EsMensualidad { get; set; }
        public int? ConvenioId { get; set; }
        public string? Carril { get; set; }
    }

    public class ConvenioLocal
    {
        public int Id { get; set; }
        public string NombreConvenio { get; set; } = "";
        public bool Activo { get; set; }
        public DateTime FechaInicio { get; set; }
        public DateTime FechaFin { get; set; }

        public virtual ICollection<ConvenioVehiculoLocal> Vehiculos { get; set; }
            = new List<ConvenioVehiculoLocal>();
    }

    public class ConvenioVehiculoLocal
    {
        public int Id { get; set; }
        public int ConvenioId { get; set; }
        public int ConvenioMensualidadId { get; set; } // alias de ConvenioId para compatibilidad
        public string Placa { get; set; } = "";
        public bool Activo { get; set; }

        public virtual ConvenioLocal ConvenioMensualidad { get; set; } = null!;
    }
}