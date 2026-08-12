using Studio.Printing.Devices.Fuji.Bridge;

namespace Studio.Tests;

/// <summary>
/// La borne qui empêche le relais 32 bits de mourir en pleine commande.
///
/// <b>Ce qu'elle corrige.</b> Chaque délai dépassé laisse un fil bloqué dans un appel natif
/// que le SDK ne rendra jamais. Rien ne bornait leur nombre — or le relais est en 32 bits :
/// deux gigaoctets d'espace, un mégaoctet de pile par fil. À Créteil, le 12/08/2026, le
/// relais s'est arrêté DEUX FOIS en pleine commande (16:11 et 17:36), laissant la commande
/// 12-024 « non confirmée » sans qu'aucun verdict n'arrive.
/// </summary>
public class FilsOrphelinsTests
{
    /// <summary>Un relais qui va bien ne refuse rien.</summary>
    [Fact]
    public void Au_repos_rien_n_est_sature()
    {
        var compte = new FilsOrphelins();

        Assert.Equal(0, compte.Perdus);
        Assert.False(compte.Sature);
    }

    /// <summary>Quelques appels lents ne ferment pas la porte : c'est le cas courant.</summary>
    [Fact]
    public void Quelques_appels_perdus_ne_saturent_pas()
    {
        var compte = new FilsOrphelins(plafond: 8);

        for (var i = 0; i < 7; i++) compte.Abandonne();

        Assert.Equal(7, compte.Perdus);
        Assert.False(compte.Sature);
    }

    /// <summary>Au plafond, on cesse d'envoyer du travail à un SDK manifestement coincé.</summary>
    [Fact]
    public void Au_plafond_le_relais_cesse_d_envoyer()
    {
        var compte = new FilsOrphelins(plafond: 8);

        for (var i = 0; i < 8; i++) compte.Abandonne();

        Assert.True(compte.Sature);
    }

    /// <summary>
    /// <b>Et la porte se ROUVRE.</b> Un poste simplement lent finit par voir ses appels
    /// revenir ; sans ce décompte, il resterait fermé jusqu'au redémarrage et l'on aurait
    /// remplacé un plantage par une panne.
    /// </summary>
    [Fact]
    public void Un_appel_qui_revient_rouvre_la_porte()
    {
        var compte = new FilsOrphelins(plafond: 2);

        compte.Abandonne();
        compte.Abandonne();
        Assert.True(compte.Sature);

        compte.Revenu();

        Assert.False(compte.Sature);
        Assert.Equal(1, compte.Perdus);
    }

    /// <summary>
    /// Un décompte de trop ne doit pas rendre le compte négatif : il rouvrirait la porte à
    /// tort, et le garde-fou ne servirait plus à rien.
    /// </summary>
    [Fact]
    public void Le_compte_ne_descend_jamais_sous_zero()
    {
        var compte = new FilsOrphelins();

        compte.Revenu();
        compte.Revenu();

        Assert.Equal(0, compte.Perdus);

        compte.Abandonne();
        Assert.Equal(1, compte.Perdus);
    }

    /// <summary>
    /// Le relais sert ses commandes de front : le compte doit tenir sous plusieurs fils.
    /// </summary>
    [Fact]
    public void Le_compte_tient_sous_plusieurs_fils()
    {
        var compte = new FilsOrphelins(plafond: 1000);

        Parallel.For(0, 200, _ => compte.Abandonne());
        Assert.Equal(200, compte.Perdus);

        Parallel.For(0, 150, _ => compte.Revenu());
        Assert.Equal(50, compte.Perdus);
    }
}
