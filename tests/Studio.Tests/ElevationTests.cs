using Studio.App.Infrastructure;

namespace Studio.Tests;

/// <summary>
/// Le contrat entre deux lancements du même logiciel, quand le second remplace le premier
/// pour obtenir les droits d'administrateur.
///
/// <b>Ce qu'on protège.</b> Sans le numéro de processus, l'instance élevée ne sait pas qui
/// elle remplace : <c>UnSeulLogiciel</c> compte alors l'ancienne — qui est en train de se
/// retirer — et demande à l'opérateur de fermer son propre logiciel. On aurait remplacé les
/// boîtes d'erreur de l'impression par une question au démarrage, ce qui n'est pas ce qu'on
/// cherchait. Voir <see cref="Elevation.AttendreLInstanceRemplacee"/>.
///
/// Ça ne se voit nulle part quand ça casse, et ça ne casse qu'une seconde par jour : c'est
/// exactement ce qui mérite un essai.
/// </summary>
public class ElevationTests
{
    [Fact]
    public void Sans_argument_on_n_attend_personne()
    {
        Assert.Null(Elevation.PidARemplacer(null));
        Assert.Null(Elevation.PidARemplacer([]));
    }

    /// <summary>Le lancement ordinaire, depuis le raccourci du bureau.</summary>
    [Fact]
    public void Une_ligne_de_commande_ordinaire_n_attend_personne()
    {
        Assert.Null(Elevation.PidARemplacer(["C:\\photos\\IMG_1234.jpg"]));
    }

    [Fact]
    public void Le_drapeau_rend_le_numero_du_processus()
    {
        Assert.Equal(4242, Elevation.PidARemplacer(["--relance-de", "4242"]));
    }

    /// <summary>Le drapeau n'est pas forcément en tête : d'autres arguments peuvent le précéder.</summary>
    [Fact]
    public void Le_drapeau_se_trouve_ou_qu_il_soit()
    {
        Assert.Equal(7, Elevation.PidARemplacer(["autre", "--relance-de", "7", "encore"]));
    }

    /// <summary>
    /// Un drapeau en fin de ligne ne désigne rien : mieux vaut n'attendre personne que
    /// lire l'argument d'à côté.
    /// </summary>
    [Fact]
    public void Un_drapeau_sans_numero_n_attend_personne()
    {
        Assert.Null(Elevation.PidARemplacer(["--relance-de"]));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Un_numero_illisible_ou_absurde_n_attend_personne(string numero)
    {
        Assert.Null(Elevation.PidARemplacer(["--relance-de", numero]));
    }
}
