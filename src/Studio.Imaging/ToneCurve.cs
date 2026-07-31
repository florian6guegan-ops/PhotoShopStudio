using Studio.Core.Domain;

namespace Studio.Imaging;

/// <summary>
/// Courbe de tons : rassemble en une seule fonction tous les réglages qui ne touchent
/// qu'à la luminosité — exposition, noirs, blancs, ombres, hautes lumières, contraste.
///
/// Les appliquer un par un à l'image ferait autant d'arrondis 8 bits, et les tons
/// finiraient par se casser en bandes dans les dégradés. On calcule donc la courbe une
/// fois en flottant, et l'image ne la traverse qu'une seule fois.
///
/// Fonction pure, donc vérifiable au point près sans rien rendre.
/// </summary>
public static class ToneCurve
{
    /// <summary>Amplitude maximale des points noir et blanc, en fraction de l'échelle.</summary>
    private const double PointRange = 0.2;

    /// <summary>
    /// Amplitude maximale de la récupération des ombres et des hautes lumières.
    ///
    /// La valeur n'est pas un goût : au-delà d'un tiers, la courbe redescend. Le terme des
    /// ombres vaut k(1−v)³, de dérivée 1−3k(1−v)² une fois ajouté à v ; elle ne reste
    /// positive partout que si 3k ≤ 1. Passer outre inverserait des tons entre eux dans
    /// les zones sombres — un dégradé y perdrait son sens de lecture.
    /// </summary>
    private const double RecoveryRange = 1.0 / 3.0;

    /// <summary>
    /// Applique la courbe à une valeur de luminance normalisée (0 = noir, 1 = blanc).
    /// Le résultat reste dans 0..1.
    /// </summary>
    public static double Apply(double v, ImageAdjustments a)
    {
        ArgumentNullException.ThrowIfNull(a);

        // exposition : multiplicative, comme un diaphragme — +1 IL double la lumière
        if (a.Exposure != 0) v *= Math.Pow(2, a.Exposure);

        // noirs et blancs : déplacent les points d'ancrage avant tout le reste, comme
        // un réglage de niveaux ; l'écart minimal évite une division par zéro
        if (a.Blacks != 0 || a.Whites != 0)
        {
            var noir = -(a.Blacks / 100.0) * PointRange;
            var blanc = 1 - (a.Whites / 100.0) * PointRange;
            if (blanc - noir < 0.2) blanc = noir + 0.2;
            v = (v - noir) / (blanc - noir);
        }

        v = Clamp(v);

        // ombres et hautes lumières : pondérées par la luminance, pour n'agir que là où
        // c'est utile — remonter les ombres ne doit pas délaver le reste de l'image
        if (a.Shadows != 0)
            v += a.Shadows / 100.0 * RecoveryRange * Math.Pow(1 - v, 3);

        if (a.Highlights != 0)
            v += a.Highlights / 100.0 * RecoveryRange * Math.Pow(v, 3);

        v = Clamp(v);

        // luminosité : gamma, donc noir et blanc restent en place — c'est ce qui la
        // distingue de l'exposition, qui elle peut brûler
        if (a.Brightness != 0)
            v = Math.Pow(v, 1 / Math.Pow(2, a.Brightness / 100.0));

        // contraste : courbe en S autour du gris moyen
        if (a.Contrast != 0)
        {
            var c = a.Contrast / 100.0;
            v = c > 0
                ? Melange(v, Adoucir(v), c)
                : Melange(v, AdoucirInverse(v), -c);
        }

        return Clamp(v);
    }

    /// <summary>Vrai si la courbe ne change rien, et peut donc être sautée entièrement.</summary>
    public static bool IsIdentity(ImageAdjustments a)
    {
        ArgumentNullException.ThrowIfNull(a);

        return a.Exposure == 0 && a.Brightness == 0 && a.Contrast == 0 &&
               a.Highlights == 0 && a.Shadows == 0 && a.Whites == 0 && a.Blacks == 0;
    }

    /// <summary>Table de correspondance sur <paramref name="taille"/> entrées, prête à appliquer à l'image.</summary>
    public static double[] BuildLut(ImageAdjustments a, int taille = 1024)
    {
        if (taille < 2) throw new ArgumentOutOfRangeException(nameof(taille));

        var lut = new double[taille];
        for (var i = 0; i < taille; i++)
            lut[i] = Apply((double)i / (taille - 1), a);

        return lut;
    }

    /// <summary>Courbe en S classique : ralentit près du noir et du blanc, accélère au milieu.</summary>
    private static double Adoucir(double v) => v * v * (3 - 2 * v);

    /// <summary>Réciproque de la courbe en S, pour aplatir le contraste.</summary>
    private static double AdoucirInverse(double v) =>
        0.5 - Math.Sin(Math.Asin(1 - 2 * Clamp(v)) / 3);

    private static double Melange(double a, double b, double part) => a + (b - a) * part;

    private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
