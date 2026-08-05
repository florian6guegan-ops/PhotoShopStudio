using ImageMagick;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Le débord du minilab, et le liseré blanc qu'il faisait apparaître.
///
/// <b>Le défaut</b> (constaté en boutique le 05/08/2026, sur des 10×15, des 13×18 et des
/// 15×20 — c'est-à-dire sur tout ce qui sortait) : le DE100 réclame l'image AVEC les 3 mm
/// de débord qu'il rognera. Studio étendait le canevas à cette définition en comblant de
/// BLANC, et la photo se retrouvait cernée d'un liseré que le rognage de la machine ne
/// mangeait pas — il part du bord du papier, pas du bord de l'image.
///
/// Le débord doit être rempli par la photo elle-même : c'est ce que ces essais figent.
/// </summary>
public class DebordMinilabTests
{
    /// <summary>
    /// Les cotes réelles du 21×29,7, relevées sur la machine : elle réclame 2515 × 3543
    /// là où les cotes nues font 2480 × 3508.
    /// </summary>
    [Fact]
    public void Le_debord_est_rempli_par_la_photo_et_non_par_du_blanc()
    {
        using var image = ImageUnie(2480, 3508, MagickColors.Red);

        PrintOrchestrator.RemplirLeDebord(image, 2515, 3543);

        Assert.Equal(2515u, image.Width);
        Assert.Equal(3543u, image.Height);
        Assert.Empty(BordsBlancs(image));
    }

    /// <summary>
    /// Le cas du 10×15, le plus tiré de la boutique. Le débord y est le même en pixels,
    /// donc plus visible en proportion.
    /// </summary>
    [Fact]
    public void Un_10x15_sort_sans_lialisere_sur_ses_quatre_bords()
    {
        using var image = ImageUnie(1795, 1205, MagickColors.Red);

        PrintOrchestrator.RemplirLeDebord(image, 1830, 1240);

        Assert.Empty(BordsBlancs(image));
    }

    /// <summary>
    /// Sans débord — machine muette, format qu'elle ne connaît pas — la cible vaut les
    /// cotes nues et l'image ne doit pas bouger d'un pixel. C'est le repli qui garantit
    /// qu'on ne perd jamais un tirage sur une lecture ratée.
    /// </summary>
    [Fact]
    public void Sans_debord_l_image_est_laissee_telle_quelle()
    {
        using var image = ImageUnie(1795, 1205, MagickColors.Red);

        PrintOrchestrator.RemplirLeDebord(image, 1795, 1205);

        Assert.Equal(1795u, image.Width);
        Assert.Equal(1205u, image.Height);
        Assert.Empty(BordsBlancs(image));
    }

    /// <summary>
    /// <b>Les bandes blanches du rouleau, elles, doivent survivre.</b> Un 10×15 posé sur un
    /// rouleau de 210 mm laisse du blanc de chaque côté, et c'est voulu : c'est la largeur
    /// du papier, pas un défaut de calage. La correction du liseré ne doit pas les manger
    /// en agrandissant la photo jusqu'aux bords du rouleau.
    /// </summary>
    [Fact]
    public void Les_bandes_blanches_du_rouleau_ne_sont_pas_mangees()
    {
        // le tirage tel qu'il paraît : photo au centre, blanc de chaque côté
        using var image = ImageUnie(2480, 1205, MagickColors.White);
        using var photo = ImageUnie(1795, 1205, MagickColors.Red);
        image.Composite(photo, Gravity.Center, CompositeOperator.Over);

        PrintOrchestrator.RemplirLeDebord(image, 2515, 1240);

        Assert.Equal(2515u, image.Width);

        // le bord gauche reste blanc — la bande est toujours là
        using var pixels = image.GetPixels();
        var coin = pixels.GetPixel(2, (int)image.Height / 2).ToColor()!;
        Assert.True(EstBlanc(coin), $"la bande blanche du rouleau a disparu : {coin}");
    }

    /// <summary>
    /// Le débord ne vaut pas la même PROPORTION en largeur qu'en hauteur : cadrer chaque
    /// axe séparément étirerait la photo. Le rapport doit être tenu.
    /// </summary>
    [Fact]
    public void La_photo_n_est_pas_etiree_par_le_debord()
    {
        // un disque : toute déformation se voit sur son rapport largeur/hauteur
        using var image = ImageUnie(2480, 3508, MagickColors.White);
        image.Draw(new ImageMagick.Drawing.Drawables()
            .FillColor(MagickColors.Red)
            .Ellipse(1240, 1754, 600, 600, 0, 360));

        PrintOrchestrator.RemplirLeDebord(image, 2515, 3543);

        var (largeur, hauteur) = EtendueDuRouge(image);

        // le disque reste un disque : agrandi du même facteur sur les deux axes
        Assert.True(Math.Abs(largeur / (double)hauteur - 1) < 0.02,
            $"le disque est sorti déformé : {largeur} × {hauteur}");
    }

    /// <summary>
    /// <b>Une machine qui demande MOINS doit faire réduire la photo, pas la tronquer.</b>
    ///
    /// Le cas ne s'est jamais présenté sur les machines de la boutique — le débord est
    /// toujours positif — mais le code se contentait alors de rogner au centre : la photo
    /// serait sortie amputée de ses bords, sans que rien ne le dise. Trouvé en relisant le
    /// code avant de livrer.
    /// </summary>
    [Fact]
    public void Une_cible_plus_petite_fait_reduire_la_photo_et_non_la_tronquer()
    {
        // un disque qui touche presque les bords : s'il est rogné au lieu d'être réduit,
        // il ressort coupé
        using var image = ImageUnie(2000, 2000, MagickColors.White);
        image.Draw(new ImageMagick.Drawing.Drawables()
            .FillColor(MagickColors.Red)
            .Ellipse(1000, 1000, 980, 980, 0, 360));

        PrintOrchestrator.RemplirLeDebord(image, 1000, 1000);

        Assert.Equal(1000u, image.Width);
        Assert.Equal(1000u, image.Height);

        var (largeur, hauteur) = EtendueDuRouge(image);

        // le disque occupe toujours presque toute l'image : il a été mis à l'échelle
        Assert.True(largeur > 900, $"le disque est sorti tronqué : {largeur} px de large");
        Assert.True(hauteur > 900, $"le disque est sorti tronqué : {hauteur} px de haut");
    }

    // ————— outils —————

    private static MagickImage ImageUnie(int largeur, int hauteur, IMagickColor<byte> couleur) =>
        new(couleur, (uint)largeur, (uint)hauteur);

    private static bool EstBlanc(IMagickColor<byte> c) =>
        c.R > 235 && c.G > 235 && c.B > 235;

    /// <summary>
    /// Les bords qui portent du blanc — c'est-à-dire le liseré. On regarde à deux pixels
    /// du bord : le tout premier peut porter un demi-pixel d'interpolation.
    /// </summary>
    private static List<string> BordsBlancs(MagickImage image)
    {
        using var pixels = image.GetPixels();
        var w = (int)image.Width;
        var h = (int)image.Height;

        var fautifs = new List<string>();

        void Voir(string nom, int x, int y)
        {
            if (EstBlanc(pixels.GetPixel(x, y).ToColor()!)) fautifs.Add($"{nom} ({x},{y})");
        }

        Voir("haut", w / 2, 2);
        Voir("bas", w / 2, h - 3);
        Voir("gauche", 2, h / 2);
        Voir("droite", w - 3, h / 2);

        return fautifs;
    }

    /// <summary>Étendue de la tache rouge, en pixels.</summary>
    private static (int Largeur, int Hauteur) EtendueDuRouge(MagickImage image)
    {
        using var pixels = image.GetPixels();
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;

        for (var y = 0; y < (int)image.Height; y += 4)
        for (var x = 0; x < (int)image.Width; x += 4)
        {
            var c = pixels.GetPixel(x, y).ToColor()!;
            if (c.R <= 150 || c.G >= 100) continue;

            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        return (maxX - minX, maxY - minY);
    }
}
