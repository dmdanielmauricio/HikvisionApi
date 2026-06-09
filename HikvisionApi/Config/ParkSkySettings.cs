namespace HikvisionApi.Config
{
    public class ParkSkySettings
    {
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public bool UsarApiNube { get; set; } = true;
        /// Minutos de gracia entre pago y salida física
        public int TiempoGraciaMinutos { get; set; } = 15;
    }

    public class PorteriaSettings
    {
        /// "Local" | "Nube"
        public string FuenteDatos { get; set; } = "Local";
        public bool AbrirSiSinInternet { get; set; } = false;
        public int TimerSegundos { get; set; } = 0;
        /// Minutos de gracia entre pago y salida física (0 = sin gracia)
        public int TiempoGraciaMinutos { get; set; } = 15;
    }

    public class ParqueaderoSettings
    {
        /// "Local" | "Nube"
        public string FuenteDatos { get; set; } = "Nube";
        public bool AbrirTodo { get; set; } = false;
        public bool EntregarTiquete { get; set; } = true;
        public int TimerEntradaSegundos { get; set; } = 2;
        public int TimerSalidaSegundos { get; set; } = 2;
        public bool AbrirSiSinInternet { get; set; } = false;
        /// Minutos de gracia entre pago y salida física (0 = sin gracia)
        public int TiempoGraciaMinutos { get; set; } = 15;
    }

    public class ImpresorasSettings
    {
        public string Entrada1 { get; set; } = "";
        public string Entrada2 { get; set; } = "";
    }
}
