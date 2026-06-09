using HikvisionApi.Models;
using Microsoft.EntityFrameworkCore;

namespace HikvisionApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }

        // Siempre presentes
        public DbSet<Vehiculo> Vehiculos { get; set; }
        public DbSet<AccesoVehicular> AccesosVehiculares { get; set; }

        // Para modo Parqueadero Local o Portería Local con convenios
        public DbSet<RegistroLocal> Registros { get; set; }
        public DbSet<ConvenioLocal> Convenios { get; set; }
        public DbSet<ConvenioVehiculoLocal> ConveniosVehiculos { get; set; }

        // Patrones de placa y tarifas (compartidos con ParkSky)
        public DbSet<PatronPlacaLocal> PatronPlacas { get; set; }
        public DbSet<TarifaLocal> Tarifas { get; set; }
    }
}