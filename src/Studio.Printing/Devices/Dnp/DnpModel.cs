namespace Studio.Printing.Devices.Dnp;

/// <summary>Modèles d'imprimantes reconnus par le SDK DNP.</summary>
public enum DnpDeviceId
{
    Unknown = 0,
    Cv = 1,
    Cw = 2,
    Cx = 3,
    Cxw = 4,
    Cy = 5,
    Cw02 = 6,
    Ds80Dup = 8,
    TowerController = 10,
    Ds620 = 20,
    Ds621 = 21,
    Ds820 = 30,
    Qw410 = 35,
    CyDiSup = 91,
}

/// <summary>Filtre de découverte passé à <c>SetPrinterFilter</c>.</summary>
public enum DnpPrinterType
{
    All = 0,
    SingleSide = 1,
    Duplex = 2,
}

/// <summary>Finition de surface appliquée au tirage.</summary>
public enum DnpOvercoat
{
    Glossy = 0,
    Matte = 1,
    FineMatte = 21,
    Luster = 22,
    PremiumMatte11 = 101,
    PremiumMatte12 = 121,
    PremiumMatte13 = 122,
}

/// <summary>Mode de découpe.</summary>
public enum DnpCutter
{
    Standard = 0,
    /// <summary>Sans chute : évite le petit déchet de coupe.</summary>
    NoScrap = 1,
    SingleCut = 2,
    TwoInchCut = 120,
    /// <summary>Deux images sur un tirage L.</summary>
    L2ImagePrint = 130,
    Panorama = 1000,
}

/// <summary>Vitesse d'impression.</summary>
public enum DnpPrintSpeed
{
    Normal = 0,
    /// <summary>Plus lent, densité supérieure.</summary>
    HighDensity = 3,
}

/// <summary>Classe de média lue sur la puce RFID du rouleau.</summary>
public enum DnpMediaClass
{
    Unknown = 0,
    Rx = 1,
    Hql = 2,
    Hdm = 3,
}

/// <summary>
/// Formats de média DNP. Les valeurs sont celles attendues par <c>SetMediaSize</c>.
/// Le DS620 utilise principalement la plage 1–14 ; le DS820 la plage 31–80.
/// </summary>
public enum DnpMediaSize
{
    None = -1,
    Size5x3 = 1,
    Size5x7 = 2,
    Size6x4 = 3,
    Size6x8 = 4,
    Size6x9 = 5,
    Size6x4x2 = 6,
    SizeLx2 = 7,
    PostcardRewind = 8,
    LRewind = 9,
    Size5x5 = 10,
    Size6x6 = 11,
    Size6x4p5 = 12,
    Size6x4p5x2 = 13,
    Size6x4p5Rewind = 14,
    Size8x10 = 31,
    Size8x12 = 32,
    Size8x4 = 33,
    Size8x5 = 34,
    Size8x6 = 35,
    Size8x8 = 36,
    Size8x4x2 = 37,
    Size8x5x2 = 38,
    Size8x6x2 = 39,
    Size8x5And8x4 = 40,
    Size8x6And8x4 = 41,
    Size8x6And8x5 = 42,
    Size8x8And8x4 = 43,
    Size8x4x3 = 44,
    A4Length = 45,
    Size8x7 = 46,
    Size8x9 = 47,
    Size8x4Rewind = 48,
    Size8x5Rewind = 49,
    Size8x6Rewind = 50,
    Size8x10_50 = 51,
    Size8x10_75 = 52,
    Size8x4x3Cut = 53,
    Size4x4 = 54,
    Size4x6 = 55,
    Size4x8 = 56,
    Size4p5x4p5 = 57,
    Size4p5x6 = 58,
    Size4p5x8 = 59,
    A5 = 71,
    A5x2 = 72,
    A5Rewind = 73,
    A4x5 = 74,
    A4x6 = 75,
    A4x8 = 76,
    A4x10 = 77,
    A4 = 78,
    A4x5x2 = 79,
    A4x5Rewind = 80,
    Size8x10CutPaper = 131,
    Size8x12CutPaper = 132,
    Size8x4CutPaper = 133,
    Size8x5CutPaper = 134,
    Size8x6CutPaper = 135,
    Size8x8CutPaper = 136,
    Size8x4x2CutPaper = 137,
    Size8x5x2CutPaper = 138,
    Size8x6x2CutPaper = 139,
    Size8x4x3CutPaper = 153,
    Size8x10DuplexFront = 231,
    Size8x12DuplexFront = 232,
    Size8x4DuplexFront = 233,
    Size8x5DuplexFront = 234,
    Size8x6DuplexFront = 235,
    Size8x8DuplexFront = 236,
    Size8x4x2DuplexFront = 237,
    Size8x5x2DuplexFront = 238,
    Size8x6x2DuplexFront = 239,
    Size8x4x3DuplexFront = 253,
    Size8x10DuplexBack = 331,
    Size8x12DuplexBack = 332,
    Size8x4DuplexBack = 333,
    Size8x5DuplexBack = 334,
    Size8x6DuplexBack = 335,
    Size8x8DuplexBack = 336,
    Size8x4x2DuplexBack = 337,
    Size8x5x2DuplexBack = 338,
    Size8x6x2DuplexBack = 339,
    Size8x4x3DuplexBack = 353,
}

/// <summary>Instantané d'une imprimante DNP.</summary>
/// <param name="PortNumber">Numéro de port USB, tel que renvoyé par la découverte.</param>
/// <param name="SerialNumber">Numéro de série de la machine.</param>
/// <param name="FirmwareVersion">Version du micrologiciel.</param>
/// <param name="Status">État décodé.</param>
/// <param name="MediaRemaining">Tirages restants sur le rouleau.</param>
/// <param name="MediaInitialCount">Capacité initiale du rouleau ; 0 si inconnue.</param>
/// <param name="MediaSize">Format chargé.</param>
/// <param name="MediaClass">Classe de média (RFID).</param>
/// <param name="QueuedPrints">Tirages en file dans la mémoire de l'imprimante.</param>
/// <param name="LifetimePrints">Compteur total de tirages de la machine.</param>
/// <param name="WindowsQueueName">
/// File d'impression Windows correspondante, quand la machine n'est connue QUE par elle.
/// Voir <see cref="EndormieOuInjoignable"/>.
/// </param>
public sealed record DnpPrinterInfo(
    int PortNumber,
    string SerialNumber,
    string FirmwareVersion,
    DnpStatus Status,
    int MediaRemaining,
    int MediaInitialCount,
    DnpMediaSize MediaSize,
    DnpMediaClass MediaClass,
    int QueuedPrints,
    int LifetimePrints,
    string? WindowsQueueName = null,
    EtatSpouleurDnp? Spouleur = null)
{
    /// <summary>Pourcentage de rouleau restant, ou <c>null</c> si la capacité initiale est inconnue.</summary>
    public double? MediaRemainingPercent =>
        MediaInitialCount > 0 ? 100.0 * MediaRemaining / MediaInitialCount : null;

    /// <summary>
    /// La machine existe pour Windows mais ne répond pas au SDK.
    ///
    /// C'est l'état d'une DS620 en veille prolongée : elle ne disparaît pas du bus USB,
    /// mais <c>CPPCtrl32.dll</c> ne la découvre plus et rend un échec de communication sur
    /// tous les rangs (constaté le 03/08/2026). Sans ce cas, l'imprimante disparaissait
    /// simplement du bandeau — l'opérateur ne pouvait pas distinguer « endormie » de
    /// « débranchée », ni savoir qu'un tirage la réveillerait.
    ///
    /// <b>Ce n'est PAS « en veille ».</b> Le SDK est aussi muet quand DiLand tient le port
    /// USB — c'est-à-dire presque toujours en boutique. Ce que la machine fait vraiment se
    /// lit alors dans <see cref="Spouleur"/> ; c'est lui qu'il faut afficher.
    /// </summary>
    public bool EndormieOuInjoignable => WindowsQueueName is not null;

    /// <summary>
    /// Vrai quand le spouleur affirme que la machine est là — prête, en train de tirer, ou
    /// en panne. Dans ce cas, « en veille » est faux et ne doit pas être affiché.
    /// </summary>
    public bool VueParLeSpouleur =>
        Spouleur is not null && Spouleur.Etat != EtatFileDnp.Inconnu;
}
