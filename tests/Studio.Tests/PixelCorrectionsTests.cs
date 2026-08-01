using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le cœur des corrections, calculé hors d'ImageMagick depuis le 01/08/2026.
///
/// <c>ImageAdjusterTests</c> vérifie déjà que chaque réglage agit dans le bon sens ; ici on
/// contrôle ce que le déplacement hors d'ImageMagick a mis en jeu, et qui ne se voit pas à
/// la compilation : la disposition des canaux, le parallélisme, et le fait qu'un noir et
/// blanc ne se remette pas à parler couleur.
/// </summary>
public class PixelCorrectionsTests
{
    /// <summary>Une image d'une seule couleur, en octets BGRA comme les rend WPF.</summary>
    private static byte[] Uni(int largeur, int hauteur, byte rouge, byte vert, byte bleu)
    {
        var octets = new byte[largeur * hauteur * 4];
        for (var i = 0; i < octets.Length; i += 4)
        {
            octets[i] = bleu;
            octets[i + 1] = vert;
            octets[i + 2] = rouge;
            octets[i + 3] = 255;
        }
        return octets;
    }

    private static (byte R, byte V, byte B) Lire(byte[] bgra, int index = 0)
    {
        var p = index * 4;
        return (bgra[p + 2], bgra[p + 1], bgra[p]);
    }

    private static byte[] Corriger(byte[] bgra, int largeur, int hauteur, ImageAdjustments a)
    {
        var copie = (byte[])bgra.Clone();
        PixelCorrections.AppliquerPoints(
            copie, largeur, hauteur, PixelCorrections.Disposition.Bgra, a);
        PixelCorrections.AppliquerRelief(
            copie, largeur, hauteur, PixelCorrections.Disposition.Bgra, a);
        return copie;
    }

    /// <summary>
    /// Le piège du BGRA : rouge et bleu se ressemblent tant qu'on ne les distingue pas.
    /// Réchauffer doit monter le ROUGE, et si les indices étaient croisés on obtiendrait
    /// une photo bleutée sans qu'aucune exception ne le signale.
    /// </summary>
    [Fact]
    public void La_temperature_trouve_le_bon_canal_en_bgra()
    {
        var image = Uni(8, 8, 128, 128, 128);

        var (r, _, b) = Lire(Corriger(image, 8, 8, new ImageAdjustments { Temperature = 100 }));

        Assert.True(r > 128, $"le rouge doit monter (obtenu {r})");
        Assert.True(b < 128, $"le bleu doit descendre (obtenu {b})");
    }

    /// <summary>
    /// Une photo passée en noir et blanc arrive ici avec trois canaux ÉGAUX — c'est ainsi
    /// que WPF la manipule. Les réglages de couleur doivent alors être sautés : les
    /// appliquer quand même remettrait une dominante sur un tirage noir et blanc, et le
    /// défaut ne se verrait qu'une fois le papier sorti.
    /// </summary>
    [Fact]
    public void Un_noir_et_blanc_ne_se_recolore_pas()
    {
        var image = Uni(8, 8, 120, 120, 120);

        var reglages = new ImageAdjustments
        {
            Grayscale = true, Temperature = 100, Tint = -100, Saturation = 80, Vibrance = 60,
        };

        var (r, v, b) = Lire(Corriger(image, 8, 8, reglages));

        Assert.Equal(r, v);
        Assert.Equal(v, b);
    }

    /// <summary>
    /// Le noir et blanc n'empêche pas la lumière de se régler : la courbe de tons doit
    /// continuer d'agir, sur les trois canaux à la fois.
    /// </summary>
    [Fact]
    public void Un_noir_et_blanc_garde_sa_courbe_de_tons()
    {
        var image = Uni(8, 8, 60, 60, 60);

        var (r, v, b) = Lire(Corriger(image, 8, 8,
            new ImageAdjustments { Grayscale = true, Exposure = 1 }));

        Assert.InRange(r, 115, 125);   // 60 doublé
        Assert.Equal(r, v);
        Assert.Equal(v, b);
    }

    /// <summary>
    /// Une saturation à fond dans le négatif doit ramener les trois canaux sur la clarté
    /// HSL — la moyenne du plus clair et du plus sombre, et non la luminance. C'est ce que
    /// faisait <c>Modulate</c>, et le tirage doit rester le même qu'avant le changement.
    /// </summary>
    [Fact]
    public void Une_saturation_a_zero_ramene_sur_la_clarte_hsl()
    {
        var image = Uni(8, 8, 200, 100, 60);

        var (r, v, b) = Lire(Corriger(image, 8, 8, new ImageAdjustments { Saturation = -100 }));

        var clarte = (200 + 60) / 2;   // (max + min) / 2
        Assert.InRange(r, clarte - 1, clarte + 1);
        Assert.Equal(r, v);
        Assert.Equal(v, b);
    }

    /// <summary>
    /// Le travail est réparti sur les cœurs : deux passages sur la même image doivent
    /// rendre exactement les mêmes octets.
    ///
    /// C'est le contrôle qui attrape une écriture concurrente — deux bandes qui se
    /// marchent dessus sur une ligne de bord. Un tel défaut ne casse rien franchement : il
    /// pose quelques pixels faux, à un endroit qui change d'un tirage à l'autre.
    /// </summary>
    [Fact]
    public void Le_travail_reparti_sur_les_coeurs_rend_toujours_la_meme_image()
    {
        const int cote = 400;   // 160 000 pixels : au-dessus du seuil de répartition

        var image = new byte[cote * cote * 4];
        new Random(1234).NextBytes(image);
        for (var i = 3; i < image.Length; i += 4) image[i] = 255;

        var reglages = new ImageAdjustments
        {
            Exposure = 0.4, Contrast = 25, Saturation = 20, Vibrance = 30,
            Temperature = 15, Clarity = 40, Sharpness = 50,
        };

        var reference = Corriger(image, cote, cote, reglages);

        for (var essai = 0; essai < 5; essai++)
            Assert.Equal(reference, Corriger(image, cote, cote, reglages));
    }

    /// <summary>
    /// Sur une zone parfaitement unie, le masque flou n'a aucun écart à saisir : il doit
    /// rendre la valeur d'origine, à l'octet près. Un flou qui dériverait d'un demi-niveau
    /// se verrait en bandes dans un ciel.
    ///
    /// Les deux côtés comptent : 64 passe sous le seuil de répartition et se calcule d'un
    /// seul fil, 256 le franchit et se découpe en bandes. Un bord de bande mal recousu ne
    /// se verrait que sur le second.
    /// </summary>
    [Theory]
    [InlineData(64, 40)]
    [InlineData(64, 128)]
    [InlineData(256, 128)]
    [InlineData(256, 212)]
    public void Le_relief_ne_touche_pas_une_zone_unie(int cote, byte valeur)
    {
        var image = Uni(cote, cote, valeur, valeur, valeur);

        var corrige = Corriger(image, cote, cote,
            new ImageAdjustments { Clarity = 100, Sharpness = 100 });

        Assert.Equal(image, corrige);
    }

    /// <summary>
    /// La clarté doit creuser le contraste local : sur un bord franc entre deux gris, le
    /// côté clair s'éclaircit. C'est la contrepartie du test précédent — le flou fait
    /// quelque chose, et pas n'importe quoi.
    /// </summary>
    [Fact]
    public void La_clarte_accentue_un_bord()
    {
        const int cote = 200;
        var image = Uni(cote, cote, 160, 160, 160);

        // moitié gauche plus sombre
        for (var y = 0; y < cote; y++)
            for (var x = 0; x < cote / 2; x++)
            {
                var p = (y * cote + x) * 4;
                image[p] = image[p + 1] = image[p + 2] = 100;
            }

        var avant = Lire(image, 100 * cote + 104).R;
        var apres = Lire(Corriger(image, cote, cote, new ImageAdjustments { Clarity = 100 }),
                         100 * cote + 104).R;

        Assert.True(apres > avant, $"le côté clair du bord doit s'éclaircir ({avant} → {apres})");
    }

    /// <summary>
    /// Le chemin des octets et celui d'ImageMagick doivent donner la MÊME image : l'aperçu
    /// travaille en BGRA, le tirage sur une image Magick, et l'opérateur juge sur l'aperçu
    /// ce qui sortira du minilab.
    /// </summary>
    [Fact]
    public void L_apercu_et_le_tirage_donnent_la_meme_image()
    {
        MagickInit.Configure();

        const int cote = 48;
        var bgra = Uni(cote, cote, 190, 110, 70);

        var reglages = new ImageAdjustments
        {
            Exposure = 0.5, Contrast = 30, Shadows = 20, Temperature = 20, Tint = -10,
            Saturation = 25, Vibrance = 40,
        };

        var parLesOctets = Lire(Corriger(bgra, cote, cote, reglages));

        var lecture = new PixelReadSettings(cote, cote, StorageType.Char, PixelMapping.BGRA);
        using var image = new MagickImage(bgra, lecture);
        ImageAdjuster.Apply(image, reglages);

        using var pixels = image.GetPixels();
        var pixel = pixels.GetPixel(cote / 2, cote / 2);
        var parMagick = ((byte)pixel.GetChannel(0), (byte)pixel.GetChannel(1), (byte)pixel.GetChannel(2));

        Assert.Equal(parMagick, parLesOctets);
    }
}
