namespace Studio.Imaging.Geometry;

/// <summary>
/// Le cadre d'un Polaroid, aux cotes du film 600 / i-Type.
///
/// <b>Les cotes sont celles publiées par Polaroid</b>, en pouces : le tirage complet fait
/// 3,483 × 4,233 po et la fenêtre image 3,108 × 3,024 po. En millimètres :
///
/// | | mm |
/// |---|---|
/// | tirage | 88,47 × 107,52 |
/// | fenêtre image | 78,94 × 76,80 |
/// | marge latérale et haute | 4,77 |
/// | **bande basse** | **25,95** |
///
/// Deux choses font le Polaroid, et aucune n'était rendue par l'ancien produit « POLA »
/// (une marge blanche uniforme de 2 mm) : la fenêtre est **presque carrée**, et la bande
/// du bas fait **près du quart de la hauteur**.
///
/// <b>Le cadre garde ses proportions et se centre dans le tirage.</b> Un 10×15 (102 × 152)
/// n'a pas le rapport d'un Polaroid (0,82 contre 0,67) : le cadre y occupe donc toute la
/// largeur et laisse du blanc en haut et en bas. C'est voulu — un cadre étiré pour remplir
/// la feuille ne serait plus un Polaroid, et c'est justement la forme qu'on cherche. Le
/// contour de découpe marque le vrai bord, à suivre aux ciseaux.
/// </summary>
public static class PolaroidFrame
{
    /// <summary>Largeur du tirage complet, film 600 / i-Type.</summary>
    public const double PrintWidthMm = 88.47;

    /// <summary>Hauteur du tirage complet, bande basse comprise.</summary>
    public const double PrintHeightMm = 107.52;

    /// <summary>Largeur de la fenêtre image.</summary>
    public const double WindowWidthMm = 78.94;

    /// <summary>Hauteur de la fenêtre image.</summary>
    public const double WindowHeightMm = 76.80;

    /// <summary>Marge blanche à gauche, à droite et en haut.</summary>
    public const double MarginMm = (PrintWidthMm - WindowWidthMm) / 2;

    /// <summary>Bande blanche du bas — celle sur laquelle on écrit.</summary>
    public const double BottomBandMm = PrintHeightMm - MarginMm - WindowHeightMm;

    /// <summary>Rapport largeur/hauteur du tirage complet (0,823).</summary>
    public static double PrintAspect => PrintWidthMm / PrintHeightMm;

    /// <summary>Rapport largeur/hauteur de la fenêtre image (1,028 — presque carrée).</summary>
    public static double WindowAspect => WindowWidthMm / WindowHeightMm;

    /// <param name="Frame">Le Polaroid lui-même : c'est là que passent les ciseaux.</param>
    /// <param name="Window">La fenêtre image, à l'intérieur du cadre.</param>
    public sealed record Layout(PixelRect Frame, PixelRect Window);

    /// <summary>
    /// Place le cadre dans un tirage de <paramref name="sheetWidthPx"/> ×
    /// <paramref name="sheetHeightPx"/> : le plus grand Polaroid qui y tienne, centré, et sa
    /// fenêtre image.
    /// </summary>
    public static Layout Place(int sheetWidthPx, int sheetHeightPx)
    {
        if (sheetWidthPx <= 0 || sheetHeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(sheetWidthPx),
                "Un tirage sans dimension ne peut pas porter de cadre.");

        // le cadre est limité par la largeur ou par la hauteur, selon le tirage
        var parLaLargeur = sheetWidthPx / PrintWidthMm;
        var parLaHauteur = sheetHeightPx / PrintHeightMm;
        var echelle = Math.Min(parLaLargeur, parLaHauteur);

        var cadreW = (int)Math.Round(PrintWidthMm * echelle);
        var cadreH = (int)Math.Round(PrintHeightMm * echelle);
        var cadreX = (sheetWidthPx - cadreW) / 2;
        var cadreY = (sheetHeightPx - cadreH) / 2;

        var fenetreW = (int)Math.Round(WindowWidthMm * echelle);
        var fenetreH = (int)Math.Round(WindowHeightMm * echelle);
        var marge = (int)Math.Round(MarginMm * echelle);

        // la fenêtre est centrée horizontalement sur le cadre : l'arrondi des deux marges
        // ne doit pas la décaler d'un pixel vers la gauche
        var fenetreX = cadreX + (cadreW - fenetreW) / 2;
        var fenetreY = cadreY + marge;

        return new Layout(
            new PixelRect(cadreX, cadreY, cadreW, cadreH),
            new PixelRect(fenetreX, fenetreY, fenetreW, fenetreH));
    }
}
