namespace Studio.Printing.Devices.Fuji;

/// <summary>Code de retour de toutes les fonctions <c>PIF_*</c> / <c>PHIF_*</c>.</summary>
public enum PifResult
{
    Ok = 0,
    Timeout = 1,
    BadParam = 2,
    FileIoError = 3,
    BadAlloc = 4,
    /// <summary>Le minilab a refusé la demande (souvent : file pleine, ou machine non prête).</summary>
    Reject = 5,
}

/// <summary>
/// État d'une commande côté minilab (<c>ST_ORDER_INFO.status</c>).
///
/// Les neuf valeurs comptent. Le driver de DiLand n'en traitait que deux
/// (<see cref="Complete"/> et <see cref="Canceled"/>), laissant les commandes
/// <see cref="Error"/>, <see cref="Hold"/> et <see cref="Busy"/> éternellement « en cours » —
/// d'où le renvoi en boucle. Voir <see cref="De100JobTracker"/>.
/// </summary>
public enum De100OrderStatus
{
    PrintWaiting = 0,
    Printing = 1,
    Complete = 2,
    Error = 3,
    ImageProcessWaiting = 4,
    ImageProcessing = 5,
    /// <summary>Commande suspendue à la machine ; ne repartira que sur action de l'opérateur.</summary>
    Hold = 6,
    Canceled = 7,
    /// <summary>Le minilab est occupé et n'a pas pris la commande en charge.</summary>
    Busy = 8,
}

/// <summary>État d'un tirage individuel (<c>ST_PRINT_INFO.status</c>). Numérotation distincte de <see cref="De100OrderStatus"/>.</summary>
public enum De100PrintStatus
{
    NotPrinting = 0,
    Complete = 2,
    Error = 3,
    Canceled = 4,
}

/// <summary>État global d'une machine, lu via la propriété <c>PrinterStatus</c>.</summary>
public enum De100PrinterStatus
{
    Offline = 0,
    Ready = 1,
    Busy = 2,
    Printing = 3,
    ErrorProcessingCannotBeContinued = 4,
    ErrorProcessingCanBeContinued = 5,
    Sleep = 6,
}

/// <summary>Gravité d'un <c>ST_EVENT_INFO</c>.</summary>
public enum De100ErrorLevel
{
    SystemError = 1,
    Error = 2,
    Warning = 3,
    SoftwareError = 4,
    Information = 5,
}

/// <summary>Finition de surface du papier (paramètre <c>Surface</c>).</summary>
public enum De100Surface
{
    Glossy = 1,
    Matte = 2,
    Lustre = 3,
    FineArtMatte = 4,
    Silk = 5,
    Pearl = 10,
    Sticker = 11,
    CanvasTexture = 12,
    TransferPaper = 13,
    PhotoMatte = 14,
    GlossyThick = 15,
}

/// <summary>
/// Noms de paramètres acceptés par <c>PIF_Print</c>. Ce sont ceux que DiLand envoie
/// réellement au DE100 ; toute autre orthographe est ignorée silencieusement.
/// </summary>
public static class De100ParamNames
{
    /// <summary>Identifiant numérique de commande, 1..65535, à faire tourner.</summary>
    public const string OrderId = "OrderID";

    /// <summary>Largeur du tirage en millimètres.</summary>
    public const string Width = "Width";

    /// <summary>Hauteur du tirage en millimètres.</summary>
    public const string Height = "Height";

    /// <summary>Espace colorimétrique ; DiLand envoie toujours « 1 ».</summary>
    public const string ColorSpace = "ColorSpace";

    /// <summary>Rotation en degrés ; DiLand envoie toujours « 0 » et fait tourner l'image lui-même.</summary>
    public const string Rotation = "Rotation";

    /// <summary>Valeur de <see cref="De100Surface"/>.</summary>
    public const string Surface = "Surface";

    /// <summary>Nombre d'exemplaires de cette image.</summary>
    public const string PrintNum = "PrintNum";

    /// <summary>Identifiant machine cible (un caractère).</summary>
    public const string OutputPrinter = "OutputPrinter";

    /// <summary>Nom du format tel que déclaré dans la configuration du minilab.</summary>
    public const string PrintSizeName = "PrintSizeName";

    /// <summary>« 1 » = haute qualité, « 0 » = normale.</summary>
    public const string Quality = "Quality";

    /// <summary>Mode colorimétrique commun, chaîne libre définie côté minilab.</summary>
    public const string CommonColorMode = "CommonColorMode";
}

/// <summary>Un tirage à envoyer au DE100.</summary>
/// <param name="JobId">Identifiant côté Studio, utilisé pour recoller le callback à l'enveloppe.</param>
/// <param name="ImagePath">Chemin du fichier image déjà rendu à la bonne taille.</param>
/// <param name="WidthMm">Largeur du tirage en millimètres.</param>
/// <param name="HeightMm">Hauteur du tirage en millimètres.</param>
/// <param name="PrintSizeName">Nom du format côté minilab (paramètre <c>PrintSizeName</c>).</param>
/// <param name="Surface">Finition du papier.</param>
/// <param name="Copies">Nombre d'exemplaires.</param>
/// <param name="HighQuality">Active le mode haute qualité.</param>
/// <param name="ColorMode">Mode colorimétrique commun ; vide = réglage par défaut de la machine.</param>
public sealed record De100PrintJob(
    string JobId,
    string ImagePath,
    double WidthMm,
    double HeightMm,
    string PrintSizeName,
    De100Surface Surface = De100Surface.Glossy,
    int Copies = 1,
    bool HighQuality = false,
    string ColorMode = "");

/// <summary>Le rouleau chargé dans la machine.</summary>
/// <param name="LoadingNumber">Numéro du magasin chargé, tel que déclaré par la machine.</param>
/// <param name="MagazineType">Type de magasin.</param>
/// <param name="PaperWidthMm">Largeur du rouleau : c'est elle qui détermine les formats tirables.</param>
/// <param name="PaperHeightMm">Hauteur déclarée, quand la machine en donne une.</param>
/// <param name="Surface">Finition du papier chargé.</param>
/// <param name="PaperRemainingMm">Longueur de papier restante, en millimètres.</param>
public sealed record De100Media(
    int LoadingNumber,
    string MagazineType,
    int PaperWidthMm,
    int PaperHeightMm,
    De100Surface Surface,
    double PaperRemainingMm)
{
    /// <summary>
    /// La machine ne dit pas combien de papier il reste.
    ///
    /// <b>Elle ne le MESURE pas, elle le DÉCOMPTE</b> à partir d'une longueur qu'on lui
    /// déclare au chargement du magasin. Tant que cette déclaration n'a pas été faite, elle
    /// décompte à partir de rien et annonce zéro — sur un rouleau neuf comme sur un rouleau
    /// fini. C'est l'état de la DE100-2 de la boutique, relevé le 06/08/2026 : encres,
    /// compteur, magasin et largeur se lisent parfaitement, seule la longueur vaut 0.
    ///
    /// L'écran affichait donc « 0 tirage restant » sur une machine prête avec un rouleau
    /// presque neuf. Un zéro qu'on ne sait pas expliquer ne doit pas se présenter comme une
    /// mesure : il faut dire qu'on ne sait pas, et où aller le déclarer.
    /// </summary>
    public bool LongueurNonDeclaree => PaperRemainingMm <= 0;
}

/// <summary>Niveau d'un consommable, en pourcentage quand la machine le donne ainsi.</summary>
/// <param name="Name">Libellé destiné à l'opérateur.</param>
/// <param name="Level">Niveau restant tel que renvoyé par la machine.</param>
public sealed record De100Supply(string Name, int Level);

/// <summary>
/// Consommables du DE100 : les quatre encres et la cartouche de maintenance.
/// L'ordre des encres est celui du SDK — 1 jaune, 2 magenta, 3 cyan, 4 noir.
/// </summary>
/// <param name="Yellow">Encre jaune.</param>
/// <param name="Magenta">Encre magenta.</param>
/// <param name="Cyan">Encre cyan.</param>
/// <param name="Black">Encre noire.</param>
/// <param name="MaintenanceTank">Remplissage du bac de maintenance.</param>
/// <param name="InkCount">Nombre d'encres déclaré par la machine.</param>
public sealed record De100Supplies(
    De100Supply Yellow,
    De100Supply Magenta,
    De100Supply Cyan,
    De100Supply Black,
    De100Supply MaintenanceTank,
    int InkCount)
{
    /// <summary>Les quatre encres, dans l'ordre du SDK.</summary>
    public IReadOnlyList<De100Supply> Inks => [Yellow, Magenta, Cyan, Black];

    /// <summary>Encres dont le niveau est au plus <paramref name="seuil"/>.</summary>
    public IEnumerable<De100Supply> InksBelow(int seuil) => Inks.Where(i => i.Level <= seuil);
}

/// <summary>Instantané complet d'une machine DE100 : état, papier, consommables, formats.</summary>
/// <param name="MachineId">Identifiant machine (un caractère).</param>
/// <param name="Status">État courant.</param>
/// <param name="RegistrationNumber">Numéro d'enregistrement.</param>
/// <param name="Model">Modèle, lu dans les informations d'installation.</param>
/// <param name="SerialNumber">Numéro de série.</param>
/// <param name="IpAddress">Adresse réseau de la machine.</param>
/// <param name="TotalPrintCount">Compteur de tirages depuis la mise en service.</param>
/// <param name="Media">Rouleau chargé, ou null si la machine n'en déclare pas.</param>
/// <param name="Supplies">Encres et bac de maintenance, ou null si indisponibles.</param>
/// <param name="Formats">Formats tirables avec le rouleau chargé, et tirages restants estimés.</param>
public sealed record De100PrinterInfo(
    char MachineId,
    De100PrinterStatus Status,
    string RegistrationNumber,
    string Model,
    string SerialNumber,
    string IpAddress,
    long TotalPrintCount,
    De100Media? Media,
    De100Supplies? Supplies,
    IReadOnlyList<De100FormatAvailability> Formats);
