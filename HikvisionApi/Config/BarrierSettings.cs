namespace HikvisionApi.Config
{
    public class BarrierSettings
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public DoorSettings Doors { get; set; } = new();
    }

    public class DoorSettings
    {
        public string Entrada1 { get; set; } = "1";
        public string Salida1 { get; set; } = "2";
        public string Entrada2 { get; set; } = "3";
        public string Salida2 { get; set; } = "4";
    }
}