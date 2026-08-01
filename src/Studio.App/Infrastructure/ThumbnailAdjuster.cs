using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.App.Infrastructure;

/// <summary>
/// Applique les corrections à une image d'écran, pour que la grille et l'aperçu montrent
/// les photos telles qu'elles sortiront.
///
/// Sans cela, l'opérateur corrigerait à l'aveugle : le réglage part sur toutes les photos
/// cochées, pas seulement sur celle qu'il regarde.
///
/// <b>ImageMagick n'intervient plus du tout ici.</b> La version précédente encodait en PNG,
/// laissait ImageMagick le décoder, ré-encodait en PNG et laissait WPF le redécoder :
/// quatre compressions pour un simple curseur. On a d'abord supprimé les codecs, en
/// passant les octets en direct — il restait la construction d'une image Magick, sa
/// correction à un seul fil, et la relecture de ses pixels. Depuis
/// <see cref="PixelCorrections"/>, les octets de WPF se corrigent sur place : une copie à
/// l'aller, une passe en parallèle, rien d'autre.
///
/// Les trois corrections automatiques font exception : elles demandent de mesurer l'image
/// entière et restent le travail d'ImageMagick. Une vignette qui en dépend passe donc par
/// le chemin complet — mais ce sont des bascules, pas des curseurs, et l'opérateur ne les
/// tire pas soixante fois par seconde.
/// </summary>
public static class ThumbnailAdjuster
{
    public static BitmapSource Apply(BitmapSource source, ImageAdjustments adjustments)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adjustments);

        if (adjustments.IsNeutral) return source;

        try
        {
            var largeur = source.PixelWidth;
            var hauteur = source.PixelHeight;
            if (largeur <= 0 || hauteur <= 0) return source;

            // BGRA quoi qu'il arrive : c'est le seul format dont on connaisse la
            // disposition des octets des deux côtés
            var bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var octets = new byte[largeur * hauteur * 4];
            bgra.CopyPixels(octets, largeur * 4, 0);

            if (DemandeUnPassageParMagick(adjustments))
                octets = ParMagick(octets, largeur, hauteur, adjustments);

            var disposition = PixelCorrections.Disposition.Bgra;
            PixelCorrections.AppliquerPoints(octets, largeur, hauteur, disposition, adjustments);
            PixelCorrections.AppliquerRelief(octets, largeur, hauteur, disposition, adjustments);

            // la définition d'écran est conservée : la changer ferait sauter la taille
            // d'affichage de la vignette d'un rendu à l'autre
            var rendu = new WriteableBitmap(largeur, hauteur, source.DpiX, source.DpiY,
                PixelFormats.Bgra32, null);
            rendu.WritePixels(new Int32Rect(0, 0, largeur, hauteur), octets, largeur * 4, 0);
            rendu.Freeze();

            return rendu;
        }
        catch (Exception e)
        {
            // une vignette non corrigée vaut mieux qu'une grille vide : le tirage, lui,
            // repasse de toute façon par le pipeline complet
            FileLog.Write("Vignette : correction impossible", e);
            return source;
        }
    }

    /// <summary>
    /// Le noir et blanc et les trois automatismes : tout ce qui précède les réglages fins
    /// et qu'ImageMagick est seul à savoir faire.
    /// </summary>
    private static bool DemandeUnPassageParMagick(ImageAdjustments a) =>
        a.Grayscale || a.AutoLevels || a.AutoContrast || a.AutoColor;

    private static byte[] ParMagick(
        byte[] octets, int largeur, int hauteur, ImageAdjustments a)
    {
        MagickInit.Configure();

        var lecture = new ImageMagick.PixelReadSettings(
            (uint)largeur, (uint)hauteur,
            ImageMagick.StorageType.Char, ImageMagick.PixelMapping.BGRA);

        using var image = new ImageMagick.MagickImage(octets, lecture);

        if (a.Grayscale)
            image.Grayscale(ImageMagick.PixelIntensityMethod.Rec709Luma);

        if (a.AutoColor && !a.Grayscale) image.WhiteBalance();
        if (a.AutoLevels) image.AutoLevel();
        if (a.AutoContrast) image.Normalize();

        // le noir et blanc laisse l'image sur un seul canal : sans ce retour en sRGB, la
        // relecture BGRA n'aurait plus les canaux qu'elle attend
        if (image.ColorSpace != ImageMagick.ColorSpace.sRGB)
            image.ColorSpace = ImageMagick.ColorSpace.sRGB;
        image.Alpha(ImageMagick.AlphaOption.Opaque);

        return image.GetPixels().ToByteArray(ImageMagick.PixelMapping.BGRA) ?? octets;
    }
}
