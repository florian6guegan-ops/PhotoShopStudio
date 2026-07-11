using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace Studio.Printing;

/// <summary>
/// Capture et rejeu du DEVMODE (réglages privés du pilote : type de papier,
/// média Brillant/Lustré, sans marges, correction couleur…). On ouvre une fois
/// le dialogue du pilote, on sérialise les octets, et on les rejoue à chaque job :
/// c'est le seul moyen d'atteindre les réglages que PageSettings ne connaît pas.
/// </summary>
public static class DevMode
{
    private const int DM_OUT_BUFFER = 2;
    private const int DM_IN_BUFFER = 8;
    private const int DM_IN_PROMPT = 4;
    private const int IDOK = 1;

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(
        IntPtr hWnd, IntPtr hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    /// <summary>Extrait les octets DEVMODE courants d'un PrinterSettings.</summary>
    public static byte[] Capture(PrinterSettings settings)
    {
        var hDevMode = settings.GetHdevmode();
        try
        {
            var ptr = NativeMethods.GlobalLock(hDevMode);
            try
            {
                var size = Marshal.ReadInt16(ptr, 68) + Marshal.ReadInt16(ptr, 70); // dmSize + dmDriverExtra
                var bytes = new byte[size];
                Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally
            {
                NativeMethods.GlobalUnlock(hDevMode);
            }
        }
        finally
        {
            NativeMethods.GlobalFree(hDevMode);
        }
    }

    /// <summary>Applique des octets DEVMODE sauvegardés à un PrinterSettings.</summary>
    public static void Apply(PrinterSettings settings, byte[] devModeBytes)
    {
        var hGlobal = NativeMethods.GlobalAlloc(0x0042 /* GHND */, (nuint)devModeBytes.Length);
        var ptr = NativeMethods.GlobalLock(hGlobal);
        Marshal.Copy(devModeBytes, 0, ptr, devModeBytes.Length);
        NativeMethods.GlobalUnlock(hGlobal);
        try
        {
            settings.SetHdevmode(hGlobal);
            settings.DefaultPageSettings.SetHdevmode(hGlobal);
        }
        finally
        {
            NativeMethods.GlobalFree(hGlobal);
        }
    }

    /// <summary>
    /// Ouvre le dialogue de configuration du pilote et retourne le DEVMODE choisi
    /// (null si l'utilisateur annule). <paramref name="current"/> pré-remplit le dialogue.
    /// </summary>
    public static byte[]? ShowDriverDialog(string printerName, byte[]? current = null)
    {
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            throw new InvalidOperationException($"Imprimante introuvable : « {printerName} »");

        try
        {
            var size = DocumentProperties(IntPtr.Zero, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0)
                throw new InvalidOperationException($"DocumentProperties a échoué pour « {printerName} »");

            var output = Marshal.AllocHGlobal(size);
            var input = IntPtr.Zero;
            try
            {
                var mode = DM_OUT_BUFFER | DM_IN_PROMPT;
                if (current is not null)
                {
                    input = Marshal.AllocHGlobal(current.Length);
                    Marshal.Copy(current, 0, input, current.Length);
                    mode |= DM_IN_BUFFER;
                }

                var result = DocumentProperties(IntPtr.Zero, hPrinter, printerName, output, input, mode);
                if (result != IDOK) return null;

                var actualSize = Marshal.ReadInt16(output, 68) + Marshal.ReadInt16(output, 70);
                var bytes = new byte[actualSize];
                Marshal.Copy(output, bytes, 0, actualSize);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(output);
                if (input != IntPtr.Zero) Marshal.FreeHGlobal(input);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
