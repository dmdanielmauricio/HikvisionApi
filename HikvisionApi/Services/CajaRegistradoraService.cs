using System.Runtime.InteropServices;

namespace HikvisionApi.Services
{
    /// <summary>
    /// Envía el comando ESC/POS de apertura de caja al puerto RJ11
    /// de la impresora térmica configurada en PrinterSettings:Nombre.
    /// Usa la API de Windows winspool para enviar bytes crudos (RAW).
    /// </summary>
    public class CajaRegistradoraService
    {
        // ESC p m t1 t2 — abre la caja por el conector RJ11 del driver pin 2.
        // Si la caja usa pin 5, cambia 0x00 → 0x01.
        private static readonly byte[] CMD_ABRIR = { 0x1B, 0x70, 0x00, 0x19, 0xFF };

        private readonly string _printerName;
        private readonly ILogger<CajaRegistradoraService> _logger;

        public CajaRegistradoraService(
            IConfiguration config,
            ILogger<CajaRegistradoraService> logger)
        {
            _printerName = config["PrinterSettings:Nombre"] ?? "";
            _logger = logger;
        }

        /// <summary>
        /// Abre la caja registradora.
        /// Devuelve true si el comando se envió a la impresora.
        /// No garantiza que la caja haya abierto físicamente.
        /// </summary>
        public bool AbrirCaja()
        {
            if (string.IsNullOrWhiteSpace(_printerName))
            {
                _logger.LogWarning("AbrirCaja: PrinterSettings:Nombre no configurado — omitido.");
                return false;
            }

            try
            {
                bool ok = EnviarRaw(_printerName, CMD_ABRIR);
                if (ok)
                    _logger.LogInformation("AbrirCaja: comando enviado a '{Printer}'", _printerName);
                else
                    _logger.LogWarning("AbrirCaja: fallo al enviar a '{Printer}'", _printerName);
                return ok;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AbrirCaja: excepción enviando ESC/POS a '{Printer}'", _printerName);
                return false;
            }
        }

        // ── Win32 Raw Print ──────────────────────────────────────────────
        private static bool EnviarRaw(string printerName, byte[] data)
        {
            if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
                return false;

            try
            {
                var docInfo = new DOCINFOA { pDocName = "CajaCmd", pDataType = "RAW" };

                if (!StartDocPrinter(hPrinter, 1, docInfo)) return false;
                if (!StartPagePrinter(hPrinter))
                {
                    EndDocPrinter(hPrinter);
                    return false;
                }

                var ptr = Marshal.AllocCoTaskMem(data.Length);
                Marshal.Copy(data, 0, ptr, data.Length);
                WritePrinter(hPrinter, ptr, data.Length, out _);
                Marshal.FreeCoTaskMem(ptr);

                EndPagePrinter(hPrinter);
                EndDocPrinter(hPrinter);
                return true;
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }

        // ── P/Invoke winspool ────────────────────────────────────────────
        [DllImport("winspool.Drv", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, DOCINFOA di);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes,
                                                int dwCount, out int dwWritten);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPTStr)] public string pDocName = "";
            [MarshalAs(UnmanagedType.LPTStr)] public string? pOutputFile = null;
            [MarshalAs(UnmanagedType.LPTStr)] public string pDataType = "RAW";
        }
    }
}