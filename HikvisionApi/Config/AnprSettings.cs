namespace HikvisionApi.Config
{
    public class AnprSettings
    {
        public string TargetFolder { get; set; } = @"C:\ANPR";
        public string RawFolder { get; set; } = @"C:\ANPR\Raw";
        public string LogsFolder { get; set; } = @"C:\ANPR\Logs";
        /// URL base pública de esta API
        public string BaseUrl { get; set; } = "";
        /// Canales que corresponden a ENTRADA
        public List<string> CarrilesEntrada { get; set; } = new() { "1", "3" };
        /// Canales que corresponden a SALIDA
        public List<string> CarrilesSalida { get; set; } = new() { "2", "4" };
        /// Placas o palabras que se descartan automáticamente (no reconocidas)
        /// Ej: ["UNKNOWN", "DESCONOCIDA", "NO PLATE", "-------"]
        public List<string> PlacasDescartadas { get; set; } = new()
            { "UNKNOWN", "DESCONOCIDA", "NO PLATE", "NOPLATE", "-------", "000000" };
        /// Longitud mínima de placa para ser procesada (descarta lecturas parciales)
        public int LongitudMinimaPlaca { get; set; } = 5;
    }
}