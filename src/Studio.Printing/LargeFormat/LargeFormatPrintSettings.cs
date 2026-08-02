namespace Studio.Printing.LargeFormat;

/// <summary>
/// Réglages de la boîte d'impression grand format, calquée sur celle de Photoshop.
///
/// Elle existe parce que les agrandissements ne se traitent pas comme un tirage de
/// borne : l'opérateur choisit le papier dans le pilote Epson, décide qui convertit les
/// couleurs, met à l'échelle et positionne l'image sur la feuille. C'est exactement ce
/// que fait l'atelier aujourd'hui dans Photoshop.
/// </summary>
public sealed class LargeFormatPrintSettings
{
    /// <summary>File d'impression Windows visée (l'Epson SC-P800 en pratique).</summary>
    public string PrinterName { get; set; } = "";

    public int Copies { get; set; } = 1;

    /// <summary>
    /// Réglages capturés dans la boîte du pilote (« Paramètres d'impression… ») : type de
    /// support, qualité, format papier, sans marges. Null = réglages par défaut du pilote.
    /// </summary>
    public byte[]? DevModeBytes { get; set; }

    /// <summary>Orientation de la feuille (« Disposition »).</summary>
    public bool Landscape { get; set; }

    // — gestion des couleurs —

    /// <summary>Profil de l'image source, pour information (« Profil du document »).</summary>
    public string DocumentProfile { get; set; } = "sRGB IEC61966-2.1";

    /// <summary>
    /// Le profil ICC réellement incorporé au fichier, s'il y en a un : c'est le point de
    /// DÉPART de la conversion. Null = sRGB, ce que l'atelier produit par défaut.
    /// </summary>
    public byte[]? DocumentProfileIcc { get; set; }

    public ColorHandling ColorHandling { get; set; } = ColorHandling.PrinterManagesColor;

    /// <summary>
    /// Profil ICC du couple papier/encre, utilisé seulement quand l'application gère les
    /// couleurs. CHEMIN COMPLET d'un fichier de <c>catalog/icc</c> ou du dossier couleur de
    /// Windows — un simple nom de fichier ne suffit pas à ouvrir le profil.
    /// </summary>
    public string? PrinterProfile { get; set; }

    public RenderingIntent RenderingIntent { get; set; } = RenderingIntent.RelativeColorimetric;

    public bool BlackPointCompensation { get; set; } = true;

    // — position et taille —

    public PrintUnits Units { get; set; } = PrintUnits.Centimeters;

    public double ScalePercent { get; set; } = 100;

    /// <summary>« Ajuster au support » : l'échelle est alors calculée, pas saisie.</summary>
    public bool FitToMedia { get; set; }

    public bool Center { get; set; } = true;

    public double TopMm { get; set; }

    public double LeftMm { get; set; }

    /// <summary>
    /// Trace un contour noir sur le bord du tirage, pour le recouper à la sortie.
    ///
    /// Un agrandissement posé sur une feuille plus grande sort entouré de blanc : rien ne dit
    /// alors où passer les ciseaux. Le trait est FIN — deux dixièmes de millimètre, à cheval sur
    /// le bord — pour que le coup de ciseaux le fasse disparaître ; un trait large laisserait un
    /// liseré noir sur la photo coupée. Même règle que les planches identité
    /// (<c>ImagePipeline.DrawCutBorders</c>).
    /// </summary>
    public bool CutBorder { get; set; }

    /// <summary>
    /// Vraie ou fausse combinaison ? Renvoie les problèmes qui empêcheraient d'imprimer,
    /// en clair pour l'opérateur. Liste vide = prêt à imprimer.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(PrinterName))
            problems.Add("Aucune imprimante sélectionnée.");

        if (Copies < 1)
            problems.Add("Le nombre de copies doit être d'au moins 1.");

        if (!FitToMedia && ScalePercent <= 0)
            problems.Add("L'échelle doit être supérieure à 0 %.");

        // le piège classique : l'imprimante convertit ET l'application convertit,
        // l'image ressort deux fois corrigée
        if (ColorHandling == ColorHandling.ApplicationManagesColor && string.IsNullOrWhiteSpace(PrinterProfile))
            problems.Add(
                "Vous avez choisi que Studio gère les couleurs : il faut alors indiquer le profil ICC " +
                "du papier, et penser à désactiver la gestion des couleurs dans le pilote de l'imprimante.");

        return problems;
    }

    /// <summary>Emplacement du tirage pour une image et un format papier donnés.</summary>
    public PrintPlacement ComputePlacement(int widthPx, int heightPx, double sourceDpi,
        double paperWidthMm, double paperHeightMm) =>
        PrintLayout.Compute(widthPx, heightPx, sourceDpi, paperWidthMm, paperHeightMm,
            ScalePercent, FitToMedia, Center, TopMm, LeftMm);

    /// <summary>Copie indépendante, pour pouvoir annuler les modifications de la boîte de dialogue.</summary>
    public LargeFormatPrintSettings Clone() => new()
    {
        PrinterName = PrinterName,
        Copies = Copies,
        DevModeBytes = DevModeBytes?.ToArray(),
        Landscape = Landscape,
        DocumentProfile = DocumentProfile,
        DocumentProfileIcc = DocumentProfileIcc?.ToArray(),
        ColorHandling = ColorHandling,
        PrinterProfile = PrinterProfile,
        RenderingIntent = RenderingIntent,
        BlackPointCompensation = BlackPointCompensation,
        Units = Units,
        ScalePercent = ScalePercent,
        FitToMedia = FitToMedia,
        Center = Center,
        TopMm = TopMm,
        LeftMm = LeftMm,
        CutBorder = CutBorder,
    };
}
