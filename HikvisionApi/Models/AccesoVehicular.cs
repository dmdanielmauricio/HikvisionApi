using System;

namespace HikvisionApi.Models
{
    public class AccesoVehicular
    {
        public int Id { get; set; }
        public string Placa { get; set; }
        public string Carril { get; set; }
        public DateTime FechaHora { get; set; }
        public bool Autorizado { get; set; }
        public string TipoMovimiento { get; set; }
        public string Motivo { get; set; }
        public string ImagenUrl { get; set; }
    }
}