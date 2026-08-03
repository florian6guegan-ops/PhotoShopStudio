using Studio.Core.Domain;

namespace Studio.Core.Catalog;

/// <summary>
/// L'envoi des photos par courriel, en tant que produit du catalogue.
///
/// <b>Pourquoi un vrai produit et non un supplément codé en dur.</b> C'est une ligne de
/// caisse : elle doit figurer sur le ticket, dans le total de la commande, dans les
/// statistiques du mois, et se retrouver des semaines plus tard quand on cherche ce qu'un
/// client a payé. Un montant ajouté à côté du catalogue serait invisible partout où l'on
/// regarde le chiffre d'affaires.
///
/// C'est aussi ce qui rend le prix MODIFIABLE : 5,00 € est la décision de l'exploitant du
/// 03/08/2026, pas une constante du logiciel. Le jour où il change, il se change au
/// Catalogue comme n'importe quel tarif — et les commandes déjà passées gardent le leur,
/// puisque le prix est recopié dans la ligne à la commande.
///
/// Le produit ne sort par AUCUNE machine (<see cref="ProductOutput.Email"/>) : son
/// enveloppe est close sans rien imprimer.
/// </summary>
public static class MailProduct
{
    /// <summary>Code du produit. Déterministe : il ne doit jamais y en avoir deux.</summary>
    public const string Code = "envoi-courriel";

    /// <summary>Prix par photo envoyée, à la création du produit.</summary>
    public const decimal PrixParDefaut = 5.00m;

    /// <summary>
    /// Le produit tel qu'on le crée la première fois.
    ///
    /// Les cotes sont celles d'un 10×15 et ne servent à rien d'autre qu'à ne pas laisser
    /// de zéros dans la fiche : rien n'est imprimé. Le vrai réglage est le prix.
    /// </summary>
    public static Product Creer() => new()
    {
        Code = Code,
        Name = "Envoi des photos par courriel",
        WidthMm = 102,
        HeightMm = 152,
        Price = PrixParDefaut,
        Output = ProductOutput.Email,
        PrinterName = "",
        Enabled = true,
    };

    /// <summary>
    /// Le produit d'envoi du catalogue, en le créant s'il n'y est pas encore.
    ///
    /// <paramref name="ajouter"/> reçoit le produit à enregistrer ; il n'est appelé que
    /// lors de la toute première utilisation. Un produit déjà présent n'est JAMAIS
    /// retarifé : son prix a pu être ajusté à la main, et un envoi ne doit pas remettre
    /// le tarif d'usine.
    /// </summary>
    public static Product Obtenir(ProductCatalog catalogue, Action<Product> ajouter)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(ajouter);

        if (catalogue.Find(Code) is { } existant) return existant;

        var produit = Creer();
        ajouter(produit);
        return produit;
    }
}
