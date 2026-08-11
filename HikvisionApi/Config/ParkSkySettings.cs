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
        /// false = no imprimir tiquete cuando el vehículo tiene convenio mensual activo.
        /// El convenio se detecta por caché local — no requiere consulta al VPS.
        public bool ImprimirTiqueteMensualidad { get; set; } = true;
        /// Minutos que una placa que acaba de SALIR no puede volver a ENTRAR.
        /// Previene que la cámara de entrada capture un vehículo que aún está saliendo.
        /// 0 = desactivado. Recomendado: 1-3 minutos según el layout del carril.
        public int MinutosAntiReingreso { get; set; } = 2;
        /// Segundos de bloqueo por carril tras procesar cualquier evento.
        /// Si el MISMO carril dispara de nuevo en ese lapso (aunque con placa diferente),
        /// se ignora — atrapa doble trigger aunque el OCR cambie un carácter.
        /// 0 = desactivado. Recomendado: 3-5 segundos.
        public int SegundosAntiDupCarril { get; set; } = 4;
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