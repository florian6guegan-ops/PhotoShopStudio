using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// L'aller-retour des pixels d'écran vers ImageMagick, sans passer par un codec.
///
/// C'est le chemin que prend <c>ThumbnailAdjuster</c> depuis le 01/08/2026 : les
/// corrections encodaient et décodaient quatre fois en PNG pour un simple curseur, ce qui
/// rendait l'écran « Corriger » interminable. En copiant les octets on gagne tout ça, mais
/// on prend deux risques qui ne se voient pas à la compilation :
/// l'ordre des canaux (bleu et rouge inversés donnent une photo bleutée), et le noir et
/// blanc qui laisse l'image sur un seul canal. Les deux sont vérifiés ici.
///
/// Le contrôle porte sur la partie ImageMagick, la seule qui puisse surprendre ; la copie
/// WPF de part et d'autre est du <c>CopyPixels</c> standard.
/// </summary>
public class PixelRoundTripTests
{
    private const int Cote = 4;

    /// <summary>Une image d'une seule couleur, en octets BGRA comme les rend WPF.</summary>
    private static byte[] Bgra(byte bleu, byte vert, byte rouge)
    {
        var octets = new byte[Cote * Cote * 4];
        for (var i = 0; i < octets.Length; i += 4)
        {
            octets[i] = bleu;
            octets[i + 1] = vert;
            octets[i + 2] = rouge;
            octets[i + 3] = 255;
        }
        return octets;
    }

    /// <summary>Le trajet exact de ThumbnailAdjuster : octets BGRA → correction → octets BGRA.</summary>
    private static byte[] Traiter(byte[] bgra, ImageAdjustments reglages)
    {
        MagickInit.Configure();

        var lecture = new PixelReadSettings(Cote, Cote, StorageType.Char, PixelMapping.BGRA);
        using var image = new MagickImage(bgra, lecture);

        ImageAdjuster.Apply(image, reglages);

        if (image.ColorSpace != ColorSpace.sRGB) image.ColorSpace = ColorSpace.sRGB;
        if (!image.HasAlpha) image.Alpha(AlphaOption.Opaque);

        var sortie = image.GetPixels().ToByteArray(PixelMapping.BGRA);
        Assert.NotNull(sortie);
        return sortie!;
    }

    /// <summary>
    /// Le piège du chemin direct : si les canaux étaient relus dans l'autre sens, un ciel
    /// bleu ressortirait rouge — et rien ne le signalerait avant que la photo soit à l'écran.
    /// </summary>
    [Fact]
    public void Le_bleu_reste_bleu()
    {
        var sortie = Traiter(Bgra(220, 40, 20), new ImageAdjustments { Exposure = 0.5 });

        var (bleu, vert, rouge) = (sortie[0], sortie[1], sortie[2]);
        Assert.True(bleu > rouge, $"canaux inversés : bleu={bleu} rouge={rouge}");
        Assert.True(bleu > vert, $"canaux inversés : bleu={bleu} vert={vert}");
    }

    [Fact]
    public void Le_rouge_reste_rouge()
    {
        var sortie = Traiter(Bgra(20, 40, 220), new ImageAdjustments { Exposure = 0.5 });

        Assert.True(sortie[2] > sortie[0], $"canaux inversés : rouge={sortie[2]} bleu={sortie[0]}");
    }

    /// <summary>
    /// Le noir et blanc laisse l'image en niveaux de gris, sur un seul canal : sans le
    /// retour en sRGB, la relecture BGRA n'aurait plus les canaux qu'elle attend.
    /// </summary>
    [Fact]
    public void Le_noir_et_blanc_ressort_sur_trois_canaux_egaux()
    {
        var sortie = Traiter(Bgra(220, 40, 20), new ImageAdjustments { Grayscale = true });

        Assert.Equal(Cote * Cote * 4, sortie.Length);
        Assert.Equal(sortie[0], sortie[1]);
        Assert.Equal(sortie[1], sortie[2]);
        Assert.Equal(255, sortie[3]); // opaque : une photo à demi transparente serait un défaut
    }

    /// <summary>La correction doit agir : un aller-retour qui ne change rien ne prouve rien.</summary>
    [Fact]
    public void La_correction_agit_bien_sur_les_pixels()
    {
        var depart = Bgra(120, 120, 120);
        var sortie = Traiter(depart, new ImageAdjustments { Exposure = 1 });

        Assert.True(sortie[0] > depart[0], $"l'exposition n'a rien fait : {depart[0]} → {sortie[0]}");
    }
}
