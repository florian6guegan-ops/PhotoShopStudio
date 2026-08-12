using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le montage des agrandissements : plusieurs tirages du même format composés sur une seule
/// feuille grand format, que l'opérateur massicote.
///
/// Les feuilles sont celles du catalogue des trois boutiques. Les capacités attendues ont
/// été recalculées à la main, en pixels à 300 ppp et avec les 2 mm d'écart entre cases —
/// exactement ce que la disposition finale emploiera.
/// </summary>
public class MontageFeuilleTests
{
    private static readonly PaperOption Trente40 = new("30x40", "30×40", 300, 400);
    private static readonly PaperOption Quarante50 = new("40x50", "40×50", 400, 500);
    private static readonly PaperOption Quarante60 = new("40x60", "40×60", 400, 600);
    private static readonly PaperOption Cinquante70 = new("50x70", "50×70", 500, 700);

    private static readonly PaperOption[] Catalogue =
        [Trente40, Quarante50, Quarante60, Cinquante70];

    // le format de la demande : 24 × 30 cm
    private const double CelluleW = 240;
    private const double CelluleH = 300;

    // — F1 : les capacités annoncées par le plan —

    /// <summary>
    /// L'exemple de l'exploitant : « si j'ai 2 24×30 ».
    ///
    /// ⚠ <b>C'est la FEUILLE qui tourne, pas la case</b>, et c'est une bonne nouvelle.
    /// Debout, la feuille ne porte qu'un tirage : 300 + 300 = 600 mm la remplissent au
    /// millimètre près, et l'écart de 2 mm déborde. Couchée, elle offre 600 mm de large où
    /// deux tirages de 240 se posent DEBOUT, côte à côte — le cadrage portrait est donc
    /// gardé tel quel, et le fichier sort en 60 × 40.
    ///
    /// Le plan d'implémentation annonçait la case couchée : c'était une erreur de calcul à
    /// la main, oubliant que <c>Capacity</c> essaie aussi les deux sens de la feuille.
    /// </summary>
    [Fact]
    public void Deux_vingtquatre_trente_tiennent_sur_un_quarante_soixante()
    {
        var plan = MontageFeuille.Pour(Quarante60, CelluleW, CelluleH);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.ParFeuille);

        Assert.False(plan.CelluleTournee, "les tirages restent debout");
        Assert.True(plan.FeuilleTournee, "c'est la feuille qui se couche");

        // la feuille est donc composée en 60 × 40
        Assert.Equal(600, plan.LargeurMm);
        Assert.Equal(400, plan.HauteurMm);
    }

    /// <summary>
    /// Un 30×40 ne porte qu'un seul 24×30 : il n'y a rien à monter, et le plan est nul.
    /// C'est la garde qui préserve le comportement d'avant.
    /// </summary>
    [Fact]
    public void Une_seule_place_ne_donne_aucun_plan()
    {
        Assert.Null(MontageFeuille.Pour(Trente40, CelluleW, CelluleH));
    }

    /// <summary>
    /// Un 50×70 en porte QUATRE : deux colonnes de 240 mm (482 avec l'écart, dans 500) et
    /// deux rangées de 300 (602 dans 700). Le plan d'implémentation en annonçait deux, en
    /// croyant que 480 ne rentrait pas dans 500 — il y rentre, l'écart compris.
    /// </summary>
    [Fact]
    public void Quatre_places_sur_un_cinquante_soixante_dix()
    {
        var plan = MontageFeuille.Pour(Cinquante70, CelluleW, CelluleH);

        Assert.Equal(4, plan!.ParFeuille);
        Assert.False(plan.CelluleTournee);
        Assert.False(plan.FeuilleTournee);
    }

    /// <summary>Un format plus grand que la feuille ne se monte nulle part.</summary>
    [Fact]
    public void Un_format_trop_grand_ne_donne_aucun_plan()
    {
        Assert.Null(MontageFeuille.Pour(Trente40, 500, 700));
    }

    // — F4 : rien ne bouge tant qu'il n'y a rien à gagner —

    /// <summary>
    /// ⚠ L'essai qui protège tous les postes qui ne demandent rien : un format qui ne tient
    /// qu'une fois par feuille ne doit engendrer AUCUN candidat, donc aucun écran, donc aucun
    /// changement de rendu.
    /// </summary>
    [Fact]
    public void Aucun_candidat_quand_le_format_ne_tient_quune_fois()
    {
        // un 40×50 : il ne tient deux fois sur aucune feuille du catalogue
        var candidats = MontageFeuille.Candidats(Catalogue, 400, 500);

        Assert.Empty(candidats);
    }

    /// <summary>
    /// Le 40×50 arrive avant le 40×60 : il porte les mêmes deux tirages sur 100 cm² de
    /// moins. C'est tout l'intérêt de trier par surface — la première ligne de l'écran est
    /// celle qui gâche le moins.
    /// </summary>
    [Fact]
    public void Les_candidats_sortent_la_plus_petite_feuille_dabord()
    {
        var candidats = MontageFeuille.Candidats(Catalogue, CelluleW, CelluleH);

        Assert.NotEmpty(candidats);
        Assert.Equal("40x50", candidats[0].Feuille.Code);
        Assert.Equal(2, candidats[0].ParFeuille);

        // la plus petite d'abord : à nombre de places égal, c'est elle qui gâche le moins
        var surfaces = candidats.Select(c => c.Feuille.AreaMm2).ToList();
        Assert.Equal(surfaces.OrderBy(s => s).ToList(), surfaces);
    }

    /// <summary>La feuille elle-même n'est jamais candidate à son propre montage.</summary>
    [Fact]
    public void Chaque_candidat_porte_au_moins_deux_tirages()
    {
        foreach (var candidat in MontageFeuille.Candidats(Catalogue, CelluleW, CelluleH))
            Assert.True(candidat.ParFeuille >= MontageFeuille.MinimumUtile);
    }

    // — le compte des feuilles —

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public void Le_nombre_de_feuilles_suit_les_tirages(int tirages, int attendu)
    {
        var plan = MontageFeuille.Pour(Quarante60, CelluleW, CelluleH);

        Assert.Equal(attendu, plan!.Feuilles(tirages));
    }

    [Fact]
    public void Les_places_perdues_sont_celles_de_la_derniere_feuille()
    {
        var plan = MontageFeuille.Pour(Quarante60, CelluleW, CelluleH);

        Assert.Equal(0, plan!.PlacesPerdues(2));
        Assert.Equal(1, plan.PlacesPerdues(3));
        Assert.Equal(0, plan.PlacesPerdues(4));
    }

    // — l'empreinte, et pourquoi elle n'est pas la taille du tirage —

    /// <summary>
    /// Case non couchée : l'empreinte est celle du tirage, au pixel près. C'est le cas de
    /// tous les montages du catalogue réel — la feuille tourne, la case non.
    /// </summary>
    [Fact]
    public void Lempreinte_dune_case_droite_est_celle_du_tirage()
    {
        var plan = MontageFeuille.Pour(Quarante60, CelluleW, CelluleH);
        var (largeur, hauteur) = MontageFeuille.EmpreintePixels(plan!, CelluleW, CelluleH, 300);

        Assert.False(plan!.CelluleTournee);
        Assert.Equal(MmPx.ToPixels(CelluleW, 300), largeur);
        Assert.Equal(MmPx.ToPixels(CelluleH, 300), hauteur);
    }

    /// <summary>
    /// ⚠ Quand la case EST couchée, l'empreinte est la transposée du tirage. C'est CETTE
    /// taille que la grille occupe ; la photo, elle, reste rendue à son format et n'est que
    /// tournée à la pose. Confondre les deux recadrerait un portrait en paysage.
    ///
    /// L'invariant vaut pour toutes les feuilles et tous les formats : l'empreinte est le
    /// tirage, ou sa transposée, jamais autre chose. Une empreinte d'une autre taille
    /// rendrait un tirage qui n'est pas celui vendu.
    /// </summary>
    [Theory]
    [InlineData(240, 300)]
    [InlineData(100, 300)]
    [InlineData(300, 100)]
    [InlineData(200, 200)]
    public void Lempreinte_est_le_tirage_ou_sa_transposee(double largeurMm, double hauteurMm)
    {
        foreach (var feuille in Catalogue)
        {
            var plan = MontageFeuille.Pour(feuille, largeurMm, hauteurMm);
            if (plan is null) continue;

            var (largeur, hauteur) = MontageFeuille.EmpreintePixels(plan, largeurMm, hauteurMm, 300);
            var (droit, couche) = (MmPx.ToPixels(largeurMm, 300), MmPx.ToPixels(hauteurMm, 300));

            var attendu = plan.CelluleTournee ? (couche, droit) : (droit, couche);
            Assert.Equal(attendu, (largeur, hauteur));
        }
    }

    // — F2 : la répartition sur plusieurs feuilles —

    /// <summary>
    /// Trois photos en deux exemplaires, deux par feuille : les exemplaires d'une même photo
    /// restent groupés et le débordement passe à la feuille suivante.
    /// </summary>
    [Fact]
    public void La_repartition_garde_les_exemplaires_groupes()
    {
        var feuilles = CustomSheetLayout.Distribute([2, 2, 2], perSheet: 2);

        Assert.Equal(3, feuilles.Count);
        Assert.All(feuilles, f => Assert.Equal(2, f.Sum(c => c.Copies)));
        Assert.Equal(0, feuilles[0][0].PhotoIndex);
        Assert.Equal(1, feuilles[1][0].PhotoIndex);
        Assert.Equal(2, feuilles[2][0].PhotoIndex);
    }

    [Fact]
    public void Une_photo_a_cheval_deborde_sur_la_feuille_suivante()
    {
        // 3 exemplaires de la première photo, 1 de la seconde, deux places par feuille
        var feuilles = CustomSheetLayout.Distribute([3, 1], perSheet: 2);

        Assert.Equal(2, feuilles.Count);
        Assert.Equal(2, feuilles[0].Sum(c => c.Copies));
        Assert.Equal(2, feuilles[1].Sum(c => c.Copies));
    }
}
