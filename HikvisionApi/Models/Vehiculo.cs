namespace HikvisionApi.Models
{
    public class Vehiculo
    {
        public int Id { get; set; }
        public string Placa { get; set; } = "";
        public string Tipo { get; set; } = "Carro";
        public int PropietarioId { get; set; }
        public int? NroTicket { get; set; }
        public bool EsPrivado { get; set; }
        public string? NumeroCelda { get; set; }
        public bool Activo { get; set; }
    }
}