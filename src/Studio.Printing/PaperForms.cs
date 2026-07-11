using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Studio.Printing;

/// <summary>
/// Enregistre des formats papier personnalisés (« formulaires ») auprès du spouleur
/// Windows. Nécessaire pour les pilotes qui n'acceptent que leurs formats déclarés
/// (Microsoft Print to PDF notamment) et pour les longueurs de coupe panoramiques.
/// L'ajout demande des droits d'administration du serveur d'impression ; une fois
/// ajouté, le formulaire persiste pour tous les pilotes compatibles.
/// </summary>
public static class PaperForms
{
    private const int FORM_USER = 0;
    private const int ERROR_FILE_EXISTS = 80;
    private const int ERROR_ALREADY_EXISTS = 183;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZEL { public int cx; public int cy; } // millièmes de millimètre

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTL { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FORM_INFO_1
    {
        public int Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string pName;
        public SIZEL Size;
        public RECTL ImageableArea;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AddForm(IntPtr hPrinter, int level, ref FORM_INFO_1 pForm);

    /// <summary>
    /// Ajoute le formulaire s'il n'existe pas déjà. Retourne false si les droits
    /// manquent (à l'installation on lance ce code une fois en administrateur).
    /// </summary>
    public static bool EnsureForm(string printerName, string formName, double widthMm, double heightMm)
    {
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            throw new InvalidOperationException($"Imprimante introuvable : « {printerName} »");

        try
        {
            var form = new FORM_INFO_1
            {
                Flags = FORM_USER,
                pName = formName,
                Size = new SIZEL { cx = (int)Math.Round(widthMm * 1000), cy = (int)Math.Round(heightMm * 1000) },
                ImageableArea = new RECTL
                {
                    left = 0,
                    top = 0,
                    right = (int)Math.Round(widthMm * 1000),
                    bottom = (int)Math.Round(heightMm * 1000),
                },
            };

            if (AddForm(hPrinter, 1, ref form)) return true;

            var error = Marshal.GetLastWin32Error();
            if (error is ERROR_FILE_EXISTS or ERROR_ALREADY_EXISTS) return true;
            if (error == 5 /* ERROR_ACCESS_DENIED */) return false;
            throw new Win32Exception(error, $"AddForm a échoué pour « {formName} »");
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    /// <summary>Formats photo standard de la boutique, à enregistrer une fois à l'installation.</summary>
    public static readonly (string Name, double WidthMm, double HeightMm)[] ShopForms =
    {
        ("Photo 10x15", 102, 152),
        ("Photo 13x18", 127, 178),
        ("Photo 15x20", 152, 203),
        ("Photo 15x21", 152, 210),
        ("Photo 20x30", 203, 305),
    };
}
