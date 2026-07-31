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

/// <summary>Identité et état d'un magasin (rouleau) chargé dans une machine.</summary>
public sealed record De100Magazine(int Index, string MagazineType, double PaperWidthMm, double PaperHeightMm);

/// <summary>Instantané d'une machine DE100.</summary>
public sealed record De100PrinterInfo(
    char MachineId,
    De100PrinterStatus Status,
    string RegistrationNumber,
    IReadOnlyList<De100Magazine> Magazines);
