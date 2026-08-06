using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Printing.Devices.Dnp;

/// <summary>
/// Interrogation et réglage des imprimantes à sublimation DNP (DS620, DS820, QW410)
/// via <c>cspstat.dll</c>.
///
/// À n'instancier que dans un processus 32 bits — la DLL est en x86.
///
/// Le tirage lui-même ne passe pas par ici : il emprunte le pilote Windows
/// (<c>BitmapPrinter</c> + <c>DevMode</c>). Cette classe sert à savoir si la machine
/// peut imprimer, combien de tirages restent sur le rouleau, et à appliquer finition,
/// découpe et vitesse avant l'envoi.
/// </summary>
public sealed class DnpDriver
{
    private const int ValueCapacity = 256;

    /// <summary>Imprimantes que la découverte peut rendre — la douzaine que prévoit DiLand.</summary>
    private const int MaxPrinters = 12;

    /// <summary>Type et identifiant d'unité : deux octets par machine dans le tampon.</summary>
    private const int OctetsParImprimante = 2;

    /// <summary>
    /// La bibliothèque du SDK DNP. Voir <see cref="CspStatInterop"/> : le poste porte
    /// aussi un <c>CPPCtrl32.dll</c> aux mêmes noms de fonctions, qui ne découvre AUCUNE
    /// imprimante à sublimation — elle sert aux imprimantes à cartes. DiLand appelle
    /// celle-ci.
    /// </summary>
    private const string SdkFileName = "cspstat.dll";

    /// <summary>Déclare où trouver le SDK DNP.</summary>
    public static void UseSdkFrom(string directory) => NativeSdkResolver.Register(SdkFileName, directory);

    /// <summary>
    /// Cherche le SDK DNP et le déclare s'il est trouvé. Comme celui de Fuji, il est
    /// livré avec DiLand et non avec Studio.
    /// </summary>
    public static string? LocateSdk() => NativeSdkResolver.Locate(SdkFileName, "STUDIO_DNP_SDK");

    /// <summary>Vrai si le SDK DNP est chargeable depuis ce poste.</summary>
    public static bool IsSdkInstalled()
    {
        if (NativeSdkResolver.DirectoryOf(SdkFileName) is not null)
            return NativeSdkResolver.Exists(SdkFileName);

        var loaded = NativeLibrary.TryLoad(SdkFileName, out var lib);
        if (loaded) NativeLibrary.Free(lib);
        return loaded;
    }

    /// <summary>
    /// Numéros de port des imprimantes DNP branchées.
    ///
    /// Le « numéro de port » attendu par toutes les autres fonctions est en réalité le
    /// RANG de la machine dans la découverte (0, 1, 2…), et non une valeur lue dans le
    /// tampon : celui-ci ne contient que le type et l'identifiant d'unité, deux octets par
    /// imprimante. C'est ainsi que DiLand procède (<c>DnpHelper.GetPrinters</c>).
    ///
    /// Corrigé le 03/08/2026 : on passait un <c>int[]</c> et une taille en éléments, et on
    /// prenait le CONTENU du tampon pour des numéros de port. La fonction rendait 0 — la
    /// DS620 restait invisible même DiLand fermé, ce qu'on mettait sur le compte du port
    /// USB tenu.
    /// </summary>
    public IReadOnlyList<int> ListPorts()
    {
        // DiLand n'appelle PAS SetPrinterFilter avant la découverte : on s'en tient à son
        // enchaînement, seul éprouvé sur cette machine.
        var taille = MaxPrinters * OctetsParImprimante;
        var tampon = Marshal.AllocHGlobal(taille);
        try
        {
            var trouvees = CspStatInterop.GetPrinterPortNum(tampon, taille);
            if (trouvees <= 0) return [];

            return Enumerable.Range(0, Math.Min(trouvees, MaxPrinters)).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(tampon);
        }
    }

    /// <summary>État courant d'une imprimante.</summary>
    public DnpStatus GetStatus(int portNumber) => new(CspStatInterop.GetStatus(portNumber));

    /// <summary>Instantané complet d'une imprimante : identité, état, média, compteurs.</summary>
    public DnpPrinterInfo GetPrinterInfo(int portNumber)
    {
        return new DnpPrinterInfo(
            PortNumber: portNumber,
            SerialNumber: ReadString(sb => CspStatInterop.GetSerialNo(portNumber, sb)),
            FirmwareVersion: ReadString(sb => CspStatInterop.GetFirmwVersion(portNumber, sb)),
            Status: GetStatus(portNumber),
            MediaRemaining: CspStatInterop.GetMediaCounter(portNumber),
            MediaInitialCount: CspStatInterop.GetInitialMediaCount(portNumber),
            MediaSize: ParseMediaSize(ReadString(sb => CspStatInterop.GetMedia(portNumber, sb))),
            MediaClass: ParseMediaClass(ReadString(sb => CspStatInterop.GetRfidMediaClass(portNumber, sb))),
            QueuedPrints: CspStatInterop.GetPQTY(portNumber),
            LifetimePrints: CspStatInterop.GetCounterA(portNumber));
    }

    /// <summary>Tirages restants sur le rouleau chargé.</summary>
    public int GetMediaRemaining(int portNumber) => CspStatInterop.GetMediaCounter(portNumber);

    /// <summary>Nombre de tirages en attente dans la mémoire de l'imprimante.</summary>
    public int GetQueuedPrints(int portNumber) => CspStatInterop.GetPQTY(portNumber);

    /// <summary>Mémoire libre de l'imprimante, en octets.</summary>
    public int GetFreeBuffer(int portNumber) => CspStatInterop.GetFreeBuffer(portNumber);

    /// <summary>Résolution de l'imprimante, en points par pouce (horizontale, verticale).</summary>
    public (int Horizontal, int Vertical) GetResolution(int portNumber) =>
        (CspStatInterop.GetResolutionH(portNumber), CspStatInterop.GetResolutionV(portNumber));

    /// <summary>Applique la finition de surface au(x) tirage(s) suivant(s).</summary>
    public void SetOvercoat(int portNumber, DnpOvercoat overcoat) =>
        CspStatInterop.SetOvercoatFinish(portNumber, (int)overcoat);

    /// <summary>Applique le mode de découpe.</summary>
    public void SetCutter(int portNumber, DnpCutter cutter) =>
        CspStatInterop.SetCutterMode(portNumber, (int)cutter);

    /// <summary>Applique la vitesse d'impression.</summary>
    public void SetPrintSpeed(int portNumber, DnpPrintSpeed speed) =>
        CspStatInterop.SetPrintSpeed(portNumber, (int)speed);

    /// <summary>Déclare le format de média chargé.</summary>
    public void SetMediaSize(int portNumber, DnpMediaSize media) =>
        CspStatInterop.SetMediaSize(portNumber, (int)media);

    /// <summary>Active l'anti-tuilage du papier en sortie.</summary>
    public void SetDecurl(int portNumber, bool enabled) =>
        CspStatInterop.SetDecurlCtrl(portNumber, enabled ? 1 : 0);

    /// <summary>Délai d'attente USB, en millisecondes.</summary>
    public void SetUsbTimeout(int portNumber, int milliseconds) =>
        CspStatInterop.SetUSBTimeout(portNumber, milliseconds);

    private static string ReadString(Func<StringBuilder, int> read)
    {
        var buffer = new StringBuilder(ValueCapacity);
        read(buffer);
        return buffer.ToString().Trim();
    }

    /// <summary>
    /// Le format du rouleau chargé, lu dans le libellé que rend <c>GetMedia</c>.
    ///
    /// CE LIBELLÉ N'EST PAS UN NOMBRE. La DS620 de la boutique rend « 00301 » : les TROIS
    /// premiers chiffres portent le format (003 = <see cref="DnpMediaSize.Size6x4"/>, le
    /// rouleau 10×15), les deux derniers autre chose. Lu en entier, ça donnait 301, aucun
    /// format ne correspondait, et le bandeau affichait « None » depuis le début —
    /// constaté le 06/08/2026, une fois la bonne bibliothèque appelée.
    /// </summary>
    private static DnpMediaSize ParseMediaSize(string value)
    {
        var chiffres = new string(value.TakeWhile(char.IsDigit).ToArray());
        if (chiffres.Length > 3) chiffres = chiffres[..3];

        return int.TryParse(chiffres, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
               && Enum.IsDefined(typeof(DnpMediaSize), n)
            ? (DnpMediaSize)n
            : DnpMediaSize.None;
    }

    /// <summary>
    /// La classe de média lue sur la puce RFID du rouleau.
    ///
    /// Elle sort en CHIFFRES, pas en lettres : « 0002 » sur le rouleau de la boutique. Les
    /// libellés RX / HQL / HDM sont acceptés en plus, sans preuve qu'une machine les rende
    /// un jour — ils ne coûtent rien et évitent d'avoir à y revenir.
    /// </summary>
    private static DnpMediaClass ParseMediaClass(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && Enum.IsDefined(typeof(DnpMediaClass), n))
            return (DnpMediaClass)n;

        return value.ToUpperInvariant() switch
        {
            "RX" => DnpMediaClass.Rx,
            "HQL" => DnpMediaClass.Hql,
            "HDM" => DnpMediaClass.Hdm,
            _ => DnpMediaClass.Unknown,
        };
    }
}
