using System;
using System.Collections.Generic;

namespace HikvisionApi.Models
{
    /// Mapeado a la tabla Registros de ParqueaderoDB
    public class RegistroLocal
    {
        public int Id { get; set; }
        public int VehiculoId { get; set; }
        public Vehiculo Vehiculo { get; set; }  // mismo modelo Vehiculo existente
        public int TarifaId { get; set; }
        public DateTime FechaEntrada { get; set; }
        public DateTime? FechaSalida { get; set; }
        public decimal? ValorPagado { get; set; }
        public bool Activo { get; set; } = true;
        public string? MetodoPago { get; set; }
        public bool EsMensualidad { get; set; }
        public int? ConvenioMensualidadId { get; set; }
        public string? QrToken { get; set; }
    }

    public class ConvenioLocal
    {
        public int Id { get; set; }
        public string NombreConvenio { get; set; } = "";
        public DateTime FechaInicio { get; set; }
        public DateTime FechaFin { get; set; }
        public bool PagoPendiente { get; set; }
        public virtual ICollection<ConvenioVehiculoLocal> Vehiculos { get; set; }
            = new List<ConvenioVehiculoLocal>();
    }

    public class ConvenioVehiculoLocal
    {
        public int Id { get; set; }
        public int ConvenioMensualidadId { get; set; }
        public ConvenioLocal ConvenioMensualidad { get; set; }
        public string Placa { get; set; } = "";
        public bool Activo { get; set; }
        public int? ConvenioId { get; set; }
    }
}