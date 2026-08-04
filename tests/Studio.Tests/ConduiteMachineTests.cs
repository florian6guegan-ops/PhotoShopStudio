using Studio.Printing;
using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;

namespace Studio.Tests;

/// <summary>
/// La conduite à tenir devant chaque état de machine.
///
/// Les états étaient traduits en français à trois endroits différents, et aucun ne disait
/// ce qu'il FALLAIT FAIRE. Un opérateur devant « erreur signalée par le minilab » ou
/// « Intervention nécessaire » ne savait ni s'il devait attendre, ni s'il devait toucher la
/// machine, ni si sa commande était perdue. Demandé par l'exploitant le 04/08/2026, après
/// une journée passée à deviner.
/// </summary>
public class ConduiteMachineTests
{
    // — minilab —

    [Theory]
    [InlineData(De100PrinterStatus.Ready, Conduite.Continuer)]
    [InlineData(De100PrinterStatus.Printing, Conduite.Patienter)]
    [InlineData(De100PrinterStatus.Busy, Conduite.Patienter)]
    [InlineData(De100PrinterStatus.Sleep, Conduite.Patienter)]
    [InlineData(De100PrinterStatus.ErrorProcessingCanBeContinued, Conduite.MettreEnAttente)]
    [InlineData(De100PrinterStatus.ErrorProcessingCannotBeContinued, Conduite.Arreter)]
    [InlineData(De100PrinterStatus.Offline, Conduite.MettreEnAttente)]
    public void Chaque_etat_du_minilab_a_sa_conduite(De100PrinterStatus etat, Conduite attendue)
    {
        Assert.Equal(attendue, ConduiteMachine.PourLeMinilab(etat).Conduite);
    }

    /// <summary>
    /// <b>Une machine en veille n'est PAS une panne.</b> C'est l'état normal d'une machine
    /// peu sollicitée — la A de la boutique y passe ses journées — et la déclarer en panne
    /// enverrait l'opérateur chercher un problème qui n'existe pas.
    /// </summary>
    [Fact]
    public void La_veille_n_est_pas_une_panne()
    {
        var consigne = ConduiteMachine.PourLeMinilab(De100PrinterStatus.Sleep);

        Assert.Equal(Conduite.Patienter, consigne.Conduite);
        Assert.Contains("réveille", consigne.Geste, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Quand la machine DIT quelque chose, on le répète mot pour mot : c'est elle qui sait,
    /// et c'est le même texte que sur son écran.
    /// </summary>
    [Fact]
    public void Le_motif_de_la_machine_est_repete_tel_quel()
    {
        var consigne = ConduiteMachine.PourUnTirage(
            De100OrderStatus.Error, "Paper size mismatch. Load the correct paper.");

        Assert.Equal(Conduite.Arreter, consigne.Conduite);
        Assert.Contains("Paper size mismatch", consigne.Geste, StringComparison.Ordinal);
    }

    /// <summary>
    /// LE cas du 21×29,7 : refus sans le moindre motif. On ne peut pas se taire — on nomme
    /// la seule piste qui s'est vérifiée, la définition de l'image.
    /// </summary>
    [Fact]
    public void Un_refus_sans_motif_nomme_quand_meme_la_piste()
    {
        var consigne = ConduiteMachine.PourUnTirage(De100OrderStatus.Error);

        Assert.Equal(Conduite.Arreter, consigne.Conduite);
        Assert.Contains("définition", consigne.Geste, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(De100OrderStatus.Complete, Conduite.Continuer)]
    [InlineData(De100OrderStatus.Printing, Conduite.Patienter)]
    [InlineData(De100OrderStatus.Hold, Conduite.MettreEnAttente)]
    [InlineData(De100OrderStatus.Busy, Conduite.MettreEnAttente)]
    [InlineData(De100OrderStatus.Canceled, Conduite.Arreter)]
    public void Chaque_issue_de_tirage_a_sa_conduite(De100OrderStatus etat, Conduite attendue)
    {
        Assert.Equal(attendue, ConduiteMachine.PourUnTirage(etat).Conduite);
    }

    /// <summary>Un avertissement n'arrête rien : le capot qu'on referme se règle seul.</summary>
    [Fact]
    public void Un_avertissement_machine_fait_patienter_sans_alarmer()
    {
        var consigne = ConduiteMachine.PourUnEvenement(
            De100ErrorLevel.Warning, "Cartridge cover (left) open.");

        Assert.Equal(Conduite.Patienter, consigne.Conduite);
        Assert.Contains("Cartridge cover", consigne.Quoi, StringComparison.Ordinal);
    }

    [Fact]
    public void Une_erreur_systeme_arrete_et_renvoie_au_SAV()
    {
        var consigne = ConduiteMachine.PourUnEvenement(De100ErrorLevel.SystemError, "");

        Assert.Equal(Conduite.Arreter, consigne.Conduite);
        Assert.Contains("SAV", consigne.Geste, StringComparison.OrdinalIgnoreCase);
    }

    // — file d'impression Windows —

    /// <summary>
    /// <b>Le cas du 04/08/2026</b> : trois travaux ont bloqué la DS620 deux heures durant,
    /// alors qu'elle se déclarait prête et sans erreur. C'est le seul cas où la machine
    /// ment, et il doit l'emporter sur tout ce qu'elle raconte.
    /// </summary>
    [Fact]
    public void Une_file_qui_n_avance_plus_l_emporte_sur_l_etat_de_la_machine()
    {
        var consigne = ConduiteMachine.PourLaFile(
            EtatFileDnp.Prete, pagesEnFile: 3,
            minutesSansProgres: ConduiteMachine.MinutesAvantDeViderLaFile);

        Assert.Equal(Conduite.ViderLaFile, consigne.Conduite);
        Assert.Contains("videz la file", consigne.Geste, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Une file qui avance normalement n'est jamais déclarée bloquée, si chargée soit-elle :
    /// une commande de six cents photos passe légitimement des heures à sortir.
    /// </summary>
    [Fact]
    public void Une_file_qui_avance_n_est_pas_declaree_bloquee()
    {
        var consigne = ConduiteMachine.PourLaFile(
            EtatFileDnp.Impression, pagesEnFile: 600, minutesSansProgres: 0);

        Assert.Equal(Conduite.Patienter, consigne.Conduite);
    }

    /// <summary>Une file VIDE n'est jamais bloquée, quel que soit le temps écoulé.</summary>
    [Fact]
    public void Une_file_vide_n_est_jamais_declaree_bloquee()
    {
        var consigne = ConduiteMachine.PourLaFile(
            EtatFileDnp.Prete, pagesEnFile: 0, minutesSansProgres: 120);

        Assert.Equal(Conduite.Continuer, consigne.Conduite);
    }

    [Theory]
    [InlineData(EtatFileDnp.Prete, Conduite.Continuer)]
    [InlineData(EtatFileDnp.Impression, Conduite.Patienter)]
    [InlineData(EtatFileDnp.EnPause, Conduite.MettreEnAttente)]
    [InlineData(EtatFileDnp.HorsLigne, Conduite.MettreEnAttente)]
    [InlineData(EtatFileDnp.Erreur, Conduite.MettreEnAttente)]
    [InlineData(EtatFileDnp.Inconnu, Conduite.MettreEnAttente)]
    public void Chaque_etat_de_file_a_sa_conduite(EtatFileDnp etat, Conduite attendue)
    {
        Assert.Equal(attendue, ConduiteMachine.PourLaFile(etat, pagesEnFile: 1).Conduite);
    }

    // — DNP par son SDK —

    /// <summary>
    /// Une DNP muette au SDK, c'est presque toujours DiLand qui tient le port USB — et non
    /// une machine en panne. Le geste à faire n'est pas le même du tout.
    /// </summary>
    [Fact]
    public void Une_DNP_injoignable_renvoie_a_DiLand()
    {
        var consigne = ConduiteMachine.PourLaDnp(new DnpStatus(0x80000000));

        Assert.Equal(Conduite.MettreEnAttente, consigne.Conduite);
        Assert.Contains("DiLand", consigne.Geste, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Un consommable épuisé fait attendre : la commande repart seule après le changement.</summary>
    [Fact]
    public void Un_consommable_epuise_met_en_attente_sans_perdre_la_commande()
    {
        var consigne = ConduiteMachine.PourLaDnp(new DnpStatus(DnpStatus.Codes.UsualRibbonEnd));

        Assert.Equal(Conduite.MettreEnAttente, consigne.Conduite);
        Assert.Contains("repartira", consigne.Geste, StringComparison.OrdinalIgnoreCase);
    }

    // — le message rendu à l'opérateur —

    /// <summary>L'état, puis le geste. Sans geste, pas de tiret orphelin.</summary>
    [Fact]
    public void Le_message_colle_l_etat_et_le_geste()
    {
        Assert.Equal("Prête", ConduiteMachine.PourLeMinilab(De100PrinterStatus.Ready).Message);

        Assert.Equal("Occupée — elle finit ce qu'elle a commencé",
            ConduiteMachine.PourLeMinilab(De100PrinterStatus.Printing).Message);
    }
}
