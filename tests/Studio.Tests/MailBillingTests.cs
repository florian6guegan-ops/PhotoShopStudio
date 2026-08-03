using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// La facturation de l'envoi par courriel : 5,00 € PAR PHOTO.
///
/// Décision de l'exploitant du 03/08/2026. Deux points s'y jouent, et se trompent
/// silencieusement :
///
/// - <b>par photo, et non par fichier.</b> Le client reçoit trois versions de chaque
///   photo — l'entière, la légère, la pleine résolution — parce qu'elles ne servent pas à
///   la même chose. Les compter séparément triplerait l'addition.
/// - <b>le prix vit au catalogue</b>, pas dans le code : il se change au Catalogue comme
///   n'importe quel tarif, et une commande déjà passée garde le sien.
/// </summary>
public class MailBillingTests
{
    private static ProductCatalog Vide() => new([]);

    [Fact]
    public void Le_produit_est_cree_a_la_premiere_utilisation()
    {
        var ajoutes = new List<Product>();

        var produit = MailProduct.Obtenir(Vide(), ajoutes.Add);

        Assert.Equal(MailProduct.Code, produit.Code);
        Assert.Equal(5.00m, produit.Price);
        Assert.Single(ajoutes);
    }

    /// <summary>
    /// Le prix ajusté à la main ne doit pas être remis à celui d'usine par le premier
    /// envoi venu : c'est le catalogue qui décide, pas nous.
    /// </summary>
    [Fact]
    public void Un_produit_deja_present_nest_jamais_retarife()
    {
        var maison = MailProduct.Creer();
        maison.Price = 7.50m;

        var ajoutes = new List<Product>();
        var produit = MailProduct.Obtenir(new ProductCatalog([maison]), ajoutes.Add);

        Assert.Equal(7.50m, produit.Price);
        Assert.Empty(ajoutes);
    }

    [Theory]
    [InlineData(1, 5.00)]
    [InlineData(2, 10.00)]
    [InlineData(6, 30.00)]
    public void Le_total_suit_le_nombre_de_photos(int photos, decimal attendu)
    {
        var produit = MailProduct.Creer();

        Assert.Equal(attendu, produit.UnitPriceFor(photos) * photos);
    }

    /// <summary>
    /// Le produit ne sort par AUCUNE machine : c'est ce qui fait que son enveloppe se clôt
    /// sans rien imprimer, au lieu de partir en attente d'une imprimante qu'on n'attend pas.
    /// </summary>
    [Fact]
    public void Le_produit_ne_passe_par_aucune_imprimante()
    {
        Assert.Equal(ProductOutput.Email, MailProduct.Creer().Output);
    }
}
