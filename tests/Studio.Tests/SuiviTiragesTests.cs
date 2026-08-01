using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Le rattachement d'un tirage sorti à sa commande.
///
/// Le minilab ne rappelle que l'identifiant fabriqué à l'envoi. Le numéro de commande y
/// contient lui-même un tiret (« 01-016 »), ce qui interdit de découper naïvement — et
/// sans ce rattachement, aucune commande ne saurait jamais qu'elle est terminée.
/// </summary>
public class SuiviTiragesTests
{
    [Theory]
    [InlineData("01-016", 1, 3, "01-016-1-003")]
    [InlineData("01-016", 12, 120, "01-016-12-120")]
    [InlineData("31-002", 1, 1, "31-002-1-001")]
    public void L_identifiant_porte_la_commande_l_enveloppe_et_le_rang(
        string numero, int enveloppe, int rang, string attendu) =>
        Assert.Equal(attendu, PrintOrchestrator.MinilabJobId(numero, enveloppe, rang));

    [Theory]
    [InlineData("01-016-1-003", "01-016")]
    [InlineData("01-016-12-120", "01-016")]
    [InlineData("31-002-1-001", "31-002")]
    public void Le_numero_de_commande_se_relit_dans_l_identifiant(string jobId, string attendu) =>
        Assert.Equal(attendu, PrintOrchestrator.OrderNumberOf(jobId));

    /// <summary>Aller-retour : ce qu'on fabrique doit toujours se relire.</summary>
    [Fact]
    public void Tout_identifiant_fabrique_se_relit()
    {
        foreach (var numero in new[] { "01-001", "31-999", "07-042" })
            for (var rang = 1; rang <= 3; rang++)
                Assert.Equal(numero,
                    PrintOrchestrator.OrderNumberOf(PrintOrchestrator.MinilabJobId(numero, 2, rang)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bricole")]
    [InlineData("01-016")]
    public void Un_identifiant_qui_ne_vient_pas_de_nous_est_ignore(string jobId) =>
        Assert.Null(PrintOrchestrator.OrderNumberOf(jobId));
}
