using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Printing.Devices.Fuji;

/// <summary>
/// Liaison brute avec le SDK du minilab Fuji Frontier DE100 (<c>PModuleIF.dll</c>).
///
/// ATTENTION : la DLL est en 32 bits. Tout processus qui appelle ces fonctions doit
/// être compilé en x86, sinon le premier appel lève <see cref="BadImageFormatException"/>.
/// Studio.App tourne en 64 bits (c'est voulu — c'est ce qui nous évite les OOM de
/// DiLand) : le dialogue avec le DE100 se fait donc depuis un processus hôte x86.
///
/// Signatures relevées sur le SDK installé avec DiLand Studio 2.
/// </summary>
internal static class De100Interop
{
    private const string Dll = "PModuleIF.dll";

    /// <summary>Notification d'événement machine (erreur, avertissement). <paramref name="onOff"/> : 1 = apparition, 0 = disparition.</summary>
    internal delegate void EventCallback(IntPtr eventInfo, uint onOff);

    /// <summary>Notification de changement d'état d'une commande. Le pointeur vise un <see cref="ST_ORDER_INFO"/>.</summary>
    internal delegate void OrderCallback(IntPtr orderInfo);

    // — session —

    [DllImport(Dll)]
    internal static extern int PIF_Open();

    [DllImport(Dll)]
    internal static extern int PIF_Close();

    // — découverte et état des machines —

    /// <param name="machineIds">Reçoit la liste des identifiants machine, un caractère chacun.</param>
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PIF_GetPrinterList(StringBuilder machineIds, ref uint machineIdCount);

    [DllImport(Dll)]
    internal static extern int PIF_DevIsReady(char machineId);

    [DllImport(Dll)]
    internal static extern int PIF_DevGetPrinterInfo(char machineId, ref IntPtr handle);

    [DllImport(Dll)]
    internal static extern int PIF_DevGetSetupInfo(char machineId, ref IntPtr handle);

    [DllImport(Dll)]
    internal static extern int PIF_DevGetPixelCount(char machineId, ref ST_PRINT_SIZE printSize,
        ref uint pxImageWidth, ref uint pxImageHeight);

    [DllImport(Dll)]
    internal static extern int PIF_DevSetCallbackAddress(char machineId,
        [MarshalAs(UnmanagedType.FunctionPtr)] EventCallback? eventProc,
        [MarshalAs(UnmanagedType.FunctionPtr)] OrderCallback? orderProc);

    // — lecture des propriétés d'une machine (handle obtenu ci-dessus) —

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PHIF_GetValue(IntPtr handle, StringBuilder name, StringBuilder value);

    /// <summary>Variante indexée, pour les propriétés par magasin (rouleau) : <c>n</c> = index du magasin.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PHIF_GetNValue(IntPtr handle, StringBuilder name, uint n, StringBuilder value);

    [DllImport(Dll)]
    internal static extern int PHIF_ReleaseHandle(IntPtr handle);

    // — cycle de vie d'une commande —

    /// <param name="orderHandle">Tampon d'au moins 256 caractères ; reçoit le handle de commande.</param>
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PIF_StartOrder(StringBuilder orderHandle);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PIF_Print(StringBuilder orderHandle, ref ST_IMAGE_DATA imageData,
        ST_PARAM[] parameters, uint parameterCount);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PIF_EndOrder(StringBuilder orderHandle);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PIF_CancelOrder(StringBuilder orderHandle);

    /// <summary>Passe la commande en priorité haute dans la file du minilab.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PIF_ExpressOrder(StringBuilder orderHandle);

    /// <summary>
    /// ⚠ <b>CharSet.Unicode est OBLIGATOIRE</b>, comme pour toute fonction qui reçoit un
    /// handle de commande — <c>PIF_CancelOrder</c> et <c>PIF_ExpressOrder</c> l'ont depuis
    /// toujours. Il manquait ici, et le défaut de .NET est l'ANSI : le handle partait en
    /// octets simples là où le SDK attend de l'UTF-16, et il ne retrouvait donc JAMAIS la
    /// commande.
    ///
    /// Ce que ça coûtait : <c>OrderProgress</c> rendait toujours <c>null</c>, l'avancement
    /// retombait sur les verdicts — qui n'arrivent qu'à la fin, tous ensemble — et la barre
    /// restait à zéro d'un bout à l'autre de la commande. Constaté sur les trois boutiques
    /// le 12/08/2026 : six commandes minilab, six relevés muets, alors que la veille les
    /// sept commandes du même poste avançaient normalement avec l'ancien compteur global.
    /// </summary>
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PIF_GetOrderInfo(StringBuilder orderHandle, ref ST_ORDER_INFO orderInfo);

    /// <summary>Même exigence d'encodage que <see cref="PIF_GetOrderInfo"/>.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    internal static extern int PIF_GetPrintInfo(StringBuilder orderHandle, uint index, ref ST_PRINT_INFO printInfo);

    [DllImport(Dll)]
    internal static extern int PIF_SendCommand(ST_PARAM[] parameters, uint parameterCount);
}

/// <summary>Couple nom/valeur passé à <c>PIF_Print</c>. Les noms reconnus sont dans <see cref="De100ParamNames"/>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ST_PARAM
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string name;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string value;
}

/// <summary>Image à tirer : soit un chemin de fichier, soit un buffer RGB en mémoire.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ST_IMAGE_DATA
{
    public IntPtr srcRGB;
    public uint pxImageWidth;
    public uint pxImageHeight;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string imagePath;
}

/// <summary>État d'une commande, tel que remonté par le callback ou <c>PIF_GetOrderInfo</c>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ST_ORDER_INFO
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
    public string orderNo;

    /// <summary>Voir <see cref="De100OrderStatus"/>.</summary>
    public uint status;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
    public string printSizeName;

    public double mmPaperWidth;
    public double mmPaperHeight;
    public uint orderNum;
    public uint printedNum;
    public uint receptionTime;
    public uint completionTime;
    public char machineID;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ohnd;

    public uint buffer1;
    public uint buffer2;
    public uint buffer3;
    public uint buffer4;
}

/// <summary>Détail d'un tirage dans une commande. <c>errmsg</c> porte le message du minilab en cas d'échec.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ST_PRINT_INFO
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
    public string orderNo;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string iifPath;

    public uint printedNum;

    /// <summary>Voir <see cref="De100PrintStatus"/>.</summary>
    public uint status;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
    public string errmsg;

    public uint buffer1;
    public uint buffer2;
    public uint buffer3;
    public uint buffer4;
}

/// <summary>Géométrie d'un format de tirage, pour <c>PIF_DevGetPixelCount</c>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ST_PRINT_SIZE
{
    public double mmPaperWidth;
    public double mmPaperHeight;
    public uint resolution;
    public double mmBorderLeft;
    public double mmBorderRight;
    public double mmBorderTop;
    public double mmBorderBottom;
    public uint booklet;
    public uint openSide;
    public uint border;
    public uint buffer2;
    public uint buffer3;
    public uint buffer4;
}

/// <summary>Événement machine (bourrage, fin de papier, erreur système…).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ST_EVENT_INFO
{
    public char machineID;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string warningCode;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
    public string errorString1;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
    public string errorString2;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
    public string errorString3;

    /// <summary>Voir <see cref="De100ErrorLevel"/>.</summary>
    public uint errorLevel;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string errorNo;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string buttonString1;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string buttonString2;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string buttonString3;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string buttonString4;

    public uint recovery;
    public uint buffer2;
    public uint buffer3;
    public uint buffer4;
}
