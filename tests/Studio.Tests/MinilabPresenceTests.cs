using Studio.Printing.Devices.Fuji;

namespace Studio.Tests;

/// <summary>
/// Les minilabs reconnus d'après les files Windows, quand le relais ne répond pas.
///
/// <b>Le défaut du 07/08/2026.</b> Le bandeau gardait les Fuji en mémoire de session : une
/// panne passagère ne les effaçait pas. Mais une application qui DÉMARRE machines éteintes
/// n'a rien en mémoire, et le bandeau restait vide — comme si le poste n'avait pas de
/// minilab. Le poste de Créteil n'en montrait aucun, là où Maisons-Alfort les affichait
/// parce que son application tournait depuis qu'ils étaient allumés.
/// </summary>
public class MinilabPresenceTests
{
    /// <summary>Les deux files du DE100 de la boutique, telles que Windows les nomme.</summary>
    [Theory]
    [InlineData("FUJIFILM DE100")]
    [InlineData("FUJIFILM DE100-2")]
    [InlineData("fujifilm de100")]
    public void Les_files_du_DE100_sont_reconnues(string file)
    {
        Assert.True(MinilabPresence.EstUnMinilab(file));
    }

    /// <summary>
    /// Reconnu sur le MODÈLE : une file renommée par l'exploitant reste un minilab tant
    /// qu'elle garde « DE100 » dans son nom.
    /// </summary>
    [Theory]
    [InlineData("Minilab DE100 comptoir")]
    [InlineData("DE100")]
    public void Une_file_renommee_reste_reconnue(string file)
    {
        Assert.True(MinilabPresence.EstUnMinilab(file));
    }

    /// <summary>
    /// <b>Surtout ne rien attraper d'autre.</b> Une DNP, une Epson ou un télécopieur pris
    /// pour un minilab s'afficherait comme une machine à tirages — et le bandeau
    /// annoncerait un minilab là où il n'y en a pas.
    /// </summary>
    [Theory]
    [InlineData("DP-DS620")]
    [InlineData("EPSON67A266 (SC-P900 Series)")]
    [InlineData("Microsoft Print to PDF")]
    [InlineData("SAWGRASS SG500")]
    [InlineData("Fax")]
    [InlineData("iR-ADV C257")]
    public void Les_autres_imprimantes_ne_sont_pas_des_minilabs(string file)
    {
        Assert.False(MinilabPresence.EstUnMinilab(file));
    }

    /// <summary>
    /// La lecture ne lève jamais et rend une liste, même sans imprimante : elle est appelée
    /// depuis le rafraîchissement du bandeau, où une exception viderait l'écran.
    /// </summary>
    [Fact]
    public void La_lecture_ne_leve_jamais()
    {
        var vus = MinilabPresence.VusParWindows(TimeSpan.FromSeconds(3));

        Assert.NotNull(vus);

        // sur un poste qui en a, ils sortent hors ligne et numérotés depuis A
        Assert.All(vus, m => Assert.Equal(De100PrinterStatus.Offline, m.Status));
        Assert.All(vus, m => Assert.InRange(m.MachineId, 'A', 'Z'));
    }

    /// <summary>
    /// Un budget épuisé rend une liste vide plutôt que d'attendre : une file qui ne répond
    /// pas ne doit pas retenir le bandeau.
    /// </summary>
    [Fact]
    public void Un_budget_nul_rend_une_liste_vide_sans_lever()
    {
        Assert.Empty(MinilabPresence.VusParWindows(TimeSpan.Zero));
    }
}
