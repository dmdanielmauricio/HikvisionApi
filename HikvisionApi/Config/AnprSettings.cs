namespace HikvisionApi.Config
{
    public class AnprSettings
    {
        public string TargetFolder { get; set; } = @"C:\ANPR";
        public string RawFolder { get; set; }   // 👈 NUEVO
        public string LogsFolder { get; set; }
    }
}

