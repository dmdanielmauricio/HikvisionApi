namespace HikvisionApi.Config
{
    public class AnprSettings
    {
        public string TargetFolder { get; set; } = @"C:\ANPR";
        public string RawFolder { get; set; } = @"C:\ANPR\Raw";
        public string LogsFolder { get; set; } = @"C:\ANPR\Logs";
        /// URL base pública de esta API — usada para construir URLs de imágenes
        /// Ej: "http://10.20.34.158:85"
        public string BaseUrl { get; set; } = "";
    }
}
