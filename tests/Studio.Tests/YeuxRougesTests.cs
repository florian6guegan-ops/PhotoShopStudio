using ImageMagick;
using Studio.Imaging;
using Studio.Imaging.Faces;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// La correction des yeux rouges.
///
/// L'essai porte sur la CORRECTION, pas sur la détection : on lui donne des visages déjà
/// trouvés, ce qui la rend vérifiable sans modèle ONNX ni photographie de personne réelle.
/// Ce qu'il faut prouver tient en deux points — le rouge de la pupille part, et rien
/// d'autre n'est touché.
/// </summary>
public class YeuxRougesTests
{
    private const int Largeur = 200;
    private const int Hauteur = 200;

    /// <summary>Une image unie, sur laquelle on pose ensuite des taches.</summary>
    private static MagickImage Toile(MagickColor fond) =>
        new(fond, Largeur, Hauteur);

    private static void PoserUneTache(MagickImage image, int cx, int cy, int rayon, MagickColor couleur)
    {
        using var pixels = image.GetPixels();

        for (var y = Math.Max(0, cy - rayon); y <= Math.Min(Hauteur - 1, cy + rayon); y++)
        for (var x = Math.Max(0, cx - rayon); x <= Math.Min(Largeur - 1, cx + rayon); x++)
        {
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > rayon * rayon) continue;
            pixels.SetPixel(x, y, [couleur.R, couleur.G, couleur.B]);
        }
    }

    /// <summary>
    /// Magick.NET est en Q8 sur ce dépôt (<c>Magick.NET-Q8-x64</c>) : les canaux sont des
    /// octets et se lisent tels quels. Les décaler de huit bits, comme on le ferait en Q16,
    /// rendrait tout noir.
    /// </summary>
    private static (int R, int V, int B) Lire(MagickImage image, int x, int y)
    {
        using var pixels = image.GetPixels();
        var couleur = pixels.GetPixel(x, y).ToColor()!;
        return (couleur.R, couleur.G, couleur.B);
    }

    /// <summary>Un visage occupant tout le cadre, avec un œil à l'endroit indiqué.</summary>
    private static DetectedFace Visage(double oeilX, double oeilY) =>
        new(new NormRect(0.1, 0.1, 0.8, 0.8), 0.99,
            [new Studio.Imaging.Geometry.NormPoint(oeilX / Largeur, oeilY / Hauteur)]);

    /// <summary>Rouge vif de rétine : le rouge écrase largement les deux autres canaux.</summary>
    private static readonly MagickColor RougeDeRetine = new(220, 40, 40);

    /// <summary>
    /// Des PEAUX, relevées ou plausibles — ce que la correction ne doit jamais toucher.
    ///
    /// <b>La première est mesurée</b>, le 17/08/2026, sur la peau au-dessus de l'œil d'une
    /// photo d'identité du poste : R=83, V=61, B=47, soit un rapport de 1,54. L'ancien seuil
    /// de 1,5 la prenait pour une pupille et lui rabattait le rouge à 54 — 36 862 pixels
    /// grisés autour des yeux, pendant que les vraies pupilles (R=22) n'étaient pas touchées.
    /// C'est le défaut signalé depuis la boutique : « il ne l'applique pas sur les yeux ».
    /// </summary>
    public static TheoryData<byte, byte, byte, string> Peaux => new()
    {
        { 83, 61, 47, "peau foncée mesurée le 17/08 (rapport 1,54)" },
        { 95, 67, 55, "la même, plus claire (rapport 1,56)" },
        { 205, 155, 135, "peau claire éclairée (rapport 1,41)" },
        { 180, 120, 100, "peau chaude, joue rosée (rapport 1,64)" },
    };

    /// <summary>
    /// <b>Aucune peau ne doit être prise pour une pupille.</b> Le disque est centré sur l'œil
    /// mais déborde toujours un peu : c'est le SEUIL qui est le rempart, pas le cadrage.
    /// </summary>
    [Theory]
    [MemberData(nameof(Peaux))]
    public void La_peau_n_est_jamais_prise_pour_une_pupille(byte r, byte v, byte b, string quoi)
    {
        using var image = Toile(new MagickColor(r, v, b));

        var touche = YeuxRouges.Corriger(image, [Visage(100, 100)]);

        Assert.False(touche, $"la correction a mordu sur : {quoi}");

        var (apresR, apresV, apresB) = Lire(image, 100, 100);
        Assert.Equal((r, v, b), ((byte)apresR, (byte)apresV, (byte)apresB));
    }

    /// <summary>
    /// Le rempart ne doit pas non plus tout bloquer : un vrai rouge de rétine passe encore,
    /// et de loin — son rapport dépasse 4 là où une peau reste sous 1,7.
    /// </summary>
    [Fact]
    public void Un_vrai_rouge_de_retine_reste_corrige_malgre_le_seuil_releve()
    {
        using var image = Toile(new MagickColor(83, 61, 47)); // sur de la peau, comme en vrai
        PoserUneTache(image, 100, 100, 4, RougeDeRetine);

        var touche = YeuxRouges.Corriger(image, [Visage(100, 100)]);

        Assert.True(touche, "le rouge de rétine devait être corrigé");

        var (r, v, _) = Lire(image, 100, 100);
        Assert.InRange(r, v - 2, v + 2);

        // et la peau autour n'a pas bougé
        Assert.Equal((83, 61, 47), Lire(image, 100, 130));
    }

    [Fact]
    public void Le_rouge_de_la_pupille_est_neutralise()
    {
        using var image = Toile(new MagickColor(128, 128, 128));
        PoserUneTache(image, 100, 100, 6, RougeDeRetine);

        var touche = YeuxRouges.Corriger(image, [Visage(100, 100)]);

        Assert.True(touche, "la correction n'a rien trouvé à corriger");

        var (r, v, b) = Lire(image, 100, 100);
        Assert.Equal(v, b);                       // la tache reste ce qu'elle était sur ces deux canaux
        Assert.InRange(r, v - 2, v + 2);          // et le rouge les a rejoints
    }

    /// <summary>
    /// La pupille devient GRISE, pas noire : un trou noir se remarque autant que le rouge,
    /// et c'est le défaut des retouches faites à la va-vite.
    /// </summary>
    [Fact]
    public void La_pupille_corrigee_est_grise_et_non_noire()
    {
        using var image = Toile(new MagickColor(128, 128, 128));
        PoserUneTache(image, 100, 100, 6, RougeDeRetine);

        YeuxRouges.Corriger(image, [Visage(100, 100)]);

        var (r, _, _) = Lire(image, 100, 100);
        Assert.True(r > 20, $"la pupille est devenue un trou noir (rouge = {r})");
    }

    /// <summary>
    /// LE point qui compte pour la boutique : une écharpe rouge, une joue rosée, une bouche
    /// maquillée passent toutes le test du « rouge dominant ». Le seul rempart est de ne
    /// regarder QUE là où un œil se trouve.
    /// </summary>
    [Fact]
    public void Un_rouge_hors_de_l_oeil_n_est_jamais_touche()
    {
        using var image = Toile(new MagickColor(128, 128, 128));
        PoserUneTache(image, 20, 180, 10, RougeDeRetine);   // une écharpe, en bas à gauche

        var touche = YeuxRouges.Corriger(image, [Visage(100, 100)]);

        Assert.False(touche);

        var (r, v, b) = Lire(image, 20, 180);
        Assert.Equal((220, 40, 40), (r, v, b));
    }

    [Fact]
    public void Un_gris_dans_l_oeil_reste_gris()
    {
        using var image = Toile(new MagickColor(128, 128, 128));

        var touche = YeuxRouges.Corriger(image, [Visage(100, 100)]);

        Assert.False(touche);
        Assert.Equal((128, 128, 128), Lire(image, 100, 100));
    }

    /// <summary>
    /// Un rouge SOMBRE — un cil, une ombre — n'est pas une rétine éclairée au flash.
    /// </summary>
    [Fact]
    public void Un_rouge_trop_sombre_est_laisse_tel_quel()
    {
        using var image = Toile(new MagickColor(128, 128, 128));
        PoserUneTache(image, 100, 100, 6, new MagickColor(40, 8, 8));

        Assert.False(YeuxRouges.Corriger(image, [Visage(100, 100)]));
    }

    [Fact]
    public void Une_photo_sans_visage_reste_intacte()
    {
        using var image = Toile(new MagickColor(128, 128, 128));
        PoserUneTache(image, 100, 100, 6, RougeDeRetine);

        Assert.False(YeuxRouges.Corriger(image, []));
        Assert.Equal((220, 40, 40), Lire(image, 100, 100));
    }

    /// <summary>Une photo de famille au flash : chaque visage a droit à sa correction.</summary>
    [Fact]
    public void Plusieurs_visages_sont_tous_corriges()
    {
        using var image = Toile(new MagickColor(128, 128, 128));
        PoserUneTache(image, 50, 50, 5, RougeDeRetine);
        PoserUneTache(image, 150, 150, 5, RougeDeRetine);

        Assert.True(YeuxRouges.Corriger(image, [Visage(50, 50), Visage(150, 150)]));

        Assert.True(Lire(image, 50, 50).R < 60);
        Assert.True(Lire(image, 150, 150).R < 60);
    }

    /// <summary>
    /// Sans détecteur posé sur le poste, la case reste sans effet : un tirage ne doit pas
    /// échouer parce qu'elle a été cochée.
    /// </summary>
    [Fact]
    public void Sans_detecteur_la_correction_ne_fait_rien_et_ne_leve_pas()
    {
        var avant = YeuxRouges.Detecteur;
        try
        {
            YeuxRouges.Detecteur = null;

            using var image = Toile(new MagickColor(128, 128, 128));
            PoserUneTache(image, 100, 100, 6, RougeDeRetine);

            Assert.False(YeuxRouges.Appliquer(image));
            Assert.Equal((220, 40, 40), Lire(image, 100, 100));
        }
        finally
        {
            YeuxRouges.Detecteur = avant;
        }
    }

    /// <summary>
    /// Les yeux rouges comptent parmi les réglages : une photo qui n'a QUE cette case cochée
    /// ne doit pas passer pour neutre, sinon le pipeline sauterait la correction.
    /// </summary>
    [Fact]
    public void Une_photo_avec_les_yeux_rouges_coches_n_est_pas_neutre()
    {
        Assert.False(new Studio.Core.Domain.ImageAdjustments { RedEye = true }.IsNeutral);
        Assert.True(new Studio.Core.Domain.ImageAdjustments().IsNeutral);
    }
}
