using Studio.App.Infrastructure;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Ce que l'opérateur lit pendant qu'une commande sort de la machine.
///
/// <b>Pourquoi ces essais existent.</b> Le suivi n'en avait aucun, et le compte a reculé
/// pendant des semaines sans que rien ne le signale : sur le minilab, la barre se
/// remplissait pendant l'ENVOI — quelques secondes, aucun papier — puis repartait de zéro
/// au vrai tirage. « Envoi au minilab 20 / 20 », puis « 0 / 20 photo(s) sorties ».
/// Signalé au comptoir le 11/08/2026 : « il peut y avoir un décalage sur le nombre de
/// photos sorti, ça saute ».
///
/// La règle tient en une phrase : <b>le compte des photos sorties ne recule jamais</b>.
/// </summary>
public class SuiviImpressionTests
{
    /// <summary>Une commande minilab : 20 feuilles, autant de verdicts attendus.</summary>
    private static TravailImpression Commande(int feuilles = 20, int verdicts = 20)
    {
        var travail = new TravailImpression("11-042");

        travail.Avancer(new PrintProgress(PrintProgress.Rendu, 0, 0, "A"));
        travail.Avancer(new PrintProgress(PrintProgress.Envoi, feuilles, feuilles, "A", verdicts));

        return travail;
    }

    /// <summary>
    /// <b>Le défaut signalé.</b> Pendant l'envoi, le compte porte sur des pages REMISES à
    /// la machine ; il ne dit rien du papier tombé, et l'écran ne doit donc pas le
    /// présenter comme tel.
    /// </summary>
    [Fact]
    public void Pendant_l_envoi_au_minilab_le_compte_ne_porte_pas_sur_le_papier()
    {
        var travail = Commande();

        Assert.False(travail.CompteDuPapierSorti);
    }

    /// <summary>Le tirage commencé, le compte redevient celui des photos sorties.</summary>
    [Fact]
    public void Le_tirage_commence_le_compte_porte_sur_le_papier()
    {
        var travail = Commande();

        travail.CommencerLeTirage(20, 20);

        Assert.True(travail.CompteDuPapierSorti);
        Assert.Equal(0, travail.Sortis);
    }

    /// <summary>
    /// Les circuits SANS accusé de sortie — spouleur Windows, envoi direct DNP — n'ont
    /// jamais d'étape « Tirage » : chez eux, ce qui est remis est le seul avancement
    /// affichable, et il ne recule pas. Les en priver laisserait une barre qui défile
    /// indéfiniment sans jamais se remplir.
    /// </summary>
    [Fact]
    public void Sans_minilab_le_compte_de_l_envoi_reste_le_bon()
    {
        var travail = new TravailImpression("11-043");

        // pas de verdicts annoncés : c'est ce qui distingue ces circuits du minilab
        travail.Avancer(new PrintProgress(PrintProgress.Impression, 3, 9, "D"));

        Assert.True(travail.CompteDuPapierSorti);
    }

    /// <summary>
    /// <b>Le contrat, bout à bout.</b> On rejoue une commande entière — envoi, puis le
    /// compteur de la machine qui monte, puis le verdict final — en vérifiant qu'à aucun
    /// moment le nombre de photos sorties ne diminue.
    /// </summary>
    [Fact]
    public void Le_compte_des_photos_sorties_ne_recule_jamais()
    {
        var travail = Commande();
        var vus = new List<int>();

        travail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TravailImpression.Sortis)) vus.Add(travail.Sortis);
        };

        travail.CommencerLeTirage(20, 20);

        // le compteur de la machine, relevé toutes les dix secondes : il avance par bonds
        // irréguliers, parfois de deux, parfois pas du tout
        foreach (var compteur in new[] { 0, 1, 3, 3, 6, 7, 7, 11, 15, 19 })
            travail.NoterFeuillesSorties(compteur);

        // puis le minilab rend son verdict sur toute la commande d'un coup
        for (var i = 0; i < 20; i++) travail.NoterTirage(reussi: true);

        Assert.True(travail.TirageTermine);
        Assert.Equal(20, travail.Sortis);

        // AUCUN recul, à aucun moment
        for (var i = 1; i < vus.Count; i++)
            Assert.True(vus[i] >= vus[i - 1],
                $"le compte est passé de {vus[i - 1]} à {vus[i]} : il a reculé");
    }

    /// <summary>
    /// Un relevé qui redescend — la machine redémarrée, ou une lecture qui porte sur autre
    /// chose — ne doit pas faire reculer l'affichage non plus.
    /// </summary>
    [Fact]
    public void Un_compteur_qui_redescend_ne_fait_pas_reculer_l_affichage()
    {
        var travail = Commande();
        travail.CommencerLeTirage(20, 20);

        travail.NoterFeuillesSorties(12);
        travail.NoterFeuillesSorties(4);

        Assert.Equal(12, travail.Sortis);
    }

    /// <summary>
    /// Le compteur est GLOBAL à la machine : il ne doit jamais faire dépasser le total de
    /// la commande, sans quoi une planche de six s'afficherait « 9 / 6 ».
    /// </summary>
    [Fact]
    public void Le_compte_ne_depasse_jamais_ce_que_la_commande_demande()
    {
        var travail = Commande(6, 6);
        travail.CommencerLeTirage(6, 6);

        travail.NoterFeuillesSorties(9);

        Assert.Equal(6, travail.Sortis);
        Assert.Equal(1, travail.Fraction);
    }

    /// <summary>
    /// Ce qui reste à sortir sert à la commande SUIVANTE sur la même machine : le minilab
    /// tire dans l'ordre où il reçoit, et sans ce report chaque commande comptait pour elle
    /// le papier de celle qui la précédait.
    /// </summary>
    [Fact]
    public void Ce_qui_reste_a_sortir_se_lit_pour_la_commande_suivante()
    {
        var travail = Commande();
        travail.CommencerLeTirage(20, 20);

        Assert.Equal(20, travail.RestantASortir);

        travail.NoterFeuillesSorties(8);

        Assert.Equal(12, travail.RestantASortir);
    }

    // ————— le compte tenu par la machine —————

    /// <summary>
    /// <b>Le handle de la commande minilab doit remonter jusqu'au suivi.</b>
    ///
    /// C'est par lui qu'on demande à la machine combien de tirages de CETTE commande sont
    /// sortis (<c>ST_ORDER_INFO.printedNum</c>), au lieu de lire son compteur général et
    /// d'y attribuer tout ce qui passe. Sans le handle, le suivi retombe sur les verdicts —
    /// donc sur un affichage qui saute à la fin.
    /// </summary>
    [Fact]
    public void Le_handle_de_la_commande_minilab_remonte_au_suivi()
    {
        var travail = new TravailImpression("11-044");

        travail.Avancer(new PrintProgress(PrintProgress.Rendu, 0, 0, "A"));
        Assert.Null(travail.HandleMinilab);

        // pendant l'envoi, la commande n'existe pas encore côté machine
        travail.Avancer(new PrintProgress(PrintProgress.Envoi, 20, 20, "A", 20));
        Assert.Null(travail.HandleMinilab);

        // …puis le minilab l'accepte et rend son handle
        travail.Avancer(new PrintProgress(PrintProgress.Envoi, 20, 20, "A", 20, "2608111128051361"));

        Assert.Equal("2608111128051361", travail.HandleMinilab);
    }

    /// <summary>
    /// Le compte de la machine s'affiche tel quel — pas de soustraction, pas de point de
    /// départ à relever : <c>printedNum</c> ne parle que de cette commande.
    /// </summary>
    [Fact]
    public void Le_compte_de_la_machine_s_affiche_tel_quel()
    {
        var travail = Commande();
        travail.CommencerLeTirage(20, 20);

        travail.NoterFeuillesSorties(7);

        Assert.Equal(7, travail.Sortis);
        Assert.Equal(0.35, travail.Fraction, 3);
    }
}
