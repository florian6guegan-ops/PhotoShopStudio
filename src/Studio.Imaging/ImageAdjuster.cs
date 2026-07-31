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
/// </summary>
public static class ImageAdjuster
{
    /// <summary>Écart maximal appliqué aux canaux rouge et bleu par la température.</summary>
    private const double TemperatureRange = 0.30;

    /// <summary>Écart maximal appliqué au canal vert par la teinte.</summary>
    private const double TintRange = 0.20;

    public static void Apply(IMagickImage<byte> image, ImageAdjustments a)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(a);

        if (a.IsNeutral) return;

        // le noir et blanc d'abord : les réglages de couleur qui suivent n'auraient
        // plus de sens une fois l'image désaturée.
        //
        // Rec709Luma et non Rec709Luminance : la seconde travaille en lumière linéaire et
        // laisse l'image dans un espace linéaire, où la courbe de tons qui suit n'agirait
        // plus comme sur une photo couleur. Tout le pipeline est en sRGB, on y reste.
        if (a.Grayscale)
            image.Grayscale(PixelIntensityMethod.Rec709Luma);

        ApplyTone(image, a);

        if (!a.Grayscale)
        {
            ApplyWhiteBalance(image, a);
            ApplySaturation(image, a);
        }

        ApplyDetail(image, a);
    }

    /// <summary>
    /// Exposition, noirs, blancs, ombres, hautes lumières et contraste, en une seule
    /// traversée de l'image via une table de correspondance.
    /// </summary>
    private static void ApplyTone(IMagickImage<byte> image, ImageAdjustments a)
    {
        if (ToneCurve.IsIdentity(a)) return;

        using var table = BuildLutImage(a);
        image.Clut(table, PixelInterpolateMethod.Bilinear);
    }

    /// <summary>La courbe rendue sous forme d'image d'une ligne, que ImageMagick sait appliquer.</summary>
    private static MagickImage BuildLutImage(ImageAdjustments a, int taille = 1024)
    {
        var valeurs = ToneCurve.BuildLut(a, taille);

        var table = new MagickImage(MagickColors.Black, (uint)taille, 1);
        table.Alpha(AlphaOption.Off);   // sans quoi le nombre de canaux dépendrait de la source

        var pixels = table.GetPixels();
        var canaux = (int)table.ChannelCount;

        for (var i = 0; i < taille; i++)
        {
            var niveau = (byte)Math.Round(valeurs[i] * 255);
            var valeur = new byte[canaux];
            Array.Fill(valeur, niveau);
            pixels.SetPixel(i, 0, valeur);
        }

        return table;
    }

    /// <summary>
    /// Température et teinte, par pondération des canaux : réchauffer, c'est monter le
    /// rouge et descendre le bleu — exactement ce qu'il faut pour rattraper un intérieur
    /// éclairé à l'ampoule.
    /// </summary>
    private static void ApplyWhiteBalance(IMagickImage<byte> image, ImageAdjustments a)
    {
        if (a.Temperature != 0)
        {
            var t = a.Temperature / 100.0 * TemperatureRange;
            image.Evaluate(Channels.Red, EvaluateOperator.Multiply, 1 + t);
            image.Evaluate(Channels.Blue, EvaluateOperator.Multiply, 1 - t);
        }

        if (a.Tint != 0)
        {
            // vers le magenta on retire du vert, vers le vert on en ajoute
            var t = a.Tint / 100.0 * TintRange;
            image.Evaluate(Channels.Green, EvaluateOperator.Multiply, 1 - t);
        }
    }

    private static void ApplySaturation(IMagickImage<byte> image, ImageAdjustments a)
    {
        if (a.Saturation != 0)
            image.Modulate(new Percentage(100), new Percentage(100 + a.Saturation), new Percentage(100));

        if (a.Vibrance != 0)
            ApplyVibrance(image, a.Vibrance);
    }

    /// <summary>
    /// Vibrance : la saturation appliquée surtout là où l'image est terne.
    ///
    /// On sature fortement une copie, puis on la ramène à travers un masque tiré de la
    /// saturation existante — inversé, donc opaque sur les zones ternes et transparent sur
    /// les couleurs déjà vives. Un teint de peau, déjà saturé, est ainsi épargné, alors
    /// qu'une saturation globale le ferait virer à l'orange.
    /// </summary>
    private static void ApplyVibrance(IMagickImage<byte> image, double vibrance)
    {
        using var accentuee = image.Clone();
        accentuee.Modulate(new Percentage(100), new Percentage(100 + vibrance), new Percentage(100));

        using var masque = image.Clone();
        masque.ColorSpace = ColorSpace.HSL;
        using var saturation = masque.Separate(Channels.Green).First();
        saturation.Negate();   // opaque là où la couleur est terne

        accentuee.Alpha(AlphaOption.On);
        accentuee.Composite(saturation, CompositeOperator.CopyAlpha);

        image.Composite(accentuee, CompositeOperator.Over);
    }

    /// <summary>
    /// Clarté et netteté, toutes deux en masque flou : la clarté sur un large rayon donne
    /// du relief à la matière, la netteté sur un rayon serré détache les contours.
    ///
    /// Le rayon de la clarté suit la taille de l'image, sinon son effet dépendrait du
    /// format de tirage au lieu du sujet.
    /// </summary>
    private static void ApplyDetail(IMagickImage<byte> image, ImageAdjustments a)
    {
        if (a.Clarity != 0)
        {
            var sigma = Math.Max(4, Math.Max(image.Width, image.Height) / 200.0);
            image.UnsharpMask(0, sigma, a.Clarity / 100.0, 0.0);
        }

        if (a.Sharpness > 0)
            image.UnsharpMask(0, 1, a.Sharpness / 100.0 * 1.5, 0.02);
    }
}
