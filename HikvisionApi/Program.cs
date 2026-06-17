using HikvisionApi.Config;
using HikvisionApi.Data;
using HikvisionApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// =============================================
// CORS — permitir llamadas desde ParkSky (HTTPS)
// hacia la API local (HTTP) sin bloqueo de origen
// =============================================
builder.Services.AddCors(options =>
{
    options.AddPolicy("ParkSky", policy =>
    {
        policy
            .WithOrigins(
                "https://park.sysparking.com",
                "http://localhost:7194",
                "https://localhost:7194"
            )
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// =============================================
// BASE DE DATOS LOCAL
// =============================================
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(opts =>
{
    var cs = !string.IsNullOrWhiteSpace(connStr)
        ? connStr
        : "Server=localhost;Database=PorteriaDB_Empty;Trusted_Connection=True;TrustServerCertificate=True;";
    opts.UseSqlServer(cs, sqlOpts =>
    {
        sqlOpts.CommandTimeout(5);
        sqlOpts.EnableRetryOnFailure(
            maxRetryCount: 0,
            maxRetryDelay: TimeSpan.Zero,
            errorNumbersToAdd: null);
    });
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

// ── CORS debe ir antes de UseRouting ──
app.UseCors("ParkSky");

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

app.MapGet("/status", (IConfiguration config) => new
{
    ok = true,
    app = "HikvisionApi",
    version = "2.0",
    hora = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
    modo = config["ModoOperacion"] ?? "—",
    fuenteDatos = config["Parqueadero:FuenteDatos"] ?? config["Porteria:FuenteDatos"] ?? "—",
    parkSkyUrl = config["ParkSkySettings:ApiUrl"] ?? "—",
    barrera = config["BarrierSettings:BaseUrl"] ?? "—",
    anprFolder = config["AnprSettings:TargetFolder"] ?? "—"
});

app.Run();