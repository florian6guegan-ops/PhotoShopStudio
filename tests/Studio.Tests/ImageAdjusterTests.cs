using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Application des corrections sur de vrais pixels. La courbe de tons est vérifiée à
/// part ; ici on contrôle ce que la courbe ne dit pas : la couleur, le relief, et le
/// fait que chaque réglage agisse bien sur l'image.
/// </summary>
public class ImageAdjusterTests
{
    /// <summary>Une image d'une seule couleur : ce qu'on lui fait se lit directement.</summary>
    private static MagickImage Uni(byte r, byte g, byte b) =>
        new(MagickColor.FromRgb(r, g, b), 40, 40);

    private static (double R, double G, double B) Couleur(IMagickImage<byte> image)
    {
        using var pixels = image.GetPixels();
        var pixel = pixels.GetPixel(20, 20);
        return (pixel.GetChannel(0), pixel.GetChannel(1), pixel.GetChannel(2));
    }

    private static (double R, double G, double B) Corriger(
        MagickImage image, Action<ImageAdjustments> reglage)
    {
        var a = new ImageAdjustments();
        reglage(a);
        ImageAdjuster.Apply(image, a);
        return Couleur(image);
    }

    [Fact]
    public void Sans_reglage_l_image_n_est_pas_touchee()
    {
        using var image = Uni(120, 130, 140);

        ImageAdjuster.Apply(image, new ImageAdjustments());

        Assert.Equal((120, 130, 140), Couleur(image));
    }

    [Fact]
    public void L_exposition_eclaircit_l_image()
    {
        using var image = Uni(60, 60, 60);

        var (r, _, _) = Corriger(image, a => a.Exposure = 1);

        Assert.InRange(r, 115, 125);   // 60 doublé, aux arrondis de la table près
    }

    [Fact]
    public void Les_ombres_debouchent_un_sujet_sombre()
    {
        using var image = Uni(30, 30, 30);

        var (r, _, _) = Corriger(image, a => a.Shadows = 100);

        Assert.True(r > 80, $"les ombres doivent remonter nettement (obtenu {r})");
    }

    [Fact]
    public void Les_hautes_lumieres_recuperent_un_ciel_brule()
    {
        using var image = Uni(240, 240, 240);

        var (r, _, _) = Corriger(image, a => a.Highlights = -100);

        Assert.True(r < 190, $"les hautes lumières doivent redescendre (obtenu {r})");
    }

    /// <summary>Réchauffer, c'est monter le rouge et descendre le bleu.</summary>
    [Fact]
    public void La_temperature_rechauffe_ou_refroidit()
    {
        using var chaud = Uni(128, 128, 128);
        var (rc, _, bc) = Corriger(chaud, a => a.Temperature = 100);
        Assert.True(rc > 128 && bc < 128, $"chaud attendu, obtenu R={rc} B={bc}");

        using var froid = Uni(128, 128, 128);
        var (rf, _, bf) = Corriger(froid, a => a.Temperature = -100);
        Assert.True(rf < 128 && bf > 128, $"froid attendu, obtenu R={rf} B={bf}");
    }

    [Fact]
    public void La_teinte_va_du_vert_au_magenta()
    {
        using var magenta = Uni(128, 128, 128);
        var (_, gm, _) = Corriger(magenta, a => a.Tint = 100);
        Assert.True(gm < 128, $"le magenta retire du vert (obtenu {gm})");

        using var vert = Uni(128, 128, 128);
        var (_, gv, _) = Corriger(vert, a => a.Tint = -100);
        Assert.True(gv > 128, $"le vert en ajoute (obtenu {gv})");
    }

    [Fact]
    public void La_saturation_intensifie_les_couleurs()
    {
        using var image = Uni(180, 120, 120);

        var (r, g, _) = Corriger(image, a => a.Saturation = 60);

        Assert.True(r - g > 60, $"l'écart entre canaux doit croître (obtenu {r - g})");
    }

    [Fact]
    public void Une_saturation_negative_eteint_les_couleurs()
    {
        using var image = Uni(180, 120, 120);

        var (r, g, _) = Corriger(image, a => a.Saturation = -100);

        Assert.True(Math.Abs(r - g) < 5, $"les canaux doivent se rejoindre (obtenu {r} et {g})");
    }

    /// <summary>
    /// Le point de la vibrance : elle relève les couleurs ternes et laisse tranquilles
    /// celles qui sont déjà vives — c'est ce qui protège les teints de peau.
    /// </summary>
    [Fact]
    public void La_vibrance_releve_les_couleurs_ternes_plus_que_les_vives()
    {
        using var terne = Uni(140, 128, 128);
        var (rt, gt, _) = Corriger(terne, a => a.Vibrance = 100);
        var gainTerne = (rt - gt) - 12;

        using var vive = Uni(240, 40, 40);
        var (rv, gv, _) = Corriger(vive, a => a.Vibrance = 100);
        var gainVive = (rv - gv) - 200;

        Assert.True(gainTerne > 0, $"une couleur terne doit gagner (obtenu {gainTerne})");
        Assert.True(gainTerne > gainVive,
            $"la couleur terne doit gagner plus que la vive ({gainTerne} contre {gainVive})");
    }

    /// <summary>
    /// Le noir et blanc ramène l'image à un seul canal : ImageMagick change son espace
    /// colorimétrique, il ne se contente pas d'égaliser trois canaux.
    /// </summary>
    [Fact]
    public void Le_noir_et_blanc_ramene_l_image_en_niveaux_de_gris()
    {
        using var image = Uni(200, 100, 50);

        ImageAdjuster.Apply(image, new ImageAdjustments { Grayscale = true });

        Assert.Equal(ColorSpace.Gray, image.ColorSpace);
        var (r, _, _) = Couleur(image);
        Assert.InRange(r, 100, 140);   // luminance Rec709 du rouge orangé d'origine
    }

    /// <summary>
    /// Le noir et blanc passe avant la courbe de tons : il faut que les deux se combinent
    /// encore, sur une image qui n'a plus qu'un canal.
    /// </summary>
    [Fact]
    public void Un_noir_et_blanc_se_combine_avec_la_courbe_de_tons()
    {
        using var reference = Uni(200, 100, 50);
        ImageAdjuster.Apply(reference, new ImageAdjustments { Grayscale = true });
        var (avant, _, _) = Couleur(reference);

        using var image = Uni(200, 100, 50);
        ImageAdjuster.Apply(image, new ImageAdjustments { Grayscale = true, Exposure = 1 });
        var (apres, _, _) = Couleur(image);

        Assert.True(apres > avant + 40, $"l'exposition doit encore agir ({avant} → {apres})");
    }

    /// <summary>
    /// La netteté et la clarté travaillent sur les écarts entre voisins : sur une image
    /// unie elles n'ont rien à saisir, et ne doivent donc rien changer.
    /// </summary>
    [Fact]
    public void La_nettete_ne_denature_pas_une_zone_unie()
    {
        using var image = Uni(128, 128, 128);

        var (r, g, b) = Corriger(image, a => { a.Sharpness = 100; a.Clarity = 100; });

        Assert.Equal((128d, 128d, 128d), (r, g, b));
    }

    /// <summary>La clarté doit creuser le contraste local, donc écarter les tons d'un bord net.</summary>
    [Fact]
    public void La_clarte_accentue_le_contraste_local()
    {
        // deux gris moyens : un bord franc entre blanc et noir ne pourrait pas
        // s'accentuer, les deux côtés étant déjà aux bornes
        using var image = new MagickImage(MagickColor.FromRgb(160, 160, 160), 200, 200);
        using var sombre = new MagickImage(MagickColor.FromRgb(100, 100, 100), 100, 200);
        image.Composite(sombre, 0, 0, CompositeOperator.Over);

        using var pixelsAvant = image.GetPixels();
        var avant = pixelsAvant.GetPixel(104, 100).GetChannel(0);

        ImageAdjuster.Apply(image, new ImageAdjustments { Clarity = 100 });

        using var pixelsApres = image.GetPixels();
        var apres = pixelsApres.GetPixel(104, 100).GetChannel(0);

        Assert.True(apres > avant, $"le côté clair du bord doit s'éclaircir ({avant} → {apres})");
    }

    /// <summary>Les réglages se combinent sans se contredire ni sortir des bornes.</summary>
    [Fact]
    public void Un_developpement_complet_reste_dans_les_bornes()
    {
        using var image = Uni(90, 110, 130);

        var (r, g, b) = Corriger(image, a =>
        {
            a.Exposure = 0.7; a.Contrast = 40; a.Shadows = 60; a.Highlights = -40;
            a.Whites = 20; a.Blacks = -20; a.Temperature = 25; a.Tint = -10;
            a.Saturation = 15; a.Vibrance = 30; a.Clarity = 20; a.Sharpness = 40;
        });

        Assert.InRange(r, 0, 255);
        Assert.InRange(g, 0, 255);
        Assert.InRange(b, 0, 255);
    }

    // — corrections automatiques, reprises de DiLand —

    /// <summary>Un dégradé terne : de quoi laisser aux automatismes de la marge.</summary>
    private static MagickImage Terne()
    {
        var image = new MagickImage(MagickColors.Black, 60, 60);
        using var pixels = image.GetPixels();
        for (var y = 0; y < 60; y++)
            for (var x = 0; x < 60; x++)
            {
                // valeurs resserrées entre 90 et 150 : contraste faible, à étirer
                var v = (byte)(90 + x);
                pixels.SetPixel(x, y, [v, v, v]);
            }
        return image;
    }

    /// <summary>Sans réglage, rien ne doit bouger : les automatismes sont des bascules.</summary>
    [Fact]
    public void Les_corrections_automatiques_sont_inactives_par_defaut()
    {
        Assert.True(new ImageAdjustments().IsNeutral);
    }

    [Theory]
    [InlineData("niveaux")]
    [InlineData("contraste")]
    public void Un_automatisme_de_ton_etire_une_image_terne(string lequel)
    {
        using var image = Terne();

        var a = new ImageAdjustments();
        if (lequel == "niveaux") a.AutoLevels = true; else a.AutoContrast = true;
        Assert.False(a.IsNeutral);

        ImageAdjuster.Apply(image, a);

        // l'écart entre le point le plus sombre et le plus clair doit s'être creusé
        using var pixels = image.GetPixels();
        var sombre = pixels.GetPixel(0, 30).GetChannel(0);
        var clair = pixels.GetPixel(59, 30).GetChannel(0);

        Assert.True(clair - sombre > 59,
            $"{lequel} : écart {clair - sombre}, attendu plus large que les 59 d'origine");
    }

    /// <summary>Une dominante jaune doit être ramenée vers le neutre.</summary>
    [Fact]
    public void La_couleur_automatique_attenue_une_dominante()
    {
        using var image = new MagickImage(MagickColors.Black, 60, 60);
        using (var pixels = image.GetPixels())
            for (var y = 0; y < 60; y++)
                for (var x = 0; x < 60; x++)
                    pixels.SetPixel(x, y, [(byte)(100 + x * 2), (byte)(95 + x * 2), (byte)(40 + x)]);

        var avant = Couleur(image);
        var ecartAvant = avant.R - avant.B;

        ImageAdjuster.Apply(image, new ImageAdjustments { AutoColor = true });

        var apres = Couleur(image);
        Assert.True(apres.R - apres.B < ecartAvant,
            $"dominante non atténuée : {ecartAvant} avant, {apres.R - apres.B} après");
    }

    /// <summary>
    /// Sur une image déjà en noir et blanc, corriger une dominante n'a plus d'objet et
    /// ne doit pas réintroduire de couleur.
    /// </summary>
    [Fact]
    public void La_couleur_automatique_ne_recolore_pas_un_noir_et_blanc()
    {
        using var image = Uni(180, 120, 60);

        ImageAdjuster.Apply(image, new ImageAdjustments { Grayscale = true, AutoColor = true });

        // on lit la couleur composée, et non les canaux : une image passée en noir et
        // blanc n'en a plus qu'un seul, et interroger le vert renverrait 0 à tort
        using var pixels = image.GetPixels();
        var couleur = pixels.GetPixel(20, 20).ToColor()!;

        Assert.Equal(couleur.R, couleur.G);
        Assert.Equal(couleur.G, couleur.B);
    }

    /// <summary>Les trois ensemble ne doivent produire ni débordement ni valeur aberrante.</summary>
    [Fact]
    public void Les_trois_automatismes_cumules_restent_dans_la_plage()
    {
        using var image = Terne();

        ImageAdjuster.Apply(image, new ImageAdjustments
        {
            AutoLevels = true, AutoContrast = true, AutoColor = true, Exposure = 0.4,
        });

        var (r, g, b) = Couleur(image);
        Assert.InRange(r, 0, 255);
        Assert.InRange(g, 0, 255);
        Assert.InRange(b, 0, 255);
    }
}
