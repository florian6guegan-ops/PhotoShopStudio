using Studio.Printing.Devices.Fuji;

namespace Studio.Tests;

/// <summary>
/// Formats du DE100 et estimation des tirages restants. La table vient du pilote de
/// DiLand ; l'estimation, elle, n'existe pas chez lui (il annonce une quantité illimitée).
/// </summary>
public class De100FormatsTests
{
    [Fact]
    public void Le_catalogue_couvre_les_largeurs_de_rouleau_de_la_boutique()
    {
        var largeurs = De100Formats.All.Select(f => f.ShortSideMm).Distinct().OrderBy(w => w);

        Assert.Equal([89, 102, 127, 152, 203], largeurs);
    }

    [Theory]
    [InlineData(102, "10x15")]
    [InlineData(127, "13x18")]
    [InlineData(152, "15x20")]
    [InlineData(203, "20x30")]
    public void Un_rouleau_propose_les_formats_de_sa_largeur(int largeurRouleau, string formatAttendu)
    {
        var formats = De100Formats.ForPaperWidth(largeurRouleau).Select(f => f.Name);

        Assert.Contains(formatAttendu, formats);
    }

    [Fact]
    public void Un_rouleau_ne_propose_pas_les_formats_d_une_autre_largeur()
    {
        var formats = De100Formats.ForPaperWidth(102).Select(f => f.Name).ToList();

        Assert.DoesNotContain("13x18", formats);
        Assert.DoesNotContain("20x30", formats);
    }

    /// <summary>Un format est aussi tirable quand la largeur du rouleau est son grand côté.</summary>
    [Fact]
    public void Un_format_est_tirable_dans_les_deux_sens()
    {
        var surRouleau152 = De100Formats.ForPaperWidth(152).Select(f => f.Name).ToList();

        // le 10x15 a 152 mm pour longueur : il sort donc aussi d'un rouleau de 152
        Assert.Contains("10x15", surRouleau152);
    }

    [Fact]
    public void Le_nombre_de_tirages_suit_la_longueur_restante()
    {
        var dixQuinze = De100Formats.All.First(f => f.Name == "10x15");

        // rouleau de 102 mm : le tirage consomme ses 152 mm de long
        Assert.Equal(65, De100Formats.EstimatePrints(dixQuinze, 10_000, paperWidthMm: 102));
    }

    /// <summary>
    /// Le même format posé en travers consomme sa petite dimension : sur un rouleau de
    /// 152 mm, un 10×15 ne mange que 102 mm de longueur.
    /// </summary>
    [Fact]
    public void Un_format_pose_en_travers_consomme_sa_petite_dimension()
    {
        var dixQuinze = De100Formats.All.First(f => f.Name == "10x15");

        Assert.Equal(152, De100Formats.ConsumedLengthMm(dixQuinze, paperWidthMm: 102));
        Assert.Equal(102, De100Formats.ConsumedLengthMm(dixQuinze, paperWidthMm: 152));
        Assert.Equal(98, De100Formats.EstimatePrints(dixQuinze, 10_000, paperWidthMm: 152));
    }

    [Fact]
    public void Un_tirage_incomplet_n_est_pas_compte()
    {
        var dixQuinze = De100Formats.All.First(f => f.Name == "10x15");

        Assert.Equal(1, De100Formats.EstimatePrints(dixQuinze, 300, paperWidthMm: 102));
        Assert.Equal(0, De100Formats.EstimatePrints(dixQuinze, 151, paperWidthMm: 102));
    }

    /// <summary>
    /// Le relevé réel du 31/07/2026 : rouleau lustré de 152 mm, 34 470 mm restants.
    /// Ces chiffres sont ceux que la machine a donnés, ils servent de repère.
    /// </summary>
    [Fact]
    public void Le_releve_reel_du_rouleau_lustre_donne_des_comptes_coherents()
    {
        var estimation = De100Formats.Estimate(152, 34_470).ToDictionary(e => e.Format.Name);

        Assert.Equal(337, estimation["10x15"].RemainingPrints);   // 102 mm par tirage
        Assert.Equal(226, estimation["15x15"].RemainingPrints);   // 152 mm par tirage
        Assert.Equal(169, estimation["15x20"].RemainingPrints);   // 203 mm par tirage
        Assert.Equal(113, estimation["15x30"].RemainingPrints);   // 304 mm par tirage
    }

    [Fact]
    public void Sans_papier_il_ne_reste_aucun_tirage()
    {
        var estimation = De100Formats.Estimate(102, 0);

        Assert.NotEmpty(estimation);
        Assert.All(estimation, e => Assert.Equal(0, e.RemainingPrints));
    }

    [Fact]
    public void Une_longueur_negative_est_traitee_comme_zero()
    {
        var estimation = De100Formats.Estimate(102, -500);

        Assert.All(estimation, e => Assert.Equal(0, e.RemainingPrints));
    }

    /// <summary>Un grand format consomme plus de papier : il en reste forcément moins.</summary>
    [Fact]
    public void Les_grands_formats_s_epuisent_plus_vite()
    {
        var estimation = De100Formats.Estimate(102, 10_000).ToDictionary(e => e.Format.Name);

        Assert.True(estimation["10x15"].RemainingPrints > estimation["10x20"].RemainingPrints);
        Assert.True(estimation["10x13"].RemainingPrints > estimation["10x15"].RemainingPrints);
    }

    [Fact]
    public void L_estimation_est_classee_du_format_le_plus_econome_au_plus_gourmand()
    {
        var estimation = De100Formats.Estimate(127, 5_000);

        var consommations = estimation.Select(e => De100Formats.ConsumedLengthMm(e.Format, 127)).ToList();
        Assert.Equal(consommations.OrderBy(l => l), consommations);
    }

    [Fact]
    public void Une_largeur_inconnue_ne_propose_aucun_format()
    {
        Assert.Empty(De100Formats.Estimate(999, 10_000));
    }

    [Fact]
    public void Les_formats_a_longueur_libre_sont_signales()
    {
        var variables = De100Formats.ForPaperWidth(102).Where(f => f.IsVariable).Select(f => f.Name);

        Assert.Contains("10xS", variables);
        Assert.Contains("10xL", variables);
    }
}

/// <summary>Lecture des consommables du DE100.</summary>
public class De100SuppliesTests
{
    private static De100Supplies Niveaux(int jaune, int magenta, int cyan, int noir, int maintenance) =>
        new(new De100Supply("Jaune", jaune),
            new De100Supply("Magenta", magenta),
            new De100Supply("Cyan", cyan),
            new De100Supply("Noir", noir),
            new De100Supply("Bac de maintenance", maintenance),
            InkCount: 4);

    [Fact]
    public void Les_encres_sont_dans_l_ordre_du_SDK()
    {
        var supplies = Niveaux(73, 21, 5, 37, 42);

        Assert.Equal(["Jaune", "Magenta", "Cyan", "Noir"], supplies.Inks.Select(i => i.Name));
        Assert.Equal([73, 21, 5, 37], supplies.Inks.Select(i => i.Level));
    }

    /// <summary>Le cas qui compte : repérer l'encre qui va manquer avant qu'elle ne bloque un tirage.</summary>
    [Fact]
    public void Les_encres_basses_sont_reperees()
    {
        var supplies = Niveaux(73, 21, 5, 37, 42);

        var basses = supplies.InksBelow(25).Select(i => i.Name).ToList();

        Assert.Equal(["Magenta", "Cyan"], basses);
    }

    [Fact]
    public void Aucune_encre_basse_quand_tout_va_bien()
    {
        Assert.Empty(Niveaux(90, 85, 80, 95, 10).InksBelow(25));
    }

    [Fact]
    public void Le_bac_de_maintenance_n_est_pas_compte_parmi_les_encres()
    {
        var supplies = Niveaux(73, 21, 5, 37, 42);

        Assert.DoesNotContain(supplies.MaintenanceTank, supplies.Inks);
        Assert.Equal(42, supplies.MaintenanceTank.Level);
    }
}
