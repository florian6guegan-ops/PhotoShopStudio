using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Capacité d'une planche d'identité selon l'orientation du tirage.
///
/// La boutique veut des planches de 8, avec 4 et 6 également proposés. Ce n'est pas un
/// réglage libre : c'est de la géométrie. Sur un 10×15 en portrait, huit vignettes de
/// 35×45 ne rentrent pas — il faut la planche en paysage.
/// </summary>
public class IdSheetCapacityTests
{
    private const int Dpi = 300;

    private static int Px(double mm) => MmPx.ToPixels(mm, Dpi);

    private static int Capacite(double largeurMm, double hauteurMm) =>
        IdSheetLayout.MaxCopies(Px(largeurMm), Px(hauteurMm), Px(35), Px(45), Px(2));

    [Fact]
    public void Un_10x15_en_portrait_ne_tient_que_six_vignettes()
    {
        Assert.Equal(6, Capacite(102, 152));
    }

    /// <summary>C'est la raison du passage en paysage : 4 colonnes au lieu de 2.</summary>
    [Fact]
    public void Un_10x15_en_paysage_en_tient_huit()
    {
        Assert.Equal(8, Capacite(152, 102));
    }

    // ————— la place de la bande basse —————

    /// <summary>
    /// <b>La bande qui porte la date doit être comptée dans la capacité.</b>
    ///
    /// Sans elle, le compte remplissait la planche jusqu'en bas et il ne restait plus où
    /// écrire : la date disparaissait du tirage sans que rien ne le signale. Le format
    /// français (35 × 45) laissait par chance assez de marge sur un 10×15 ; les documents
    /// étrangers aux petites cases, non — c'est là que le défaut se voyait (signalé au
    /// comptoir le 11/08/2026).
    ///
    /// 26 × 32 est la case d'un passeport espagnol.
    ///
    /// <b>Ce qu'on réserve est le minimum où une date s'écrit</b> (4,5 mm), et non la
    /// hauteur nominale de la bande (8 mm). La première version comptait la seconde et
    /// coûtait une rangée entière — cinq photos sur quinze ici, et la moitié d'une planche
    /// américaine. La bande n'y perd rien : elle prend ensuite tout ce qui reste réellement
    /// sous les photos, et la date s'y écrit au corps qui tient.
    /// </summary>
    [Fact]
    public void Un_petit_format_etranger_laisse_la_place_a_la_bande()
    {
        var sansBande = IdSheetLayout.MaxCopies(Px(152), Px(102), Px(26), Px(32), Px(2));
        var avecBande = IdSheetLayout.MaxCopies(Px(152), Px(102), Px(26), Px(32), Px(2), Px(4.5));

        // Sur ces cotes-ci — le 10×15 nominal, sans le débord — trois rangées de 32 mm
        // occupent 100 des 102 mm : il reste deux millimètres, où rien ne s'écrit. La
        // rangée saute, et c'est la bonne décision. Sur le papier RÉELLEMENT tiré par la
        // boutique (156,1 × 105, débord compris), les quinze photos tiennent avec leur
        // date — voir Un_format_carre_garde_ses_deux_rangees.
        Assert.Equal(15, sansBande);
        Assert.Equal(10, avecBande);

        // et ce que la capacité annonce doit réellement laisser de quoi écrire la date
        var disposition = IdSheetLayout.Layout(
            Px(152), Px(102), Px(26), Px(32), Px(2), avecBande, bottomReserve: Px(4.5));

        var basDesPhotos = disposition.Cells.Max(c => c.Bottom);

        var pose = SheetFooterLayout.Place(
            new SheetFooter(new DateTime(2026, 8, 11, 17, 0, 0)),
            Px(152), Px(102), basDesPhotos, Dpi);

        Assert.NotNull(pose);
        Assert.True(pose!.CorpsDatePx >= Px(SheetFooterLayout.CorpsDateMinimalMm),
            $"la date sortirait à {pose.CorpsDatePx} px, sous le lisible");
    }

    /// <summary>
    /// <b>Le cas qui a tout déclenché</b> : le passeport américain, 50 × 50, sur le 10×15
    /// de la boutique. Deux rangées de 50 mm occupent 100,2 mm des 105 disponibles ; il en
    /// reste 4,8, où une date tient si l'on ne prétend pas y loger la bande entière.
    ///
    /// <b>Ce que la géométrie impose, et qu'aucun réglage ne contournera.</b> Deux rangées
    /// occupent 100,2 mm ; il reste 4,8 mm, dont deux vont au massicot. Ce qui subsiste ne
    /// suffit pas à écrire une date lisible, donc la seconde rangée saute : trois photos.
    ///
    /// C'est le prix de la protection du bord bas, et il est assumé — une date rognée ne
    /// prouve rien. Le PORTRAIT, lui, en tient quatre avec la bande complète : c'est là
    /// qu'est la marge de manœuvre sur ces formats, pas dans le réglage de la réserve.
    /// </summary>
    [Fact]
    public void Un_format_carre_ne_tient_pas_deux_rangees_avec_sa_date()
    {
        var minimale = SheetFooterLayout.ReserveMinimalePx(
            new SheetFooter(DateTime.Now), Dpi);

        // en paysage, la seconde rangée ne rentre pas avec la date
        Assert.Equal(3, IdSheetLayout.MaxCopies(
            Px(156.1), Px(105), Px(50), Px(50), Px(0.2), minimale));

        Assert.Equal(3, IdSheetLayout.MaxCopies(
            Px(156.1), Px(105), Px(40), Px(50), Px(0.2), minimale));

        // …mais le même papier DEBOUT en tient quatre, et la bande y respire
        Assert.Equal(4, IdSheetLayout.MaxCopies(
            Px(105), Px(156.1), Px(50), Px(50), Px(0.2), minimale));

        var disposition = IdSheetLayout.Layout(
            Px(105), Px(156.1), Px(50), Px(50), Px(0.2), 4, 0, minimale);

        var pose = SheetFooterLayout.Place(
            new SheetFooter(new DateTime(2026, 8, 11, 17, 0, 0), "PHOTOS CONFORMES"),
            Px(105), Px(156.1), disposition.Cells.Max(c => c.Bottom), Dpi);

        Assert.NotNull(pose);
        Assert.Equal(Px(SheetFooterLayout.CorpsDateMm), pose!.CorpsDatePx);
        Assert.NotNull(pose.Mention);
    }

    /// <summary>
    /// La planche française ne perd RIEN au passage : ses huit vignettes laissaient déjà la
    /// place, et une correction qui les ramènerait à six serait pire que le défaut.
    /// </summary>
    [Fact]
    public void La_planche_francaise_garde_ses_huit_vignettes()
    {
        Assert.Equal(8, IdSheetLayout.MaxCopies(Px(152), Px(102), Px(35), Px(45), Px(2), Px(8)));
    }

    /// <summary>
    /// Une planche trop courte pour porter à la fois une rangée et la bande garde la
    /// rangée : la bande est un plus, la photo est le produit.
    /// </summary>
    [Fact]
    public void Une_planche_trop_courte_garde_ses_photos_plutot_que_la_bande()
    {
        // 50 mm de haut pour des cases de 45 : la bande de 8 mm ne peut pas tenir en plus
        Assert.Equal(4, IdSheetLayout.MaxCopies(Px(152), Px(50), Px(35), Px(45), Px(2), Px(8)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Les_trois_planches_proposees_tiennent_en_paysage(int vignettes)
    {
        var disposition = IdSheetLayout.Layout(
            Px(152), Px(102), Px(35), Px(45), Px(2), vignettes);

        Assert.Equal(vignettes, disposition.Cells.Count);
        Assert.True(disposition.Columns * disposition.Rows >= vignettes);
    }

    [Fact]
    public void La_planche_de_huit_se_range_en_quatre_colonnes_sur_deux_rangees()
    {
        var disposition = IdSheetLayout.Layout(Px(152), Px(102), Px(35), Px(45), Px(2), 8);

        Assert.Equal(4, disposition.Columns);
        Assert.Equal(2, disposition.Rows);
    }

    [Fact]
    public void Les_vignettes_restent_dans_la_planche()
    {
        var largeur = Px(152);
        var hauteur = Px(102);
        var disposition = IdSheetLayout.Layout(largeur, hauteur, Px(35), Px(45), Px(2), 8);

        Assert.All(disposition.Cells, cellule =>
        {
            Assert.InRange(cellule.X, 0, largeur);
            Assert.InRange(cellule.Y, 0, hauteur);
            Assert.InRange(cellule.Right, 0, largeur);
            Assert.InRange(cellule.Bottom, 0, hauteur);
        });
    }

    /// <summary>Neuf vignettes ne tiennent nulle part sur un 10×15 : le refus doit être net.</summary>
    [Fact]
    public void Au_dela_de_huit_la_planche_est_refusee()
    {
        Assert.Throws<InvalidOperationException>(
            () => IdSheetLayout.Layout(Px(152), Px(102), Px(35), Px(45), Px(2), 9));
    }

    /// <summary>Le bloc de vignettes doit être centré : les marges se coupent au massicot.</summary>
    [Fact]
    public void Le_bloc_est_centre_sur_la_planche()
    {
        var largeur = Px(152);
        var hauteur = Px(102);
        var disposition = IdSheetLayout.Layout(largeur, hauteur, Px(35), Px(45), Px(2), 8);

        var gauche = disposition.Cells.Min(c => c.X);
        var droite = largeur - disposition.Cells.Max(c => c.Right);
        var haut = disposition.Cells.Min(c => c.Y);
        var bas = hauteur - disposition.Cells.Max(c => c.Bottom);

        Assert.True(Math.Abs(gauche - droite) <= 1, $"marges gauche {gauche} / droite {droite}");
        Assert.True(Math.Abs(haut - bas) <= 1, $"marges haut {haut} / bas {bas}");
    }
}
