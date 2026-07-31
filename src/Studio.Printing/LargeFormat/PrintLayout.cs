namespace Studio.Printing.LargeFormat;

/// <summary>Unité affichée dans la boîte d'impression, comme le sélecteur « Unités » de Photoshop.</summary>
public enum PrintUnits
{
    Millimeters,
    Centimeters,
    Inches,
}

/// <summary>
/// Qui convertit les couleurs vers l'espace de l'imprimante — le choix « Traitement des
/// couleurs » de Photoshop. Se tromper ici donne une double conversion et des couleurs fausses.
/// </summary>
public enum ColorHandling
{
    /// <summary>« Laisser l'imprimante gérer les couleurs » : on envoie l'image telle quelle.</summary>
    PrinterManagesColor,

    /// <summary>« Laisser Studio gérer les couleurs » : conversion vers le profil du papier avant envoi.</summary>
    ApplicationManagesColor,
}

/// <summary>Mode de rendu ICC (« Mode de rendu » de Photoshop).</summary>
public enum RenderingIntent
{
    Perceptual,
    RelativeColorimetric,
    Saturation,
    AbsoluteColorimetric,
}

/// <summary>
/// Emplacement calculé de l'image sur la feuille, en millimètres depuis le coin
/// supérieur gauche de la zone imprimable.
/// </summary>
/// <param name="LeftMm">Marge gauche.</param>
/// <param name="TopMm">Marge haute.</param>
/// <param name="WidthMm">Largeur du tirage.</param>
/// <param name="HeightMm">Hauteur du tirage.</param>
/// <param name="ScalePercent">Échelle réellement appliquée (peut différer de la demande si « ajuster au support »).</param>
/// <param name="EffectiveDpi">Résolution d'impression obtenue — c'est le « Résolution d'impr. » de Photoshop.</param>
public sealed record PrintPlacement(
    double LeftMm,
    double TopMm,
    double WidthMm,
    double HeightMm,
    double ScalePercent,
    double EffectiveDpi)
{
    /// <summary>Vrai si le tirage déborde de la feuille : Photoshop le signale, nous aussi.</summary>
    public bool OverflowsPaper(double paperWidthMm, double paperHeightMm, double toleranceMm = 0.5) =>
        LeftMm < -toleranceMm
        || TopMm < -toleranceMm
        || LeftMm + WidthMm > paperWidthMm + toleranceMm
        || TopMm + HeightMm > paperHeightMm + toleranceMm;
}

/// <summary>
/// Géométrie de la boîte « Position et taille » de Photoshop : échelle, ajustement au
/// support, centrage et décalages. Volontairement sans dépendance à l'interface pour
/// pouvoir être vérifiée au millimètre.
/// </summary>
public static class PrintLayout
{
    private const double MmPerInch = 25.4;

    /// <summary>Taille physique d'une image à 100 %, d'après sa résolution d'origine.</summary>
    public static (double WidthMm, double HeightMm) NaturalSizeMm(int widthPx, int heightPx, double sourceDpi)
    {
        if (widthPx <= 0 || heightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthPx), "Les dimensions de l'image doivent être positives.");
        if (sourceDpi <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceDpi), "La résolution doit être positive.");

        return (widthPx / sourceDpi * MmPerInch, heightPx / sourceDpi * MmPerInch);
    }

    /// <summary>
    /// Calcule l'emplacement du tirage.
    /// </summary>
    /// <param name="widthPx">Largeur de l'image en pixels.</param>
    /// <param name="heightPx">Hauteur de l'image en pixels.</param>
    /// <param name="sourceDpi">Résolution d'origine de l'image.</param>
    /// <param name="paperWidthMm">Largeur de la zone imprimable.</param>
    /// <param name="paperHeightMm">Hauteur de la zone imprimable.</param>
    /// <param name="scalePercent">Échelle demandée ; ignorée si <paramref name="fitToMedia"/>.</param>
    /// <param name="fitToMedia">« Ajuster au support » : agrandit ou réduit pour tenir dans la feuille.</param>
    /// <param name="center">« Centre » : ignore <paramref name="topMm"/> et <paramref name="leftMm"/>.</param>
    /// <param name="topMm">Décalage depuis le haut, si non centré.</param>
    /// <param name="leftMm">Décalage depuis la gauche, si non centré.</param>
    public static PrintPlacement Compute(
        int widthPx,
        int heightPx,
        double sourceDpi,
        double paperWidthMm,
        double paperHeightMm,
        double scalePercent = 100,
        bool fitToMedia = false,
        bool center = true,
        double topMm = 0,
        double leftMm = 0)
    {
        if (paperWidthMm <= 0 || paperHeightMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(paperWidthMm), "Le format papier doit être positif.");
        if (!fitToMedia && scalePercent <= 0)
            throw new ArgumentOutOfRangeException(nameof(scalePercent), "L'échelle doit être strictement positive.");

        var (naturalW, naturalH) = NaturalSizeMm(widthPx, heightPx, sourceDpi);

        var appliedScale = fitToMedia
            ? 100 * Math.Min(paperWidthMm / naturalW, paperHeightMm / naturalH)
            : scalePercent;

        var printedW = naturalW * appliedScale / 100;
        var printedH = naturalH * appliedScale / 100;

        var left = center ? (paperWidthMm - printedW) / 2 : leftMm;
        var top = center ? (paperHeightMm - printedH) / 2 : topMm;

        // réduire l'image concentre les pixels : c'est la résolution réellement obtenue
        var effectiveDpi = sourceDpi * 100 / appliedScale;

        return new PrintPlacement(left, top, printedW, printedH, appliedScale, effectiveDpi);
    }

    /// <summary>Échelle qui fait tenir l'image dans la feuille, en pourcentage.</summary>
    public static double ScaleToFit(int widthPx, int heightPx, double sourceDpi,
        double paperWidthMm, double paperHeightMm)
    {
        var (naturalW, naturalH) = NaturalSizeMm(widthPx, heightPx, sourceDpi);
        return 100 * Math.Min(paperWidthMm / naturalW, paperHeightMm / naturalH);
    }

    /// <summary>Échelle nécessaire pour obtenir une largeur de tirage donnée.</summary>
    public static double ScaleForWidth(int widthPx, double sourceDpi, double targetWidthMm)
    {
        if (targetWidthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidthMm), "La largeur visée doit être positive.");
        var naturalW = widthPx / sourceDpi * MmPerInch;
        return 100 * targetWidthMm / naturalW;
    }

    /// <summary>Échelle nécessaire pour obtenir une hauteur de tirage donnée.</summary>
    public static double ScaleForHeight(int heightPx, double sourceDpi, double targetHeightMm)
    {
        if (targetHeightMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetHeightMm), "La hauteur visée doit être positive.");
        var naturalH = heightPx / sourceDpi * MmPerInch;
        return 100 * targetHeightMm / naturalH;
    }

    /// <summary>Conversion depuis les millimètres vers l'unité affichée.</summary>
    public static double FromMm(double millimeters, PrintUnits units) => units switch
    {
        PrintUnits.Millimeters => millimeters,
        PrintUnits.Centimeters => millimeters / 10,
        PrintUnits.Inches => millimeters / MmPerInch,
        _ => millimeters,
    };

    /// <summary>Conversion depuis l'unité affichée vers les millimètres.</summary>
    public static double ToMm(double value, PrintUnits units) => units switch
    {
        PrintUnits.Millimeters => value,
        PrintUnits.Centimeters => value * 10,
        PrintUnits.Inches => value * MmPerInch,
        _ => value,
    };

    /// <summary>Libellé de l'unité, pour l'affichage.</summary>
    public static string UnitSuffix(PrintUnits units) => units switch
    {
        PrintUnits.Millimeters => "mm",
        PrintUnits.Centimeters => "cm",
        PrintUnits.Inches => "pouces",
        _ => "",
    };
}
