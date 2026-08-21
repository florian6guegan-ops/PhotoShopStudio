using Studio.App.Infrastructure;

namespace Studio.Tests;

/// <summary>
/// Le produit FANTÔME d'une taille libre : comment on le nomme, et comment on le reconnaît.
///
/// <b>Ce qu'on protège.</b> Un fantôme qui arrive jusqu'à la commande y écrit un code que le
/// catalogue ne connaît pas. La commande est alors créée — à prix zéro, sans imprimante — et
/// le défaut n'éclate qu'à l'impression, en tâche de fond, loin de l'opérateur qui vient de
/// rendre la monnaie. C'est la commande 21-014 du 21/08/2026 : deux photos en 35 × 45,
/// « Produit inconnu dans le catalogue : perso-35x45 », rien de sorti et rien à l'écran.
///
/// Les deux moitiés de la convention — bâtir le code, le reconnaître — sont éprouvées
/// ENSEMBLE : c'est leur accord qui compte, et c'est lui qui casserait en silence.
/// </summary>
public class TailleLibreTests
{
    [Theory]
    [InlineData(35, 45, "perso-35x45")]
    [InlineData(70, 100, "perso-70x100")]
    [InlineData(5.5, 8, "perso-5,5x8")]
    public void Le_code_porte_les_cotes(double largeur, double hauteur, string attendu)
    {
        Assert.Equal(attendu, TailleLibre.Code(largeur, hauteur));
    }

    /// <summary>
    /// Les cotes sont dans le code, et ce n'est pas décoratif : poser un produit ne change
    /// rien quand le code ne change pas. Deux tailles doivent donc donner deux codes.
    /// </summary>
    [Fact]
    public void Deux_tailles_ne_partagent_pas_un_code()
    {
        Assert.NotEqual(TailleLibre.Code(7, 10), TailleLibre.Code(5.5, 8));
    }

    /// <summary>Ce qu'on bâtit, on doit savoir le reconnaître — sinon le garde-fou ne garde rien.</summary>
    [Theory]
    [InlineData(35, 45)]
    [InlineData(70, 100)]
    [InlineData(5.5, 8)]
    public void Un_code_fabrique_ici_est_reconnu_ici(double largeur, double hauteur)
    {
        Assert.True(TailleLibre.EstUnFantome(TailleLibre.Code(largeur, hauteur)));
    }

    /// <summary>
    /// Les codes du catalogue décrivent un PAPIER, jamais une taille demandée au comptoir :
    /// aucun ne doit passer pour un fantôme, sous peine de refuser une vraie commande.
    /// </summary>
    [Theory]
    [InlineData("10x15")]
    [InlineData("bord-blanc-20x25")]
    [InlineData("e-photo-dnp")]
    [InlineData("ID-FR-6")]
    [InlineData("ID-RENTREE")]
    [InlineData("personnalisation")]
    public void Un_produit_du_catalogue_n_est_pas_un_fantome(string code)
    {
        Assert.False(TailleLibre.EstUnFantome(code));
    }

    [Fact]
    public void Sans_code_il_n_y_a_pas_de_fantome()
    {
        Assert.False(TailleLibre.EstUnFantome(null));
        Assert.False(TailleLibre.EstUnFantome(""));
    }
}
