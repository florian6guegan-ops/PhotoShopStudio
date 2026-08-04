using Studio.Printing;
using Studio.Printing.Devices.Fuji;

namespace Studio.Tests;

/// <summary>
/// Ce qu'une machine peut encore sortir, TOUS consommables confondus.
///
/// Le bandeau annonçait « ~576 × 10x15 » d'après le seul papier restant. Sur la machine B
/// du 04/08/2026 — magenta à 15 %, bac de maintenance à 38 % — ce chiffre était un
/// mensonge : l'encre s'arrête bien avant le rouleau. Un opérateur qui lance trois cents
/// tirages sur cette annonce se retrouve à mi-parcours avec une machine à l'arrêt et un
/// client devant lui.
/// </summary>
public class EstimationConsommablesTests
{
    private static readonly De100Format A4 = new("A4", "21xL", 210, 297);
    private static readonly De100Format Dix15 = new("10x15", "10x15", 102, 152);

    /// <param name="restantMm">Longueur de rouleau restante.</param>
    private static De100Media Rouleau(int largeurMm, double restantMm) =>
        new(1, "0", largeurMm, 0, De100Surface.Lustre, restantMm);

    private static De100Supplies Encres(int jaune, int magenta, int cyan, int noir, int bac) =>
        new(new("Jaune", jaune), new("Magenta", magenta), new("Cyan", cyan), new("Noir", noir),
            new("Bac de maintenance", bac), 4);

    // — ce qui limite —

    /// <summary>
    /// <b>Le cas de la machine B</b> : 44 m de rouleau, mais du magenta à 15 %. Le papier
    /// permettrait 148 A4 ; l'encre, non.
    /// </summary>
    [Fact]
    public void Une_encre_basse_l_emporte_sur_un_rouleau_plein()
    {
        var vue = EstimationConsommables.Pour(
            A4, Rouleau(210, 44_000), Encres(68, 15, 99, 27, 38));

        Assert.Equal(Limite.Encre, vue.Limite);
        Assert.Contains("magenta", vue.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(vue.Tirages < vue.ParLePapier,
            $"{vue.Tirages} annoncés contre {vue.ParLePapier} que le papier permettrait");
    }

    /// <summary>Rouleau à bout, encres pleines : c'est le papier qu'on annonce.</summary>
    [Fact]
    public void Un_rouleau_a_bout_l_emporte_sur_des_encres_pleines()
    {
        var vue = EstimationConsommables.Pour(
            A4, Rouleau(210, 900), Encres(90, 90, 90, 90, 10));

        Assert.Equal(Limite.Papier, vue.Limite);
        Assert.Equal(3, vue.Tirages); // 900 mm / 297
    }

    /// <summary>
    /// Le bac de maintenance se REMPLIT : à 95 %, il ne reste que cinq points, et c'est lui
    /// qui arrêtera la machine — même avec du papier et de l'encre à revendre.
    /// </summary>
    [Fact]
    public void Un_bac_presque_plein_arrete_avant_le_reste()
    {
        var vue = EstimationConsommables.Pour(
            A4, Rouleau(210, 44_000), Encres(90, 90, 90, 90, 98));

        Assert.Equal(Limite.BacDeMaintenance, vue.Limite);
        Assert.Contains("bac", vue.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Une encre basse se DIT même quand elle ne limite pas encore : c'est le moment de
    /// commander la cartouche, pas celui de la changer.
    /// </summary>
    [Fact]
    public void Une_encre_basse_est_annoncee_meme_quand_le_papier_limite()
    {
        var vue = EstimationConsommables.Pour(
            Dix15, Rouleau(102, 300), Encres(90, 12, 90, 90, 10));

        Assert.Equal(Limite.Papier, vue.Limite);
        Assert.Contains("magenta", vue.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sans lecture des consommables, on ne sait rien de plus que le papier.</summary>
    [Fact]
    public void Sans_consommables_seul_le_papier_compte()
    {
        var vue = EstimationConsommables.Pour(A4, Rouleau(210, 2970), supplies: null);

        Assert.Equal(Limite.Papier, vue.Limite);
        Assert.Equal(10, vue.Tirages);
        Assert.Equal(-1, vue.ParLEncre);
    }

    // — l'apprentissage —

    /// <summary>
    /// Le premier relevé ne fait que poser un repère : on ne peut rien déduire d'un seul
    /// point.
    /// </summary>
    [Fact]
    public void Le_premier_releve_ne_calibre_rien()
    {
        var vue = EstimationConsommables.Apprendre(
            null, compteur: 53_800, Encres(68, 15, 99, 27, 38), DateTimeOffset.Now);

        Assert.False(vue.Calibree);
        Assert.Equal(53_800, vue.Compteur);
        Assert.Equal(15, vue.EncreLaPlusBasse);
    }

    /// <summary>
    /// Deux relevés espacés donnent la consommation RÉELLE de cette machine-là : 200
    /// tirages ont fait descendre l'encre de 10 points, donc 20 tirages par point.
    /// </summary>
    [Fact]
    public void Deux_releves_donnent_la_consommation_reelle()
    {
        var depart = EstimationConsommables.Apprendre(
            null, 53_800, Encres(68, 25, 99, 27, 38), DateTimeOffset.Now);

        var apres = EstimationConsommables.Apprendre(
            depart, 54_000, Encres(64, 15, 95, 23, 42), DateTimeOffset.Now);

        Assert.True(apres.Calibree);
        Assert.Equal(20, apres.TiragesParPourcentDEncre, 1);   // 200 tirages / 10 points
        Assert.Equal(50, apres.TiragesParPourcentDeBac, 1);    // 200 tirages / 4 points
    }

    /// <summary>
    /// <b>Trop peu de tirages n'apprend rien.</b> Le pourcentage est un entier : sur dix
    /// tirages, son arrondi fausserait le calcul d'un facteur deux. On garde alors la
    /// calibration précédente.
    /// </summary>
    [Fact]
    public void Un_ecart_trop_court_ne_recalibre_pas()
    {
        var depart = EstimationConsommables.Apprendre(
            null, 53_800, Encres(68, 25, 99, 27, 38), DateTimeOffset.Now);
        var calibre = EstimationConsommables.Apprendre(
            depart, 54_000, Encres(64, 15, 95, 23, 42), DateTimeOffset.Now);

        var apres = EstimationConsommables.Apprendre(
            calibre, 54_010, Encres(64, 14, 95, 23, 42), DateTimeOffset.Now);

        Assert.Equal(calibre.TiragesParPourcentDEncre, apres.TiragesParPourcentDEncre, 3);
        Assert.Equal(54_010, apres.Compteur); // le repère avance quand même
    }

    /// <summary>
    /// <b>Une cartouche qu'on vient de changer remonte le niveau.</b> L'écart est alors
    /// négatif et n'apprend rien : garder la calibration vaut mieux que d'en inventer une.
    /// </summary>
    [Fact]
    public void Une_cartouche_changee_ne_fausse_pas_la_calibration()
    {
        var depart = EstimationConsommables.Apprendre(
            null, 53_800, Encres(68, 25, 99, 27, 38), DateTimeOffset.Now);
        var calibre = EstimationConsommables.Apprendre(
            depart, 54_000, Encres(64, 15, 95, 23, 42), DateTimeOffset.Now);

        // le magenta passe de 15 à 100 : cartouche neuve
        var apres = EstimationConsommables.Apprendre(
            calibre, 54_200, Encres(60, 100, 91, 19, 46), DateTimeOffset.Now);

        Assert.Equal(calibre.TiragesParPourcentDEncre, apres.TiragesParPourcentDEncre, 3);
    }

    /// <summary>
    /// Tant que la machine n'a pas été observée, l'estimation est marquée APPROXIMATIVE et
    /// le bandeau l'annonce avec un tilde. C'est une honnêteté de base : le chiffre repose
    /// alors sur une valeur par défaut, pas sur cette machine.
    /// </summary>
    [Fact]
    public void Sans_calibration_l_estimation_s_annonce_approximative()
    {
        var vue = EstimationConsommables.Pour(
            A4, Rouleau(210, 44_000), Encres(68, 15, 99, 27, 38));

        Assert.True(vue.Approximative);
        Assert.StartsWith("~", vue.Resume("A4"), StringComparison.Ordinal);
    }

    [Fact]
    public void Une_machine_observee_s_annonce_sans_tilde()
    {
        var observee = new ObservationMachine(20, 50, 54_000, 15, 42, DateTimeOffset.Now);

        var vue = EstimationConsommables.Pour(
            A4, Rouleau(210, 44_000), Encres(68, 15, 99, 27, 38), observee);

        Assert.False(vue.Approximative);
        Assert.DoesNotContain("~", vue.Resume("A4"), StringComparison.Ordinal);
    }

    /// <summary>Le résumé nomme toujours le format : c'est en photos qu'on compte au comptoir.</summary>
    [Fact]
    public void Le_resume_nomme_le_format()
    {
        var vue = EstimationConsommables.Pour(
            A4, Rouleau(210, 44_000), Encres(68, 15, 99, 27, 38));

        Assert.Contains("A4", vue.Resume("A4"), StringComparison.Ordinal);
    }
}
