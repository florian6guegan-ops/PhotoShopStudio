using Studio.Core.Domain;

namespace Studio.Core.Catalog;

/// <summary>
/// Le papier de la planche de RENTRÉE, en tant que produit du catalogue.
///
/// <b>Pourquoi il se fabrique tout seul.</b> Les quatre boutiques ont chacune leur
/// <c>products.json</c>, dans leur dossier de données, et une mise à jour n'y touche
/// JAMAIS — c'est la règle de <see cref="CatalogueLivre"/>, et elle est juste : un poste
/// qui tourne a des prix et des réglages pilote qui lui appartiennent. Livrer le nouveau
/// format dans le catalogue du dépôt ne l'aurait donc donné à personne, et il aurait fallu
/// le recréer à la main sur les quatre postes, DEVMODE compris — c'est-à-dire refaire
/// quatre fois une capture de dialogue pilote, un jour de rentrée.
///
/// Il est donc DÉRIVÉ de la planche d'identité que le poste utilise déjà : même machine,
/// même papier, même profil ICC, même DEVMODE — tout ce qui est difficile à retrouver est
/// repris tel quel. Ne changent que ce qui fait le format : le nombre de cases, le portrait,
/// le nom et le prix.
///
/// <b>Ne retarife JAMAIS un produit existant</b>, comme <see cref="MailProduct"/> : une fois
/// créé, il appartient à l'exploitant, qui peut en changer le prix au Catalogue.
/// </summary>
public static class PlancheRentreeProduit
{
    /// <summary>Code du produit. Déterministe : il ne doit jamais y en avoir deux.</summary>
    public const string Code = "ID-RENTREE";

    /// <summary>Ce que l'opérateur lit dans la liste des papiers.</summary>
    public const string Nom = "Rentrée — 4 photos d'identité + 1 grande";

    /// <summary>
    /// Prix de la planche à sa création : onze euros, fixés par l'exploitant le 20/08/2026.
    ///
    /// Le prix réellement facturé vient de <see cref="TarifsIdentite.RentreeEur"/>, comme
    /// pour toutes les planches d'identité — le produit ne porte le sien que pour la fiche
    /// du Catalogue et pour ne pas afficher zéro euro.
    /// </summary>
    public const decimal PrixParDefaut = 11m;

    /// <summary>
    /// Le produit de rentrée du catalogue, en le dérivant de <paramref name="planche"/>
    /// s'il n'y est pas encore.
    /// </summary>
    /// <param name="planche">
    /// La planche d'identité dont on reprend le papier. Doit porter un <c>Sheet</c> — c'est
    /// ce qui fait d'elle une planche.
    /// </param>
    /// <param name="ajouter">
    /// Reçoit le produit à enregistrer, à la toute première utilisation seulement.
    /// </param>
    public static Product Obtenir(ProductCatalog catalogue, Product planche, Action<Product> ajouter)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(planche);
        ArgumentNullException.ThrowIfNull(ajouter);

        if (catalogue.Find(Code) is { } existant) return existant;

        var produit = Deriver(planche);
        ajouter(produit);
        return produit;
    }

    /// <summary>
    /// Fabrique le produit de rentrée à partir d'une planche d'identité.
    ///
    /// Séparé d'<see cref="Obtenir"/> pour être vérifiable sans catalogue : c'est ici que
    /// se joue le « tout ce qui est difficile à retrouver est repris tel quel ».
    /// </summary>
    public static Product Deriver(Product planche)
    {
        ArgumentNullException.ThrowIfNull(planche);

        var produit = planche.Copy();

        produit.Code = Code;
        produit.Name = Nom;
        produit.Price = PrixParDefaut;

        // Les paliers dégressifs de la planche n'ont pas de sens ici : on ne vend pas
        // trente planches de rentrée au même client, et les recopier ferait apparaître un
        // tarif de gros sur un produit de saison.
        produit.PriceTiers = [];
        produit.Enabled = true;

        // Une planche SANS Sheet n'existe pas — mais le catalogue est un fichier que
        // quelqu'un peut avoir édité à la main, et il vaut mieux un défaut à la norme
        // française qu'une exception au comptoir.
        produit.Sheet ??= new SheetSpec();
        produit.Sheet.Copies = PlancheDeRentree.IdentitesParDefaut;
        produit.Sheet.GrandePhoto = true;

        return produit;
    }
}
