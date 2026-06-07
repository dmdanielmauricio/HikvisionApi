using HikvisionApi.Config;
using HikvisionApi.Data;
using HikvisionApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// =============================================
// BASE DE DATOS LOCAL
// =============================================
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(opts =>
{
    if (!string.IsNullOrWhiteSpace(connStr))
        opts.UseSqlServer(connStr);
    else
        opts.UseSqlServer(
            "Server=localhost;Database=PorteriaDB_Empty;Trusted_Connection=True;TrustServerCertificate=True;");
});

// =============================================
// CONFIGURACIONES
// =============================================
builder.Services.Configure<AnprSettings>(
    builder.Configuration.GetSection("AnprSettings"));
builder.Services.Configure<BarrierSettings>(
    builder.Configuration.GetSection("BarrierSettings"));
builder.Services.Configure<ParkSkySettings>(
    builder.Configuration.GetSection("ParkSkySettings"));
builder.Services.Configure<PorteriaSettings>(
    builder.Configuration.GetSection("Porteria"));
builder.Services.Configure<ParqueaderoSettings>(
    builder.Configuration.GetSection("Parqueadero"));
builder.Services.Configure<ImpresorasSettings>(
    builder.Configuration.GetSection("Impresoras"));

// =============================================
// SERVICIOS
// =============================================
builder.Services.AddHttpClient<ParkSkyClient>();
builder.Services.AddScoped<HikvisionService>();
builder.Services.AddSingleton<PrintService>();
builder.Services.AddControllers();

builder.WebHost.ConfigureKestrel(opts =>
    opts.Limits.MaxRequestBodySize = 50 * 1024 * 1024);

var app = builder.Build();

var anprFolder = builder.Configuration["AnprSettings:TargetFolder"] ?? "D:\\ANPR";
if (Directory.Exists(anprFolder))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(anprFolder),
        RequestPath = "/anpr"
    });
}

app.UseRouting();
app.MapControllers();
app.Run();
