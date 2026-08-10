using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// L'accès au minilab à travers l'orchestrateur d'impression.
///
/// <b>Ce que cet essai empêche de revenir.</b> Le 10/08/2026, la commande 10-024 de Créteil
/// a échoué sur « le relais 32 bits n'a pas été fourni » alors que le relais TOURNAIT.
/// <c>ReloadCatalog</c> reconstruisait l'orchestrateur sans lui repasser le minilab :
/// toucher au catalogue rendait le DE100 inaccessible jusqu'au redémarrage de
/// l'application. Le report de la marque, lui, avait été pensé — pas celui du minilab.
/// </summary>
public class OrchestrateurMinilabTests
{
    private static (ProductCatalog, OrderFolderStore, string) Bac()
    {
        var racine = Path.Combine(Path.GetTempPath(), "OrchTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(racine);
        var catalogue = new ProductCatalog([]);
        var store = new OrderFolderStore(Path.Combine(racine, "orders"));
        return (catalogue, store, Path.Combine(racine, "catalog"));
    }

    /// <summary>
    /// Un orchestrateur construit SANS minilab doit le dire — c'est le message qu'a vu
    /// l'exploitant, et il reste juste : sans minilab, rien ne peut partir.
    /// </summary>
    [Fact]
    public void Sans_minilab_l_orchestrateur_le_signale()
    {
        var (catalogue, store, dir) = Bac();
        var sans = new PrintOrchestrator(catalogue, store, dir);

        Assert.False(sans.MinilabDisponible);
    }

    /// <summary>
    /// <b>Le cas de la commande 10-024.</b> Quand un minilab est fourni, l'orchestrateur
    /// doit le garder — c'est exactement ce que <c>ReloadCatalog</c> avait cessé de faire.
    /// </summary>
    [Fact]
    public void Le_minilab_fourni_reste_accessible()
    {
        var (catalogue, store, dir) = Bac();
        var minilab = new Studio.Printing.Devices.Fuji.De100BridgePrinter();

        var avec = new PrintOrchestrator(catalogue, store, dir, minilab);

        Assert.True(avec.MinilabDisponible);
    }
}
