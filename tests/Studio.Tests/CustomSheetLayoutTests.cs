using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le format « personnalisé » : l'opérateur donne une taille, le logiciel cherche le papier
/// qui coûte le MOINS CHER au client.
///
/// Les papiers et les prix sont ceux de la boutique. Les capacités attendues ont été
/// recalculées à la main, en pixels à 300 ppp et avec les 2 mm d'écart entre cases —
/// exactement ce que la disposition finale emploiera.
/// </summary>
public class CustomSheetLayoutTests
{
    private static readonly PaperOption Huit10 = new("8x10", "8x10", 80, 102, UnitPrice: 0.60m);
    private static readonly PaperOption Dix15 = new("10x15", "10x15", 102, 152, UnitPrice: 0.60m);
    private static readonly PaperOption Treize18 = new("13x18", "13x18", 127, 180, UnitPrice: 1.50m);
    private static readonly PaperOption Quinze20 = new("15x20", "15x20", 152, 203, UnitPrice: 1.90m);
    private static readonly PaperOption Vingt30 = new("20x30", "20x30", 203, 307, UnitPrice: 7.50m);

    private static readonly PaperOption[] Catalogue = [Huit10, Dix15, Treize18, Quinze20, Vingt30];

    // la taille de l'exemple : 5,5 × 8 cm
    private const double CelluleW = 55;
    private const double CelluleH = 80;

    // — les trois exemples donnés par l'exploitant le 02/08/2026 —

    /// <summary>
    /// Une seule photo : le 8×10 et le 10×15 coûtent le même prix, on prend le plus petit.
    /// Sortir un 10×15 pour une photo de 5,5×8 gâcherait du papier sans rien rapporter.
    /// </summary>
    [Fact]
    public void Une_photo_sort_sur_le_plus_petit_papier_a_prix_egal()
    {
        var plan = CustomSheetLayout.Choose(1, CelluleW, CelluleH, Catalogue);

        Assert.Equal("8x10", plan!.Paper.Code);
        Assert.Equal(1, plan.Sheets);
        Assert.Equal(0.60m, plan.Paper.TotalPrice(plan.Sheets));
    }

    [Fact]
    public void Deux_photos_tiennent_sur_un_seul_dix_quinze()
    {
        var plan = CustomSheetLayout.Choose(2, CelluleW, CelluleH, Catalogue);

        Assert.Equal("10x15", plan!.Paper.Code);
        Assert.Equal(1, plan.Sheets);

        // deux 8×10 coûteraient 1,20 € pour le même résultat
        Assert.Equal(0.60m, plan.Paper.TotalPrice(plan.Sheets));
    }

    /// <summary>
    /// Le cas qui condamne l'ancienne règle. Quatre photos tiennent sur UN 13×18, qui
    /// consomme moins de papier que deux 10×15 — mais coûte 1,50 € contre 1,20 €. C'est le
    /// prix qui décide, et il faut donc sortir deux 10×15.
    /// </summary>
    [Fact]
    public void Quatre_photos_sortent_sur_deux_dix_quinze_et_non_sur_un_treize_dix_huit()
    {
        var plan = CustomSheetLayout.Choose(4, CelluleW, CelluleH, Catalogue);

        Assert.Equal("10x15", plan!.Paper.Code);
        Assert.Equal(2, plan.Sheets);
        Assert.Equal(1.20m, plan.Paper.TotalPrice(plan.Sheets));

        // la planche unique existait pourtant, et l'ancienne règle la prenait
        var (surTreize18, _) = CustomSheetLayout.CapacityOf(Treize18, CelluleW, CelluleH);
        Assert.True(surTreize18 >= 4);
    }

    /// <summary>
    /// Une photo trop large pour le 10×15 monte d'elle-même au format au-dessus : ce n'est
    /// pas une règle de plus, c'est la seule possibilité.
    /// </summary>
    [Fact]
    public void Une_photo_de_douze_sur_quinze_monte_au_treize_dix_huit()
    {
        var plan = CustomSheetLayout.Choose(1, 120, 150, Catalogue);

        Assert.Equal("13x18", plan!.Paper.Code);

        var (surDix15, _) = CustomSheetLayout.CapacityOf(Dix15, 120, 150);
        Assert.Equal(0, surDix15);
    }

    [Fact]
    public void Le_moins_cher_l_emporte_meme_avec_plus_de_planches()
    {
        var plan = CustomSheetLayout.Choose(5, CelluleW, CelluleH, Catalogue);

        // trois 10×15 à 0,60 € = 1,80 €, contre deux 13×18 à 1,50 € = 3,00 €
        Assert.Equal("10x15", plan!.Paper.Code);
        Assert.Equal(3, plan.Sheets);
        Assert.Equal(1.80m, plan.Paper.TotalPrice(plan.Sheets));
    }

    /// <summary>
    /// Les paliers dégressifs se comptent en PLANCHES, puisque c'est la planche qu'on
    /// facture. Un papier qui devient très avantageux en quantité doit pouvoir l'emporter.
    /// </summary>
    [Fact]
    public void Les_paliers_degressifs_du_papier_sont_appliques()
    {
        var brade = Dix15 with
        {
            PriceTiers =
            [
                new PriceTier { FromQuantity = 1, UnitPrice = 0.60m },
                new PriceTier { FromQuantity = 3, UnitPrice = 0.10m },
            ],
        };

        Assert.Equal(1.20m, brade.TotalPrice(2)); // sous le palier
        Assert.Equal(0.30m, brade.TotalPrice(3)); // palier atteint
    }

    // — papier imposé par l'opérateur —

    [Fact]
    public void Un_papier_impose_l_emporte_sur_le_calcul()
    {
        var plan = CustomSheetLayout.Choose(4, CelluleW, CelluleH, Catalogue,
            forcedPaperCode: "13x18");

        Assert.Equal("13x18", plan!.Paper.Code);
        Assert.Equal(1, plan.Sheets);
    }

    [Fact]
    public void Un_papier_impose_trop_petit_ne_donne_aucun_plan()
    {
        // le 10×15 ne peut pas porter une photo de 12 × 15 cm
        var plan = CustomSheetLayout.Choose(1, 120, 150, Catalogue, forcedPaperCode: "10x15");

        Assert.Null(plan);
    }

    [Fact]
    public void Un_papier_impose_inconnu_ne_donne_aucun_plan()
    {
        var plan = CustomSheetLayout.Choose(1, CelluleW, CelluleH, Catalogue,
            forcedPaperCode: "n-existe-pas");

        Assert.Null(plan);
    }

    /// <summary>
    /// Sans prix connu — un catalogue mal renseigné — on retombe sur la surface consommée :
    /// un papier à 0,00 € l'emporterait toujours et viderait la caisse.
    /// </summary>
    [Fact]
    public void Sans_prix_connu_on_retombe_sur_la_surface_consommee()
    {
        PaperOption[] sansPrix =
        [
            Huit10 with { UnitPrice = 0 },
            Dix15 with { UnitPrice = 0 },
            Treize18 with { UnitPrice = 0 },
        ];

        var plan = CustomSheetLayout.Choose(4, CelluleW, CelluleH, sansPrix);

        // 1 × 13×18 (22 860 mm²) contre 2 × 10×15 (31 008 mm²)
        Assert.Equal("13x18", plan!.Paper.Code);
        Assert.Equal(1, plan.Sheets);
    }

    /// <summary>
    /// Deux photos, pas quatre : deux colonnes de 55 mm demanderaient 112 mm avec l'écart,
    /// et le 10×15 n'en fait que 102. C'est le genre de compte qu'on croit évident et qu'on
    /// rate de dix millimètres.
    ///
    /// Elles tiennent en tournant LA PLANCHE, pas la case : le cadrage saisi est conservé.
    /// Ces tests vérifiaient l'inverse jusqu'au 03/08/2026, quand la boutique a signalé des
    /// photos « coupées dans le mauvais sens ».
    /// </summary>
    [Fact]
    public void Un_dix_quinze_ne_porte_que_deux_cases_de_cette_taille()
    {
        var (parPlanche, cellulePivotee, plancheTournee) =
            CustomSheetLayout.CapacityDetaillee(Dix15, CelluleW, CelluleH);

        Assert.Equal(2, parPlanche);
        Assert.False(cellulePivotee);
        Assert.True(plancheTournee);
    }

    /// <summary>
    /// Une case de 40 × 90 : trois places sur un 10×15, obtenues en tournant la planche
    /// plutôt que la case.
    /// </summary>
    [Fact]
    public void La_case_est_essayee_dans_les_deux_sens()
    {
        var (parPlanche, cellulePivotee, plancheTournee) =
            CustomSheetLayout.CapacityDetaillee(Dix15, 40, 90);

        Assert.Equal(3, parPlanche);
        Assert.False(cellulePivotee);
        Assert.True(plancheTournee);
    }

    [Fact]
    public void A_egalite_la_case_garde_le_sens_demande()
    {
        var (_, pivotee) = CustomSheetLayout.CapacityOf(Dix15, 50, 50);

        Assert.False(pivotee);
    }

    /// <summary>
    /// Au-delà de ce qu'un seul papier accepte, on ne refuse pas : on tire plusieurs
    /// planches, sur le format qui revient le moins cher au total.
    /// </summary>
    [Fact]
    public void Une_grosse_quantite_donne_plusieurs_planches()
    {
        var plan = CustomSheetLayout.Choose(40, CelluleW, CelluleH, Catalogue);

        Assert.NotNull(plan);
        Assert.True(plan!.Sheets > 1);
        Assert.True(plan.Sheets * plan.PerSheet >= 40);

        // aucun autre papier ne doit revenir moins cher
        var retenu = plan.Paper.TotalPrice(plan.Sheets);
        foreach (var papier in Catalogue)
        {
            var (parPlanche, _) = CustomSheetLayout.CapacityOf(papier, CelluleW, CelluleH);
            if (parPlanche < 1) continue;

            var planches = (int)Math.Ceiling(40.0 / parPlanche);
            Assert.True(papier.TotalPrice(planches) >= retenu,
                $"{papier.Name} coûterait moins que {plan.Paper.Name}");
        }
    }

    [Fact]
    public void Une_photo_plus_grande_que_tous_les_papiers_est_refusee_sans_exception()
    {
        var plan = CustomSheetLayout.Choose(1, 400, 500, Catalogue);

        Assert.Null(plan);
    }

    [Fact]
    public void Une_quantite_nulle_est_une_erreur_de_programmation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CustomSheetLayout.Choose(0, CelluleW, CelluleH, Catalogue));
    }

    [Fact]
    public void Sans_papier_propose_il_n_y_a_pas_de_plan()
    {
        Assert.Null(CustomSheetLayout.Choose(1, CelluleW, CelluleH, []));
    }

    /// <summary>
    /// La case rendue doit être dans le sens que le comptage a retenu : la rendre debout
    /// alors que les places ont été comptées couchées ferait déborder la grille.
    /// </summary>
    [Fact]
    public void La_case_rendue_suit_le_sens_du_plan()
    {
        var plan = new CustomSheetPlan(Dix15, 1, 3, CellRotated: true);
        var (largeur, hauteur) = CustomSheetLayout.CellPixels(plan, 40, 90);

        Assert.True(largeur > hauteur);
        Assert.Equal(MmPx.ToPixels(90, 300), largeur);
        Assert.Equal(MmPx.ToPixels(40, 300), hauteur);
    }

    // — répartition sur les planches —

    [Fact]
    public void Les_exemplaires_remplissent_la_planche_puis_debordent_sur_la_suivante()
    {
        // 3 + 2 photos, 4 places par planche
        var planches = CustomSheetLayout.Distribute([3, 2], perSheet: 4);

        Assert.Equal(2, planches.Count);
        Assert.Equal([(0, 3), (1, 1)], planches[0]);
        Assert.Equal([(1, 1)], planches[1]);
    }

    [Fact]
    public void Une_seule_planche_quand_tout_tient()
    {
        var planches = CustomSheetLayout.Distribute([2, 2], perSheet: 6);

        Assert.Single(planches);
        Assert.Equal([(0, 2), (1, 2)], planches[0]);
    }

    [Fact]
    public void Toutes_les_cases_demandees_sont_posees()
    {
        var quantites = new[] { 5, 1, 7, 3 };
        var planches = CustomSheetLayout.Distribute(quantites, perSheet: 4);

        Assert.Equal(quantites.Sum(), planches.Sum(p => p.Sum(c => c.Copies)));
        Assert.All(planches, p => Assert.True(p.Sum(c => c.Copies) <= 4));
    }

    [Fact]
    public void Une_photo_a_zero_exemplaire_ne_prend_pas_de_place()
    {
        var planches = CustomSheetLayout.Distribute([0, 2], perSheet: 4);

        Assert.Single(planches);
        Assert.Equal([(1, 2)], planches[0]);
    }

    [Fact]
    public void Le_nombre_de_places_perdues_est_annonce()
    {
        var plan = new CustomSheetPlan(Treize18, 2, 6, CellRotated: false);

        Assert.Equal(5, plan.WastedCells(7));
    }
}
