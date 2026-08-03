using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Printing.Devices.Dnp;

/// <summary>
/// Liaison brute avec le SDK de statut DNP (<c>CPPCtrl32.dll</c>), utilisé par les
/// imprimantes à sublimation DS620 / DS820 / QW410.
///
/// LA BIBLIOTHÈQUE EST BIEN <c>CPPCtrl32.dll</c>. Le poste porte aussi un
/// <c>cspstat.dll</c> qui exporte les mêmes noms, et l'appeler fait planter le processus
/// en violation d'accès (constaté le 31/07/2026 sur <c>GetSerialNo</c>). DiLand appelle
/// <c>CPPCtrl32.dll</c> pour ses 47 fonctions : c'est la référence, elle tourne en
/// production sur cette machine.
///
/// ATTENTION : la DLL est en 32 bits, mêmes contraintes que le SDK Fuji.
///
/// Cette bibliothèque ne sert PAS à imprimer : les tirages passent par le pilote
/// Windows de l'imprimante (voir <c>BitmapPrinter</c> et <c>DevMode</c>). <c>cspstat</c>
/// donne l'état machine, les compteurs, le média chargé, et permet de régler finition,
/// massicot et vitesse avant un tirage.
///
/// Toutes les fonctions prennent un numéro de port USB, obtenu via
/// <see cref="GetPrinterPortNum"/>. Les <c>StringBuilder</c> doivent être alloués par
/// l'appelant (256 caractères suffisent partout).
/// </summary>
internal static class CspStatInterop
{
    private const string Dll = "CPPCtrl32.dll";

    // — découverte —

    /// <summary>
    /// Découverte des imprimantes branchées.
    ///
    /// ATTENTION au contrat, relevé dans la décompilation de DiLand
    /// (<c>DnpHelper.GetPrinters</c>) le 03/08/2026 : le tampon est un tableau d'OCTETS —
    /// deux par imprimante, (type, identifiant d'unité) — et <paramref name="arraySize"/>
    /// est sa taille EN OCTETS, pas un nombre d'éléments. Un <c>int[]</c> ne convient pas :
    /// la fonction rendait 0 et aucune imprimante n'était vue.
    /// </summary>
    /// <param name="portArray">Tampon d'octets alloué par l'appelant.</param>
    /// <param name="arraySize">Taille du tampon, en octets.</param>
    /// <returns>Nombre d'imprimantes trouvées.</returns>
    [DllImport(Dll)]
    internal static extern int GetPrinterPortNum(IntPtr portArray, int arraySize);

    /// <summary>Restreint la découverte à un type d'imprimante (voir <see cref="DnpPrinterType"/>).</summary>
    [DllImport(Dll)]
    internal static extern int SetPrinterFilter(int filter);

    // — état —

    /// <summary>Voir <see cref="DnpStatus"/> pour le décodage.</summary>
    [DllImport(Dll)]
    internal static extern uint GetStatus(int portNumber);

    [DllImport(Dll)]
    internal static extern uint GetMiniLabTowerStatus(int portNumber);

    /// <summary>Nombre de tirages encore possibles avec le rouleau chargé.</summary>
    [DllImport(Dll)]
    internal static extern int GetMediaCounter(int portNumber);

    /// <summary>Capacité initiale du rouleau, pour calculer un pourcentage restant.</summary>
    [DllImport(Dll)]
    internal static extern int GetInitialMediaCount(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetMediaCounterH(int portNumber);

    /// <summary>Taille de média chargée (voir <see cref="DnpMediaSize"/>).</summary>
    [DllImport(Dll)]
    internal static extern int GetMedia(int portNumber, StringBuilder value);

    [DllImport(Dll)]
    internal static extern int GetMediaLotNo(int portNumber, StringBuilder value);

    [DllImport(Dll)]
    internal static extern int GetMediaColorOffset(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetMediaIdSetInfo(int portNumber);

    /// <summary>Classe de média lue sur la puce RFID du rouleau (voir <see cref="DnpMediaClass"/>).</summary>
    [DllImport(Dll)]
    internal static extern int GetRfidMediaClass(int portNumber, StringBuilder value);

    [DllImport(Dll)]
    internal static extern int GetRfidReserveData(int portNumber, StringBuilder value, int page);

    /// <summary>Nombre de tirages en file dans la mémoire de l'imprimante.</summary>
    [DllImport(Dll)]
    internal static extern int GetPQTY(int portNumber);

    /// <summary>Mémoire libre de l'imprimante ; sert à savoir si un envoi va être accepté.</summary>
    [DllImport(Dll)]
    internal static extern int GetFreeBuffer(int portNumber);

    [DllImport(Dll)]
    internal static extern uint GetPanoramaPrintable(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetSensorInfo(int portNumber, StringBuilder value);

    // — identité —

    [DllImport(Dll)]
    internal static extern int GetSerialNo(int portNumber, StringBuilder value);

    [DllImport(Dll)]
    internal static extern int GetFirmwVersion(int portNumber, StringBuilder value);

    [DllImport(Dll)]
    internal static extern int GetResolutionH(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetResolutionV(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetMiniLabGroupNo(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetMiniLabSerialNo(int portNumber);

    // — compteurs de tirages (durée de vie de la machine) —

    /// <summary>Compteur total de tirages — remis à zéro uniquement en SAV.</summary>
    [DllImport(Dll)]
    internal static extern int GetCounterA(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetCounterB(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetCounterL(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetCounterM(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetCounterP(int portNumber);

    [DllImport(Dll)]
    internal static extern int GetCounterMatte(int portNumber);

    // — réglages appliqués au tirage suivant —

    /// <summary>Finition de surface, voir <see cref="DnpOvercoat"/>.</summary>
    [DllImport(Dll)]
    internal static extern int SetOvercoatFinish(int portNumber, int overcoat);

    /// <summary>Mode de découpe, voir <see cref="DnpCutter"/>.</summary>
    [DllImport(Dll)]
    internal static extern int SetCutterMode(int portNumber, int mode);

    /// <summary>Vitesse d'impression, voir <see cref="DnpPrintSpeed"/>.</summary>
    [DllImport(Dll)]
    internal static extern int SetPrintSpeed(int portNumber, int printSpeed);

    /// <summary>Taille de média, voir <see cref="DnpMediaSize"/>.</summary>
    [DllImport(Dll)]
    internal static extern int SetMediaSize(int portNumber, int media);

    [DllImport(Dll)]
    internal static extern int SetResolution(int portNumber, int resolution);

    /// <summary>Anti-tuilage du papier en sortie.</summary>
    [DllImport(Dll)]
    internal static extern int SetDecurlCtrl(int portNumber, int mode);

    [DllImport(Dll)]
    internal static extern bool SetContPanorama(int portNumber, int continuous, int overlap);

    [DllImport(Dll)]
    internal static extern int SetStandbyTime(int portNumber, int time);

    [DllImport(Dll)]
    internal static extern int SetUSBTimeout(int portNumber, int time);

    [DllImport(Dll)]
    internal static extern uint SetRetryControl(int portNumber, int retry);

    // — envoi d'image brute (contournement du pilote Windows ; non utilisé par Studio) —

    [DllImport(Dll)]
    internal static extern int SendImageData(int portNumber, IntPtr buffer, int xPos, int yPos, int width, int height);

    // — données colorimétriques et micrologiciel : réservé au SAV, manipuler avec précaution —

    [DllImport(Dll)]
    internal static extern int GetColorDataVersion(int portNumber, StringBuilder value);

    [DllImport(Dll)]
    internal static extern int GetColorDataVersionRes(int portNumber, StringBuilder value, int resolution);

    [DllImport(Dll)]
    internal static extern int GetColorDataVersionResEX(int portNumber, StringBuilder value, int resolution, int media);

    [DllImport(Dll)]
    internal static extern int GetColorDataChecksum(int portNumber, StringBuilder value);

    [DllImport(Dll)]
    internal static extern int GetColorDataChecksumRes(int portNumber, StringBuilder value, int resolution);

    [DllImport(Dll)]
    internal static extern int GetColorDataChecksumResEX(int portNumber, StringBuilder value, int resolution, int media);

    [DllImport(Dll)]
    internal static extern bool SetColorDataClear(int portNumber);

    [DllImport(Dll)]
    internal static extern bool SetColorDataVersion(int portNumber, string data, int dataLen);

    [DllImport(Dll)]
    internal static extern bool SetColorDataWrite(int portNumber, byte[] data, int dataLen);

    [DllImport(Dll)]
    internal static extern int SetFirmwUpdateMode(int portNumber);

    [DllImport(Dll)]
    internal static extern int SetFirmwDataWrite(int portNumber, string data, int dataLen);

    // — dos imprimé et identification en grappe (tours multi-imprimantes) —

    [DllImport(Dll)]
    internal static extern int SetMiniLabBackprintData(int portNumber, string data, int dataLen);

    [DllImport(Dll)]
    internal static extern int SetMiniLabGroupNo(int portNumber, int groupNumber);

    [DllImport(Dll)]
    internal static extern int SetMiniLabGroupNoIni(int portNumber, int groupNumber, int serialNumber, int option);

    [DllImport(Dll)]
    internal static extern int SetMiniLabSerialNo(int portNumber, int serialNumber, int final);
}
