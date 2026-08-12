using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Les corrections réservées au sujet détouré.
///
/// <b>Ce que ces essais protègent.</b> Sur une photo d'identité le fond est imposé, et
/// c'est justement lui que les corrections d'un visage abîment : remonter l'exposition
/// grise un fond blanc. La promesse de ce panneau est donc en deux temps — le sujet change,
/// <b>et le fond ne change pas</b>. La seconde moitié est celle qui se casse sans bruit :
/// un masque inversé, une composition dans le mauvais sens, et le fond part avec.
///
/// Le détourage lui-même (<c>BiRefNetMatting</c>) n'est pas rejoué ici : il demande un
/// modèle de 109 Mo qui n'est pas sur tous les postes, et ce n'est pas lui qu'on vérifie.
/// Le masque est donc fourni à la main, ce qui permet en prime de décrire des cas qu'un
/// réseau ne produirait jamais à la demande — un sujet parfaitement carré, par exemple.
/// </summary>
[Collection(DetourageStatiqueCollection.Nom)]
public class CorrectionsSujetTests
{
    /// <summary>Une image partagée en deux : moitié gauche « sujet », moitié droite « fond ».</summary>
    private static MagickImage Image() =>
        new(new MagickColor("#808080"), 200, 100);

    /// <summary>Masque blanc à gauche (le sujet), noir à droite (le fond).</summary>
    private static MagickImage Masque()
    {
        var masque = new MagickImage(MagickColors.Black, 200, 100);
        using var sujet = new MagickImage(MagickColors.White, 100, 100);
        masque.Composite(sujet, 0, 0, CompositeOperator.Over);
        return masque;
    }

    private static double Luminosite(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        var couleur = pixels.GetPixel(x, y).ToColor()!;
        return (couleur.R + couleur.G + couleur.B) / 3.0;
    }

    /// <summary>
    /// Le contrat, en une ligne : le sujet s'éclaircit, le fond ne bouge pas d'un pouce.
    /// </summary>
    [Fact]
    public void La_correction_eclaircit_le_sujet_et_laisse_le_fond_intact()
    {
        using var image = Image();
        using var masque = Masque();

        var avantFond = Luminosite(image, 180, 50);

        var applique = MasqueSujet.Appliquer(
            image, new CorrectionsSujet { Actif = true, Exposure = 1.0 }, masque);

        Assert.True(applique);
        Assert.True(Luminosite(image, 20, 50) > 150, "le sujet devait s'éclaircir");
        Assert.Equal(avantFond, Luminosite(image, 180, 50), 1);
    }

    /// <summary>
    /// Le sujet peut aussi s'ASSOMBRIR : une correction qui ne saurait aller que dans un
    /// sens laisserait un visage brûlé au flash sans recours.
    /// </summary>
    [Fact]
    public void La_correction_peut_assombrir_le_sujet_sans_toucher_au_fond()
    {
        using var image = Image();
        using var masque = Masque();

        var avantFond = Luminosite(image, 180, 50);

        MasqueSujet.Appliquer(
            image, new CorrectionsSujet { Actif = true, Exposure = -1.0 }, masque);

        Assert.True(Luminosite(image, 20, 50) < 100, "le sujet devait s'assombrir");
        Assert.Equal(avantFond, Luminosite(image, 180, 50), 1);
    }

    /// <summary>
    /// La bascule éteinte ne fait RIEN, curseurs poussés ou non. C'est ce qui permet à
    /// l'opérateur de comparer avant/après d'un clic sans perdre ses réglages.
    /// </summary>
    [Fact]
    public void Eteinte_la_correction_ne_touche_a_rien()
    {
        using var image = Image();
        using var masque = Masque();

        var avantSujet = Luminosite(image, 20, 50);

        var applique = MasqueSujet.Appliquer(
            image, new CorrectionsSujet { Actif = false, Exposure = 1.0 }, masque);

        Assert.False(applique);
        Assert.Equal(avantSujet, Luminosite(image, 20, 50), 1);
    }

    /// <summary>
    /// Sans détourage — modèle absent du poste — on ne corrige RIEN plutôt que de corriger
    /// au jugé. Une correction posée sur un masque inventé se verrait sur le tirage, et
    /// l'opérateur n'aurait aucun moyen de comprendre d'où elle vient.
    /// </summary>
    [Fact]
    public void Sans_masque_rien_n_est_corrige()
    {
        using var image = Image();

        var avant = Luminosite(image, 20, 50);

        var applique = MasqueSujet.Appliquer(
            image, new CorrectionsSujet { Actif = true, Exposure = 1.0 }, masque: null);

        Assert.False(applique);
        Assert.Equal(avant, Luminosite(image, 20, 50), 1);
    }

    /// <summary>
    /// Un masque calculé sur la vignette doit servir au tirage en pleine résolution : c'est
    /// le cas NORMAL, l'aperçu travaillant en 1600 px et le tirage sur l'original.
    /// </summary>
    [Fact]
    public void Un_masque_plus_petit_que_l_image_est_remis_a_l_echelle()
    {
        using var image = new MagickImage(new MagickColor("#808080"), 400, 200);

        using var masque = new MagickImage(MagickColors.Black, 200, 100);
        using var sujet = new MagickImage(MagickColors.White, 100, 100);
        masque.Composite(sujet, 0, 0, CompositeOperator.Over);

        var avantFond = Luminosite(image, 380, 100);

        MasqueSujet.Appliquer(
            image, new CorrectionsSujet { Actif = true, Exposure = 1.0 }, masque);

        Assert.True(Luminosite(image, 40, 100) > 150, "le sujet devait s'éclaircir");
        Assert.Equal(avantFond, Luminosite(image, 380, 100), 1);
    }

    // — le masque —

    /// <summary>
    /// <b>Le détourage fin éteint, la correction marche QUAND MÊME.</b>
    ///
    /// C'est le défaut qui a fait dire que le panneau ne servait à rien : le masque n'était
    /// demandé qu'au réseau, éteint par défaut sur un poste, et la correction était alors
    /// silencieusement sautée — curseurs poussés à fond, rien à l'écran. On retombe
    /// désormais sur la découpe par la couleur, celle qui pose déjà les fonds blancs.
    /// </summary>
    [Fact]
    public void Sans_le_reseau_le_masque_vient_de_la_methode_par_couleur()
    {
        var actifAvant = BiRefNetMatting.Actif;
        BiRefNetMatting.Actif = false;
        MasqueSujet.Oublier();

        try
        {
            // un sujet sombre sur le fond uni d'une photo d'identité
            using var image = new MagickImage(MagickColors.White, 200, 200);
            using var sujet = new MagickImage(new MagickColor("#303030"), 80, 80);
            image.Composite(sujet, 60, 60, CompositeOperator.Over);

            using var masque = MasqueSujet.Calculer(image, contourPx: 0, adoucissementPx: 1);

            Assert.NotNull(masque);
            Assert.True(Luminosite(masque, 100, 100) > 200, "le sujet devait être blanc dans le masque");
            Assert.True(Luminosite(masque, 5, 5) < 55, "le fond devait rester noir dans le masque");
        }
        finally
        {
            BiRefNetMatting.Actif = actifAvant;
            MasqueSujet.Oublier();
        }
    }

    /// <summary>
    /// Le masque n'est calculé QU'UNE FOIS pour une même photo : c'est ce qui rend un
    /// panneau de huit curseurs utilisable quand chaque découpe coûte des secondes.
    /// </summary>
    [Fact]
    public void Le_masque_d_une_meme_photo_n_est_pas_recalcule()
    {
        var actifAvant = BiRefNetMatting.Actif;
        BiRefNetMatting.Actif = false;
        MasqueSujet.Oublier();

        try
        {
            using var image = new MagickImage(MagickColors.White, 200, 200);
            using var sujet = new MagickImage(new MagickColor("#303030"), 80, 80);
            image.Composite(sujet, 60, 60, CompositeOperator.Over);

            using var premier = MasqueSujet.Calculer(image, 0, 1);
            Assert.NotNull(premier);

            var chrono = System.Diagnostics.Stopwatch.StartNew();
            using var second = MasqueSujet.Calculer(image, 0, 1);
            chrono.Stop();

            Assert.NotNull(second);

            // le second ne fait plus que relire des octets et flouter : sans la mémoire, il
            // repayerait la découpe entière
            Assert.True(chrono.ElapsedMilliseconds < 200,
                $"le masque a été recalculé ({chrono.ElapsedMilliseconds} ms)");
        }
        finally
        {
            BiRefNetMatting.Actif = actifAvant;
            MasqueSujet.Oublier();
        }
    }

    /// <summary>
    /// <b>La barre d'attente ne doit se montrer que s'il y a vraiment à attendre.</b>
    ///
    /// L'écran le demande avant chaque aperçu : une photo déjà détourée se corrige en un
    /// dixième de seconde, et une barre qui apparaîtrait là clignoterait à chaque mouvement
    /// de curseur — pire que pas de barre du tout.
    /// </summary>
    [Fact]
    public void Une_photo_deja_detouree_n_annonce_plus_d_attente()
    {
        var actifAvant = BiRefNetMatting.Actif;
        BiRefNetMatting.Actif = false;
        MasqueSujet.Oublier();

        try
        {
            using var image = new MagickImage(MagickColors.White, 200, 200);
            using var sujet = new MagickImage(new MagickColor("#303030"), 80, 80);
            image.Composite(sujet, 60, 60, CompositeOperator.Over);

            const string cle = @"D:\photos\dupont.jpg#1";

            Assert.False(MasqueSujet.DejaEnMemoire(cle, image.Width, image.Height));

            using (MasqueSujet.Calculer(image, 0, 3, cle)) { }

            Assert.True(MasqueSujet.DejaEnMemoire(cle, image.Width, image.Height));

            // le temps mis est relevé : c'est lui que la barre annoncera la fois suivante
            Assert.NotNull(MasqueSujet.DerniereDuree);

            // une AUTRE photo, elle, fait toujours attendre
            Assert.False(MasqueSujet.DejaEnMemoire(@"D:\photos\martin.jpg#1", image.Width, image.Height));

            // LA MÊME PHOTO À UNE AUTRE TAILLE N'ATTEND PLUS, et cet essai disait l'inverse.
            //
            // Il justifiait ainsi : « l'aperçu travaille en 1600 px, le tirage en pleine
            // résolution, et ce ne sont pas les mêmes masques ». La prémisse était fausse —
            // BiRefNet travaille sur une entrée FIGÉE à 1024 × 1024 et ne remet à l'échelle
            // qu'à la fin : c'est le MÊME masque, à un redimensionnement près.
            //
            // Ce qu'elle coûtait : le récapitulatif des planches, rendu à la taille
            // d'impression, refaisait tourner le réseau pour rien — 14 495 ms par photo
            // relevés à Créteil le 12/08/2026 — et ce second passage mettait la carte
            // graphique en panne de mémoire.
            Assert.True(MasqueSujet.DejaEnMemoire(cle, image.Width * 2, image.Height * 2),
                "le masque d'une taille sert à toutes les autres");
        }
        finally
        {
            BiRefNetMatting.Actif = actifAvant;
            MasqueSujet.Oublier();
        }
    }

    // — le modèle —

    /// <summary>
    /// <b>La copie doit être indépendante, sujet compris.</b> <c>MemberwiseClone</c> ne
    /// copie que la surface : sans traitement particulier, l'original et sa copie
    /// partageraient le même objet, et l'aperçu — qui clone les réglages à chaque frappe
    /// pour les passer à un fil de fond — écrirait dans les deux à la fois.
    /// </summary>
    [Fact]
    public void Cloner_des_reglages_detache_aussi_le_sujet()
    {
        var reglages = new ImageAdjustments { Sujet = { Actif = true, Exposure = 0.5 } };

        var copie = reglages.Clone();
        copie.Sujet.Exposure = -1.5;

        Assert.Equal(0.5, reglages.Sujet.Exposure);
        Assert.Equal(-1.5, copie.Sujet.Exposure);
    }

    /// <summary>
    /// Des réglages par ailleurs vierges ne sont PAS neutres si le sujet demande quelque
    /// chose : <c>ImageAdjuster.Apply</c> sort au premier mot sur <c>IsNeutral</c>, et
    /// l'oublier rendrait le panneau entier sans effet.
    /// </summary>
    [Fact]
    public void Un_reglage_de_sujet_suffit_a_rendre_les_corrections_non_neutres()
    {
        var reglages = new ImageAdjustments();
        Assert.True(reglages.IsNeutral);

        reglages.Sujet.Actif = true;
        reglages.Sujet.Exposure = 0.5;

        Assert.False(reglages.IsNeutral);
    }

    /// <summary>
    /// Le contour et l'adoucissement, SEULS, ne changent rien à l'image : ils décrivent une
    /// découpe, pas une correction. Les compter rendrait l'aperçu non neutre pour un
    /// curseur qui n'a encore rien demandé.
    /// </summary>
    [Fact]
    public void Le_contour_seul_ne_rend_pas_les_corrections_actives()
    {
        var reglages = new ImageAdjustments();
        reglages.Sujet.Actif = true;
        reglages.Sujet.ContourPx = 5;
        reglages.Sujet.AdoucissementPx = 12;

        Assert.True(reglages.IsNeutral);
    }
}
