using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ImageMagick;
using Studio.Imaging;

namespace Studio.Printing.LargeFormat;

/// <summary>
/// La conversion ICC de l'agrandissement : du profil du document vers celui du couple
/// papier/encre, avec le mode de rendu et la compensation du point noir choisis.
///
/// <b>Pourquoi ce fichier existe.</b> La boîte d'agrandissement proposait « Profil de
/// l'imprimante », « Mode de rendu » et « Compensation du point noir » depuis le début, et
/// <see cref="LargeFormatPrinter"/> n'en faisait RIEN : les trois réglages étaient lus dans
/// <see cref="LargeFormatPrintSettings"/> puis jetés, l'impression se contentant d'un
/// <c>Graphics.DrawImage</c>. L'opérateur croyait donc régler sa colorimétrie et tirait, à
/// chaque fois, la même image non convertie.
///
/// <b>Ce que la conversion produit.</b> Une image dont les nombres ne sont plus du sRGB mais
/// des valeurs DU PÉRIPHÉRIQUE. Elle doit donc partir vers une imprimante dont la gestion des
/// couleurs est DÉSACTIVÉE dans le pilote, sans quoi elle est convertie deux fois. C'est le
/// flux de Photoshop, et c'est ce que dit déjà l'avertissement de
/// <see cref="LargeFormatPrintSettings.Validate"/>.
/// </summary>
public static class IccTransform
{
    /// <summary>
    /// Convertit <paramref name="source"/> vers le profil du papier.
    /// </summary>
    /// <param name="source">L'agrandissement, dans l'espace de son profil de document.</param>
    /// <param name="documentProfile">
    /// Profil ICC incorporé au fichier, s'il y en a un. Null = sRGB, la convention de l'atelier.
    /// </param>
    /// <param name="printerProfilePath">Fichier .icc/.icm du couple papier/encre.</param>
    /// <param name="intent">Mode de rendu demandé.</param>
    /// <param name="blackPointCompensation">Compensation du point noir.</param>
    /// <returns>Une nouvelle image, à la charge de l'appelant de la libérer.</returns>
    public static Bitmap Apply(
        Bitmap source, byte[]? documentProfile, string printerProfilePath,
        RenderingIntent intent, bool blackPointCompensation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(printerProfilePath);

        if (!File.Exists(printerProfilePath))
            throw new FileNotFoundException(
                $"Profil ICC introuvable : « {printerProfilePath} ».", printerProfilePath);

        MagickInit.Configure();

        var cible = new ColorProfile(printerProfilePath);

        // Un profil CMJN ne mène nulle part ici : l'impression passe par GDI+, qui ne sait
        // envoyer que du RVB et reconvertirait tout. Mieux vaut le dire que tirer du faux.
        if (cible.ColorSpace is ColorSpace.CMYK)
            throw new InvalidOperationException(
                $"« {Path.GetFileName(printerProfilePath)} » est un profil CMJN. " +
                "Les agrandissements passent en RVB : choisissez le profil RVB du papier " +
                "(les profils Epson SC-P800 le sont tous).");

        var (pixels, largeur, hauteur) = LirePixels(source);

        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings(
            (uint)largeur, (uint)hauteur, StorageType.Char, PixelMapping.BGR));

        image.RenderingIntent = Traduire(intent);

        // La PROPRIÉTÉ, et non l'artefact « profile:black-point-compensation » : celui-ci est
        // accepté sans broncher et n'a aucun effet — vérifié sur le profil Canvas Matte de
        // la SC-P800, où les ombres sortaient identiques dans les deux cas. La propriété, elle,
        // rouvre bien les valeurs sous 20 que la colorimétrie relative écrasait à zéro.
        image.BlackPointCompensation = blackPointCompensation;

        var depart = documentProfile is { Length: > 0 }
            ? new ColorProfile(documentProfile)
            : ColorProfiles.SRGB;

        // Quantum, et surtout PAS HighRes.
        //
        // HighRes avait été retenu en supposant qu'il évitait des marches dans les dégradés.
        // Mesuré sur une photo de 39 Mpx et le profil Premium Luster : HighRes met
        // 13 536 ms, Quantum 1 459 ms — neuf fois plus — pour un écart maximal de DEUX
        // niveaux sur 255, sur une photo réelle comme sur un dégradé noir-blanc. Deux
        // niveaux sont invisibles à l'œil et très en dessous du bruit d'une jet d'encre.
        // C'est ce seul appel qui rendait « Imprimer » interminable.
        image.TransformColorSpace(depart, cible, ColorTransformMode.Quantum);

        return EcrirePixels(image, largeur, hauteur);
    }

    /// <summary>Décrit la conversion pour le journal d'impression.</summary>
    public static string Describe(string printerProfilePath, RenderingIntent intent, bool bpc) =>
        $"profil « {Path.GetFileName(printerProfilePath)} », {Nommer(intent)}" +
        (bpc ? ", compensation du point noir" : "");

    private static ImageMagick.RenderingIntent Traduire(RenderingIntent intent) => intent switch
    {
        RenderingIntent.Perceptual => ImageMagick.RenderingIntent.Perceptual,
        RenderingIntent.Saturation => ImageMagick.RenderingIntent.Saturation,
        RenderingIntent.AbsoluteColorimetric => ImageMagick.RenderingIntent.Absolute,
        _ => ImageMagick.RenderingIntent.Relative,
    };

    private static string Nommer(RenderingIntent intent) => intent switch
    {
        RenderingIntent.Perceptual => "perception",
        RenderingIntent.Saturation => "saturation",
        RenderingIntent.AbsoluteColorimetric => "colorimétrie absolue",
        _ => "colorimétrie relative",
    };

    /// <summary>
    /// Les pixels du bitmap en BGR compact, sans le remplissage de fin de ligne.
    ///
    /// GDI+ aligne chaque ligne sur quatre octets : passer la mémoire telle quelle à
    /// ImageMagick, qui n'attend aucun remplissage, décale l'image d'une ligne à l'autre et
    /// la fait partir en biais.
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) LirePixels(Bitmap source)
    {
        var largeur = source.Width;
        var hauteur = source.Height;

        // on repasse par un format connu : une image indexée ou 16 bits n'a pas la même
        // disposition mémoire, et il n'y a pas lieu de multiplier les cas
        Bitmap? copie = null;
        var lu = source;
        if (source.PixelFormat != PixelFormat.Format24bppRgb)
        {
            copie = new Bitmap(largeur, hauteur, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(copie))
                g.DrawImageUnscaled(source, 0, 0);
            lu = copie;
        }

        try
        {
            var zone = new Rectangle(0, 0, largeur, hauteur);
            var data = lu.LockBits(zone, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var ligne = largeur * 3;
                var pixels = new byte[(long)ligne * hauteur];

                for (var y = 0; y < hauteur; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * ligne, ligne);

                return (pixels, largeur, hauteur);
            }
            finally
            {
                lu.UnlockBits(data);
            }
        }
        finally
        {
            copie?.Dispose();
        }
    }

    private static Bitmap EcrirePixels(IMagickImage<byte> image, int largeur, int hauteur)
    {
        using var pixels = image.GetPixels();
        var octets = pixels.ToByteArray(PixelMapping.BGR)
                     ?? throw new InvalidOperationException("La conversion ICC n'a rien rendu.");

        var sortie = new Bitmap(largeur, hauteur, PixelFormat.Format24bppRgb);
        var zone = new Rectangle(0, 0, largeur, hauteur);
        var data = sortie.LockBits(zone, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var ligne = largeur * 3;
            for (var y = 0; y < hauteur; y++)
                Marshal.Copy(octets, y * ligne, data.Scan0 + y * data.Stride, ligne);
        }
        finally
        {
            sortie.UnlockBits(data);
        }

        return sortie;
    }
}
