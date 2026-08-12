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
        // LE FORMAT PAPIER SE RÉSOUT PAR SON NOM, jamais par l'index reçu. Voir
        // RecalerLeFormatPapier : c'est la seule partie d'un DEVMODE qui ne voyage pas.
        devModeBytes = RecalerLeFormatPapier(settings, devModeBytes);

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
    /// Réécrit <c>dmPaperSize</c> d'après <c>dmFormName</c>, tel que CE poste le numérote.
    ///
    /// <b>Un DEVMODE ne voyage pas.</b> Le pilote DNP construit sa liste de formats à
    /// l'installation et les numérote à la suite ; le même papier n'a donc pas le même
    /// index d'un poste à l'autre. Relevé le 12/08/2026, trois postes, même pilote, même
    /// liste dans le même ordre :
    ///
    /// <list type="table">
    /// <item><term>Maisons-Alfort</term><description><c>(6x4)</c> = 121</description></item>
    /// <item><term>Créteil</term><description><c>(6x4)</c> = 127</description></item>
    /// <item><term>DESKTOP-KT88VDM</term><description><c>(6x4)</c> = 147</description></item>
    /// </list>
    ///
    /// Or le catalogue livré embarque le DEVMODE capturé à Maisons-Alfort. Les deux autres
    /// postes recevaient donc un index qui n'existe pas chez eux — 121 pour une plage
    /// valide de 145 à 155 sur le troisième — et le pilote faisait ce qu'il pouvait : la
    /// planche sortait dans une page trop grande. Chacun a été rustiné à la main, et le
    /// défaut revenait à la publication suivante.
    ///
    /// <b>Le nom, lui, est stable</b>, et le DEVMODE le porte déjà dans
    /// <c>dmFormName</c> — « (6x4) » dans les trois cas. On le relit, on demande à CE
    /// poste comment il numérote ce format, et l'on réécrit l'index avant d'appliquer.
    ///
    /// Au moindre doute — pas de nom, format inconnu du pilote local, DEVMODE trop court —
    /// on rend les octets INCHANGÉS : le comportement d'avant, qui marchait sur le poste
    /// d'origine et n'a jamais empêché un tirage de partir.
    /// </summary>
    internal static byte[] RecalerLeFormatPapier(PrinterSettings settings, byte[] devModeBytes)
    {
        // DEVMODEW : dmPaperSize (short) à 78, dmFormName (32 WCHAR) à 102.
        const int offsetPaperSize = 78;
        const int offsetFormName = 102;
        const int tailleFormName = 32 * 2;

        if (devModeBytes.Length < offsetFormName + tailleFormName) return devModeBytes;

        var nom = System.Text.Encoding.Unicode
            .GetString(devModeBytes, offsetFormName, tailleFormName)
            .TrimEnd('\0')
            .Trim();

        if (nom.Length == 0) return devModeBytes;

        int local;
        try
        {
            local = settings.PaperSizes
                .Cast<PaperSize>()
                .FirstOrDefault(p => string.Equals(p.PaperName, nom, StringComparison.OrdinalIgnoreCase))
                ?.RawKind ?? 0;
        }
        catch (Exception)
        {
            // imprimante absente ou pilote muet : on n'a rien de mieux que ce qu'on a reçu
            return devModeBytes;
        }

        if (local == 0) return devModeBytes;

        var recu = BitConverter.ToInt16(devModeBytes, offsetPaperSize);
        if (recu == local) return devModeBytes;

        // copie : les octets viennent du catalogue et peuvent être partagés entre tirages
        var corrige = (byte[])devModeBytes.Clone();
        BitConverter.GetBytes((short)local).CopyTo(corrige, offsetPaperSize);

        Log?.Invoke($"Réglages pilote : format « {nom} » numéroté {recu} à l'enregistrement, " +
                    $"{local} sur ce poste — index recalé.");

        return corrige;
    }

    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Ouvre le dialogue du pilote SANS bloquer le fil de l'interface.
    ///
    /// <b>Le défaut qu'elle corrige, et il tuait l'application.</b> Le dialogue du pilote
    /// DS620 interroge la machine pour se remplir — il porte un onglet « Infos de
    /// l'imprimante ». Or DiLand tient le port USB en exclusif (voir
    /// <see cref="Devices.Dnp.DiLandPresence"/>), et il tourne en permanence : le dialogue
    /// s'ouvre, reste « (Ne répond pas) », et le fil de l'interface est bloqué DANS un
    /// appel natif dont on ne peut pas sortir. Windows a alors fait ce qu'il fait d'une
    /// fenêtre qui ne pompe plus : il a fermé l'application. Trois fois en onze minutes le
    /// 06/08/2026 — journal des événements, <c>AppHangXProcB1</c>.
    ///
    /// Le dialogue part donc sur SON PROPRE fil, en appartement cloisonné comme toute
    /// fenêtre Windows. L'écran reste vivant pendant ce temps ; si le pilote ne répond
    /// jamais, l'application survit, et c'est le fil du dialogue qui reste en plan — il est
    /// d'arrière-plan, il ne retiendra pas la fermeture.
    /// </summary>
    /// <returns>Le DEVMODE retenu, ou null si l'opérateur a annulé.</returns>
    public static Task<byte[]?> ShowDriverDialogAsync(string printerName, byte[]? current = null)
    {
        var promesse = new TaskCompletionSource<byte[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var fil = new Thread(() =>
        {
            try
            {
                promesse.TrySetResult(ShowDriverDialog(printerName, current));
            }
            catch (Exception ex)
            {
                promesse.TrySetException(ex);
            }
        })
        {
            // d'arrière-plan : un dialogue que le pilote ne rend jamais ne doit pas
            // empêcher l'application de se fermer
            IsBackground = true,
            Name = "Dialogue pilote",
        };

        // toute fenêtre Windows veut un appartement cloisonné ; le dialogue d'un pilote
        // ouvre en outre des objets COM du shell
        fil.SetApartmentState(ApartmentState.STA);
        fil.Start();

        return promesse.Task;
    }

    /// <summary>
    /// Ouvre le dialogue de configuration du pilote et retourne le DEVMODE choisi
    /// (null si l'utilisateur annule). <paramref name="current"/> pré-remplit le dialogue.
    ///
    /// <b>Bloque le fil appelant</b> tant que l'opérateur n'a pas répondu — et
    /// indéfiniment si le pilote ne répond pas. À n'appeler que depuis un outil en ligne
    /// de commande ; l'application, elle, passe par <see cref="ShowDriverDialogAsync"/>.
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
