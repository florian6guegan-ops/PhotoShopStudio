using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Combien de temps une commande va encore prendre.
///
/// Le bandeau disait « 12 / 24 photos sorties » et rien d'autre. L'opérateur qui a un
/// client devant lui veut savoir s'il a le temps d'en servir un autre, et cette
/// question-là n'a qu'une réponse : une durée.
///
/// <b>Maintenances comprises</b>, et c'est voulu : le DE100 s'interrompt pour nettoyer sa
/// tête et avancer son papier. Ces pauses font partie de l'attente. Le débit est donc
/// mesuré de bout en bout, elles y sont dedans.
/// </summary>
public class EstimationDureeTests
{
    // — le temps qu'il reste —

    /// <summary>Un A4 ne sort pas à la cadence d'un 10×15 : le format décide.</summary>
    [Fact]
    public void Un_grand_format_prend_plus_longtemps()
    {
        var dixQuinze = EstimationDuree.Restant(10, longueurMm: 152);
        var a4 = EstimationDuree.Restant(10, longueurMm: 297);

        Assert.True(a4 > dixQuinze,
            $"A4 {a4.TotalSeconds} s contre 10×15 {dixQuinze.TotalSeconds} s");
    }

    [Fact]
    public void Rien_a_sortir_ne_prend_aucun_temps()
    {
        Assert.Equal(TimeSpan.Zero, EstimationDuree.Restant(0, 152));
        Assert.Equal(TimeSpan.Zero, EstimationDuree.Restant(-3, 152));
    }

    /// <summary>Une cadence MESURÉE l'emporte sur la valeur par défaut du format.</summary>
    [Fact]
    public void Une_cadence_mesuree_l_emporte_sur_le_defaut()
    {
        var mesure = new DebitMesure(SecondesParTirage: 30, TiragesMesures: 100);

        var avec = EstimationDuree.Restant(10, longueurMm: 152, mesure);

        Assert.Equal(300, avec.TotalSeconds, 1);
    }

    // — comment on l'écrit —

    /// <summary>
    /// Personne n'annonce « 4 minutes 37 » : on dit « environ 5 minutes ». La précision
    /// affichée doit correspondre à la précision réelle, sans quoi elle promet ce qu'elle
    /// ne peut pas tenir.
    /// </summary>
    [Theory]
    [InlineData(20, "moins d'une minute")]
    [InlineData(70, "environ 1 minute")]
    [InlineData(277, "environ 5 minutes")]
    public void La_duree_s_ecrit_comme_on_la_dit(int secondes, string attendu)
    {
        Assert.Equal(attendu, EstimationDuree.Ecrire(TimeSpan.FromSeconds(secondes)));
    }

    /// <summary>Au-delà de dix minutes, on arrondit à cinq : « 23 minutes » serait un faux-semblant.</summary>
    [Fact]
    public void Au_dela_de_dix_minutes_on_arrondit_a_cinq()
    {
        Assert.Equal("environ 25 minutes", EstimationDuree.Ecrire(TimeSpan.FromMinutes(23)));
    }

    [Fact]
    public void Au_dela_d_une_heure_on_compte_en_heures()
    {
        Assert.Equal("environ 1 h 30", EstimationDuree.Ecrire(TimeSpan.FromMinutes(92)));
        Assert.Equal("environ 2 h", EstimationDuree.Ecrire(TimeSpan.FromMinutes(118)));
    }

    /// <summary>Une cadence mesurée n'a plus à s'excuser : « environ » disparaît.</summary>
    [Fact]
    public void Une_cadence_mesuree_s_annonce_sans_environ()
    {
        Assert.Equal("5 minutes",
            EstimationDuree.Ecrire(TimeSpan.FromMinutes(5), approximatif: false));
    }

    [Fact]
    public void Une_duree_nulle_ne_s_ecrit_pas()
    {
        Assert.Equal("", EstimationDuree.Ecrire(TimeSpan.Zero));
    }

    // — l'apprentissage —

    [Fact]
    public void Une_commande_chronometree_donne_la_cadence()
    {
        var debit = EstimationDuree.Apprendre(null, tirages: 20, TimeSpan.FromMinutes(2));

        Assert.NotNull(debit);
        Assert.Equal(6, debit!.SecondesParTirage, 1);   // 120 s / 20
        Assert.Equal(20, debit.TiragesMesures);
    }

    /// <summary>
    /// <b>Une commande d'un seul tirage n'apprend rien</b> : elle est presque entièrement
    /// faite du réveil de la machine, qui ne se reproduira pas sur les suivantes.
    /// </summary>
    [Fact]
    public void Un_tirage_seul_n_apprend_rien()
    {
        Assert.Null(EstimationDuree.Apprendre(null, tirages: 1, TimeSpan.FromSeconds(45)));
    }

    /// <summary>
    /// La moyenne est PONDÉRÉE : une commande de soixante photos pèse plus qu'une commande
    /// d'une seule.
    /// </summary>
    [Fact]
    public void La_moyenne_est_ponderee_par_le_nombre_de_tirages()
    {
        var petite = EstimationDuree.Apprendre(null, tirages: 2, TimeSpan.FromSeconds(40));   // 20 s/photo
        var grande = EstimationDuree.Apprendre(petite, tirages: 98, TimeSpan.FromSeconds(490)); // 5 s/photo

        // (20×2 + 5×98) / 100 = 5,3 — la grande commande domine, et c'est juste
        Assert.Equal(5.3, grande!.SecondesParTirage, 1);
        Assert.Equal(100, grande.TiragesMesures);
    }

    /// <summary>
    /// Une valeur aberrante — machine en panne au milieu, opérateur parti déjeuner — ne
    /// doit pas empoisonner la moyenne pour toujours.
    /// </summary>
    [Fact]
    public void Une_mesure_aberrante_est_rejetee()
    {
        var bon = EstimationDuree.Apprendre(null, tirages: 20, TimeSpan.FromMinutes(2));

        var apres = EstimationDuree.Apprendre(bon, tirages: 3, TimeSpan.FromHours(2));

        Assert.Equal(bon!.SecondesParTirage, apres!.SecondesParTirage, 3);
    }

    /// <summary>
    /// Le compte retenu est BORNÉ : au-delà, la moyenne deviendrait insensible et une
    /// machine qui ralentit — tête encrassée, papier plus épais — ne serait plus suivie.
    /// </summary>
    [Fact]
    public void Le_compte_retenu_est_borne()
    {
        var debit = new DebitMesure(6, EstimationDuree.TiragesRetenus);

        var apres = EstimationDuree.Apprendre(debit, tirages: 100, TimeSpan.FromSeconds(1000));

        Assert.Equal(EstimationDuree.TiragesRetenus, apres!.TiragesMesures);
        Assert.True(apres.SecondesParTirage > 6, "la moyenne doit encore bouger");
    }

    /// <summary>Une mesure sur trop peu de tirages ne se dit pas « fiable ».</summary>
    [Fact]
    public void Une_mesure_courte_n_est_pas_fiable()
    {
        Assert.False(new DebitMesure(6, 3).Fiable);
        Assert.True(new DebitMesure(6, EstimationDuree.TiragesPourEtreFiable).Fiable);
    }
}
