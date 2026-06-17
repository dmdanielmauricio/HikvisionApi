namespace HikvisionApi.Config
{
    public class BarrierSettings
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        /// Habilita registro de QR en controladora al pago
        public bool UsarQR { get; set; } = false;

        /// Puertas de salida donde hay lector QR (una o varias)
        /// Una portería:  "PuertaLectorQR": [2]
        /// Dos porterías: "PuertaLectorQR": [2, 4]
        /// La controladora abre SOLO la puerta donde se presenta el QR
        public List<int> PuertaLectorQR { get; set; } = new() { 2 };

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