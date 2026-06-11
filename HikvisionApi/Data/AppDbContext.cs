using HikvisionApi.Models;
using Microsoft.EntityFrameworkCore;

namespace HikvisionApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }

        // Tablas principales de ParqueaderoDB
        public DbSet<Vehiculo> Vehiculos { get; set; }
        public DbSet<AccesoVehicular> AccesosVehiculares { get; set; }
        public DbSet<RegistroLocal> Registros { get; set; }
        public DbSet<ConvenioLocal> ConveniosMensualidad { get; set; }
        public DbSet<ConvenioVehiculoLocal> ConveniosVehiculos { get; set; }
        public DbSet<PatronPlacaLocal> PatronPlacas { get; set; }
        public DbSet<TarifaLocal> Tarifas { get; set; }
        public DbSet<VehiculoRestringidoLocal> VehiculosRestringidos { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Mapear nombres de tabla exactos de ParkSky
            modelBuilder.Entity<RegistroLocal>().ToTable("Registros");
            modelBuilder.Entity<ConvenioLocal>().ToTable("ConveniosMensualidad");
            modelBuilder.Entity<ConvenioVehiculoLocal>().ToTable("ConveniosVehiculos");
            modelBuilder.Entity<VehiculoRestringidoLocal>().ToTable("VehiculosRestringidos");
            modelBuilder.Entity<PatronPlacaLocal>().ToTable("PatronPlacas");
            modelBuilder.Entity<TarifaLocal>().ToTable("Tarifas");
        }
    }
}