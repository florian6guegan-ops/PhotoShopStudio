using ImageMagick;
using Studio.Core.Domain;

namespace Studio.Imaging;

/// <summary>
/// Applique les corrections à une image : la courbe de tons d'un côté, la couleur et le
/// relief de l'autre.
///
/// L'ordre suit celui d'un développement photo, et il compte : on cale d'abord la
/// lumière, puis la couleur sur cette base, et le relief en dernier — accentuer avant de
/// remonter les ombres ferait ressortir le bruit du capteur.
///
/// <b>Le calcul lui-même n'est plus celui d'ImageMagick</b>, mais celui de
/// <see cref="PixelCorrections"/> : Magick.NET est mono-fil sur ce poste, et faisait une
/// traversée complète de l'image par réglage. On ne lui laisse ici que ce qu'il est seul à
/// savoir faire — le noir et blanc, qui change l'espace colorimétrique, et les trois
/// automatismes, qui demandent de mesurer l'image avant de la corriger.
/// </summary>
public static class ImageAdjuster
{
    /// <param name="avecRelief">
    /// Faux pour sauter la clarté et la netteté.
    ///
    /// Ce n'est plus une béquille de vitesse : depuis <see cref="PixelCorrections"/> le
    /// relief coûte quelques millisecondes et l'aperçu l'applique toujours. Le paramètre
    /// reste pour les usages où l'on veut la couleur sans le relief — une vignette de
    /// planche, où l'accentuation ne se verrait pas.
    /// </param>
    public static void Apply(IMagickImage<byte> image, ImageAdjustments a, bool avecRelief = true)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(a);

        if (a.IsNeutral) return;

        // Le fond blanc AVANT tout le reste : il raisonne sur les couleurs d'origine.
        // Après une désaturation ou un coup de contraste, le fond ne ressemblerait plus à
        // ce que le pourtour a mesuré, et la découpe partirait de travers.
        if (a.WhiteBackground && image is MagickImage photo)
            BackgroundRemoval.PoserUnFondBlanc(photo);

        // Les yeux rouges AVANT le noir et blanc, et avant tout réglage de couleur : la
        // correction reconnaît une pupille au ROUGE qui y domine, et une image désaturée ou
        // recontrastée ne la lui montrerait plus. Après le fond blanc, en revanche, qui ne
        // touche jamais au visage.
        if (a.RedEye)
            YeuxRouges.Appliquer(image);

        // le noir et blanc ensuite : les réglages de couleur qui suivent n'auraient
        // plus de sens une fois l'image désaturée.
        //
        // Rec709Luma et non Rec709Luminance : la seconde travaille en lumière linéaire et
        // laisse l'image dans un espace linéaire, où la courbe de tons qui suit n'agirait
        // plus comme sur une photo couleur. Tout le pipeline est en sRGB, on y reste.
        if (a.Grayscale)
            image.Grayscale(PixelIntensityMethod.Rec709Luma);

        ApplyAuto(image, a);

        // le reste — tons, couleur, relief — se calcule sur les octets, en parallèle
        SurLesOctets(image, octets => Corriger(octets, image, a, avecRelief));
    }

    /// <summary>
    /// Les trois corrections automatiques de DiLand, appliquées avant les réglages fins.
    ///
    /// Elles restent à ImageMagick : chacune commence par mesurer l'image entière —
    /// histogramme, extrema, moyenne par canal — et c'est un travail qu'il fait bien. Elles
    /// sont d'ailleurs des bascules, cochées une fois : leur coût ne se paie pas à chaque
    /// mouvement de curseur.
    ///
    /// L'ordre entre elles n'est pas indifférent : la dominante se neutralise d'abord,
    /// sinon l'étirement des niveaux la fige en l'amplifiant canal par canal. Le contraste
    /// vient en dernier, sur une image déjà juste en couleur.
    /// </summary>
    private static void ApplyAuto(IMagickImage<byte> image, ImageAdjustments a)
    {
        // sur une image déjà désaturée, corriger une dominante n'a plus d'objet
        if (a.AutoColor && !a.Grayscale)
            image.WhiteBalance();

        // « niveaux » : chaque canal est étiré sur toute la plage
        if (a.AutoLevels)
            image.AutoLevel();

        // « contraste » : on étire la luminosité sans redistribuer les canaux entre eux,
        // ce qui préserve l'équilibre des couleurs — c'est ce qui le distingue des niveaux
        if (a.AutoContrast)
            image.Normalize();
    }

    private static void Corriger(
        byte[] octets, IMagickImage<byte> image, ImageAdjustments a, bool avecRelief)
    {
        var largeur = (int)image.Width;
        var hauteur = (int)image.Height;
        var disposition = Disposition(image);

        PixelCorrections.AppliquerPoints(octets, largeur, hauteur, disposition, a);

        if (avecRelief)
            PixelCorrections.AppliquerRelief(octets, largeur, hauteur, disposition, a);
    }

    /// <summary>
    /// Où sont les canaux dans ce que rend ImageMagick.
    ///
    /// Une image passée en noir et blanc n'en a plus qu'un, et l'interroger comme une
    /// image couleur renverrait du vert et du bleu à zéro — donc une photo noire.
    /// </summary>
    private static PixelCorrections.Disposition Disposition(IMagickImage<byte> image)
    {
        var canaux = (int)image.ChannelCount;

        return image.ColorSpace == ColorSpace.Gray
            ? PixelCorrections.Disposition.Gris(canaux)
            : PixelCorrections.Disposition.Rvb(canaux);
    }

    /// <summary>
    /// Sort les octets de l'image, laisse les corriger, les remet.
    ///
    /// La lecture suit la disposition native d'ImageMagick — R, V, B puis l'alpha, ou le
    /// seul canal d'une image grise — pour que la réécriture retombe exactement sur les
    /// mêmes cases. Passer par du BGRA obligerait à reconstruire l'image, ce qui lui
    /// coûterait son espace colorimétrique : le noir et blanc redeviendrait une image
    /// couleur dont les trois canaux sont égaux, et le tirage n'irait plus au même papier.
    ///
    /// Un espace exotique (CMJN d'un fichier venu d'un imprimeur) est d'abord ramené en
    /// sRGB : tout le pipeline y travaille, et corriger canal par canal du CMJN
    /// inverserait les réglages.
    /// </summary>
    private static void SurLesOctets(IMagickImage<byte> image, Action<byte[]> corriger)
    {
        if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Gray))
            image.ColorSpace = ColorSpace.sRGB;

        var canaux = (int)image.ChannelCount;
        var carte = image.ColorSpace == ColorSpace.Gray ? "R" : "RGB";
        if (canaux == carte.Length + 1) carte += "A";

        if (canaux != carte.Length)
        {
            // disposition inattendue : plutôt que d'écrire n'importe où, on repasse par
            // une image sRGB dont on connaît la forme
            image.ColorSpace = ColorSpace.sRGB;
            image.Alpha(AlphaOption.Off);
            canaux = (int)image.ChannelCount;
            carte = "RGB";
            if (canaux != 3) return;
        }

        using var pixels = image.GetPixels();

        var octets = pixels.ToByteArray(carte);
        if (octets is null) return;

        corriger(octets);

        pixels.SetPixels(octets);
    }
}
