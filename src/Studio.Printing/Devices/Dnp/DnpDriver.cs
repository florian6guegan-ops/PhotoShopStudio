using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Printing.Devices.Dnp;

/// <summary>
/// Interrogation et réglage des imprimantes à sublimation DNP (DS620, DS820, QW410)
/// via <c>CPPCtrl32.dll</c>.
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
    private const int MaxPrinters = 16;

    /// <summary>
    /// La bibliothèque du SDK DNP. Voir <see cref="CspStatInterop"/> : le poste porte
    /// aussi un <c>cspstat.dll</c> aux mêmes noms de fonctions, dont l'appel fait planter
    /// le processus. DiLand appelle celle-ci.
    /// </summary>
    private const string SdkFileName = "CPPCtrl32.dll";

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
    /// </summary>
    /// <param name="filter">Restreint la recherche à un type de machine.</param>
    public IReadOnlyList<int> ListPorts(DnpPrinterType filter = DnpPrinterType.All)
    {
        CspStatInterop.SetPrinterFilter((int)filter);

        // tableau managé, marshalé tel quel : c'est ainsi que DiLand appelle la fonction,
        // et c'est le seul usage éprouvé sur cette machine
        var ports = new int[MaxPrinters];
        var found = CspStatInterop.GetPrinterPortNum(ports, MaxPrinters);

        return found <= 0 ? [] : ports.Take(Math.Min(found, MaxPrinters)).ToList();
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

    private static DnpMediaSize ParseMediaSize(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
        && Enum.IsDefined(typeof(DnpMediaSize), n)
            ? (DnpMediaSize)n
            : DnpMediaSize.None;

    private static DnpMediaClass ParseMediaClass(string value) => value.ToUpperInvariant() switch
    {
        "RX" => DnpMediaClass.Rx,
        "HQL" => DnpMediaClass.Hql,
        "HDM" => DnpMediaClass.Hdm,
        _ => DnpMediaClass.Unknown,
    };
}
