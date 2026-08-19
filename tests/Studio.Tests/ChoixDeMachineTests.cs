using Studio.App.Infrastructure;
using Studio.Printing.Devices.Fuji;

namespace Studio.Tests;

/// <summary>
/// L'OPÉRATEUR NE DOIT JAMAIS PERDRE LE CHOIX DE SA MACHINE.
///
/// Le 18/08/2026 à 15:24, « list-machines » a dépassé ses dix secondes — le relais l'écrit
/// lui-même : « la machine est probablement en veille ». L'exception est remontée jusqu'à
/// l'écran de choix des photos, qui a affiché « minilab injoignable » et GRISÉ la liste. Les
/// deux DE100 étaient allumées à côté de l'opérateur, et il n'avait plus aucun moyen de
/// désigner un rouleau.
///
/// Même règle qu'en 1.4.1 pour les tirages : un délai dépassé ne dit pas « rien n'est là », il
/// dit « je ne sais pas ». Ici on ne risque même pas une feuille — proposer une machine
/// n'engage rien, le tirage revérifie tout.
/// </summary>
public class ChoixDeMachineTests
{
    private static De100PrinterInfo Machine(char id, De100PrinterStatus etat) =>
        new(id, etat, "", "DE100", "", "", 0, null, null, []);

    /// <summary>Le cas normal : le relais répond, on prend ce qu'il dit.</summary>
    [Fact]
    public void Ce_que_le_relais_vient_de_dire_l_emporte()
    {
        var vues = new[] { Machine('A', De100PrinterStatus.Ready) };
        var vieilles = new[] { Machine('B', De100PrinterStatus.Ready) };

        var (machines, deMemoire) = ChoixDeMachine.Proposer(vues, vieilles);

        Assert.Equal('A', Assert.Single(machines).MachineId);
        Assert.False(deMemoire);
    }

    /// <summary>
    /// <b>Le défaut du 18/08.</b> Le relais n'a rien rendu ; les machines qu'il décrivait
    /// cinq minutes plus tôt restent proposables, et l'écran doit le dire.
    /// </summary>
    [Fact]
    public void Sans_reponse_on_repropose_les_machines_connues()
    {
        var connues = new[]
        {
            Machine('A', De100PrinterStatus.Ready),
            Machine('B', De100PrinterStatus.Printing),
        };

        var (machines, deMemoire) = ChoixDeMachine.Proposer([], connues);

        Assert.Equal(2, machines.Count);
        Assert.True(deMemoire);
    }

    /// <summary>
    /// Une machine HORS LIGNE ne revient pas par la mémoire : l'imposer ferait refuser la
    /// commande en nommant une machine éteinte.
    /// </summary>
    [Fact]
    public void Une_machine_hors_ligne_ne_se_repropose_pas()
    {
        var connues = new[]
        {
            Machine('A', De100PrinterStatus.Offline),
            Machine('B', De100PrinterStatus.Ready),
        };

        var (machines, deMemoire) = ChoixDeMachine.Proposer([], connues);

        Assert.Equal('B', Assert.Single(machines).MachineId);
        Assert.True(deMemoire);
    }

    /// <summary>
    /// Rien maintenant, rien avant : là, il n'y a vraiment rien à choisir, et l'écran a
    /// raison de le dire. C'est le cas d'un poste sans minilab — kodakidpc, par exemple.
    /// </summary>
    [Fact]
    public void Sans_rien_de_connu_on_ne_propose_rien()
    {
        var (machines, deMemoire) = ChoixDeMachine.Proposer([], null);

        Assert.Empty(machines);
        Assert.False(deMemoire);
    }

    /// <summary>
    /// Un dernier état connu qui ne portait QUE des machines hors ligne ne vaut pas mieux
    /// que rien : on ne prétend pas se souvenir d'une machine utilisable.
    /// </summary>
    [Fact]
    public void Un_souvenir_entierement_hors_ligne_ne_compte_pas()
    {
        var connues = new[] { Machine('A', De100PrinterStatus.Offline) };

        var (machines, deMemoire) = ChoixDeMachine.Proposer([], connues);

        Assert.Empty(machines);
        Assert.False(deMemoire);
    }
}
