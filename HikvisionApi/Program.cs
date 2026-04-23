using HikvisionApi.Config;
using HikvisionApi.Data;
using HikvisionApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// 🔹 Configuración
builder.Services.Configure<AnprSettings>(
    builder.Configuration.GetSection("AnprSettings"));

builder.Services.Configure<BarrierSettings>(
    builder.Configuration.GetSection("BarrierSettings"));

// 🔹 DB
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// 🔹 Servicios
builder.Services.AddScoped<HikvisionService>();

// 🔹 Controllers
builder.Services.AddControllers();

// 🔹 Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ANPR API",
        Version = "v1"
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// 🔹 Archivos estáticos
var anprSettings = app.Services.GetRequiredService<IOptions<AnprSettings>>().Value;

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(anprSettings.TargetFolder),
    RequestPath = "/anpr"
});

app.MapControllers();

app.Run();