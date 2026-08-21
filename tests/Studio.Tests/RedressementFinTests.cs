using ImageMagick;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le redressement fin passé à OpenCV — voir <see cref="RedressementFin"/>.
///
/// <b>Ces épreuves gardent une image FAUSSE, pas une image lente.</b> Le gain de vitesse se
/// mesure ailleurs (<see cref="RedressementCoutTests"/>) ; ici on tient la forme de l'image,
/// parce que c'est par là que ce chemin s'est trompé : une carte de canaux mal devinée ne
/// plante pas, elle sort une photo écarlate, et une photo écarlate part chez le client.
/// </summary>
public class RedressementFinTests
{
    /// <summary>
    /// <b>Le défaut du 20/08/2026.</b> Magick ouvre un JPEG noir et blanc en `Gray` avec
    /// DEUX canaux — le gris et son alpha. Le premier jet rangeait tout ce qui n'était ni un
    /// canal ni trois dans « quatre canaux » : OpenCV lisait alors deux fois trop d'octets par
    /// pixel, et l'image ressortait rouge vif, rayée de franges.
    ///
    /// Une image grise doit rester grise. On le vérifie sur les pixels eux-mêmes, et non sur
    /// l'espace colorimétrique déclaré : c'est le dessin qui part à l'impression.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Une_image_grise_reste_grise(int canaux)
    {
        using var image = ImageAvec(canaux);
        Assert.Equal(canaux, (int)image.ChannelCount);

        RedressementFin.Appliquer(image, 2.25, MagickColors.White);

        using var pixels = image.GetPixels();

        // on reste à l'intérieur du contenu : les coins, eux, sont du remplissage
        for (var y = (int)image.Height / 4; y < image.Height * 3 / 4; y += 7)
        {
            for (var x = (int)image.Width / 4; x < image.Width * 3 / 4; x += 7)
            {
                var couleur = pixels.GetPixel(x, y).ToColor()!;

                Assert.True(
                    Math.Abs(couleur.R - couleur.G) <= 2 && Math.Abs(couleur.G - couleur.B) <= 2,
                    $"{canaux} canal/canaux : le pixel ({x},{y}) est sorti coloré — " +
                    $"R{couleur.R} G{couleur.G} B{couleur.B}");
            }
        }
    }

    /// <summary>
    /// Le contrat de <c>MagickImage.Rotate</c>, auquel ce code se substitue : le canevas
    /// s'agrandit assez pour que rien ne soit coupé, et les coins qui s'ouvrent portent la
    /// couleur demandée.
    /// </summary>
    [Fact]
    public void Le_canevas_grandit_et_les_coins_prennent_la_couleur_demandee()
    {
        using var image = ImageAvec(3);
        var largeurAvant = image.Width;
        var hauteurAvant = image.Height;

        RedressementFin.Appliquer(image, 5.0, MagickColors.Red);

        Assert.True(image.Width > largeurAvant, "le canevas n'a pas grandi en largeur");
        Assert.True(image.Height > hauteurAvant, "le canevas n'a pas grandi en hauteur");

        using var pixels = image.GetPixels();
        var coin = pixels.GetPixel(1, 1).ToColor()!;

        Assert.True(coin.R > 200 && coin.G < 60 && coin.B < 60,
            $"le coin devrait porter la couleur de remplissage, il est en R{coin.R} G{coin.G} B{coin.B}");
    }

    /// <summary>Un angle négligeable ne doit RIEN faire — pas même agrandir le canevas.</summary>
    [Fact]
    public void Un_angle_negligeable_laisse_l_image_intacte()
    {
        using var image = ImageAvec(3);
        var largeur = image.Width;
        var hauteur = image.Height;

        RedressementFin.Appliquer(image, 0.005, MagickColors.White);

        Assert.Equal(largeur, image.Width);
        Assert.Equal(hauteur, image.Height);
    }

    /// <summary>
    /// Une image de la forme voulue : un dégradé gris, pour que le déplacement se voie, avec
    /// le nombre de canaux demandé.
    /// </summary>
    private static MagickImage ImageAvec(int canaux)
    {
        var image = new MagickImage(MagickColors.Gray, 400, 300);

        // un dégradé plutôt qu'un aplat : un aplat resterait identique quoi qu'on lui fasse
        using (var pixels = image.GetPixels())
        {
            var octets = pixels.ToByteArray("RGB")!;
            for (var y = 0; y < 300; y++)
                for (var x = 0; x < 400; x++)
                {
                    var niveau = (byte)(30 + (x * 200 / 400));
                    var i = (y * 400 + x) * 3;
                    octets[i] = octets[i + 1] = octets[i + 2] = niveau;
                }
            pixels.SetPixels(octets);
        }

        if (canaux is 1 or 2) image.ColorSpace = ColorSpace.Gray;

        // l'alpha se pose en dernier : c'est lui qui fait passer de 1 à 2, ou de 3 à 4
        image.Alpha(canaux is 2 or 4 ? AlphaOption.Set : AlphaOption.Off);

        return image;
    }
}
