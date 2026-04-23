using HikvisionApi.Models;
using Microsoft.EntityFrameworkCore;


namespace HikvisionApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }

        public DbSet<Vehiculo> Vehiculos { get; set; }
        public DbSet<AccesoVehicular> AccesosVehiculares { get; set; }
    }
}