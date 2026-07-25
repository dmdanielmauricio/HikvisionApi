namespace HikvisionApi.Services
{
    // Interfaz de notificación de eventos ANPR al dashboard web.
    // HikvisionService la inyecta sin conocer SignalR ni AnprHub directamente.
    //
    // Registrar en Program.cs según el caso:
    //
    //   TODOS los clientes (con o sin dashboard):
    //     builder.Services.AddSingleton<IAnprEventBus, AnprEventBusNoop>();
    //
    //   Clientes CON dashboard (además del anterior, sobreescribe el registro):
    //     builder.Services.AddSignalR();
    //     builder.Services.AddSingleton<IAnprEventBus, AnprEventBusSignalR>();
    //     app.MapHub<HikvisionApi.Hubs.AnprHub>("/anpr-hub");
    public interface IAnprEventBus
    {
        Task PublicarAsync(object evento);
    }

    // Implementación no-op — no hace nada, no depende de SignalR.
    // Usada por defecto en todos los clientes sin dashboard.
    // Compila sin AnprHub ni ninguna referencia a SignalR.
    public class AnprEventBusNoop : IAnprEventBus
    {
        public Task PublicarAsync(object evento) => Task.CompletedTask;
    }
}