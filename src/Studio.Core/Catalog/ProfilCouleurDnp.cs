using Studio.Core.Domain;

namespace Studio.Core.Catalog;

/// <summary>
/// Le profil ICC appliqué à TOUT ce qui sort de la DNP, lu et posé d'un seul geste.
///
/// <b>Pourquoi ce réglage existe.</b> Le profil couleur est une propriété de PRODUIT, et il
/// se choisit donc produit par produit dans le Catalogue — écran que Studio Photo Identité
/// n'a pas, et que personne n'ouvre pour trois lignes qui doivent de toute façon dire la
/// même chose. Or la couleur ne dépend pas du produit : elle dépend de la MACHINE et de son
/// papier. Trois produits sortent de la DS620 dans ces boutiques — la planche d'identité,
/// l'E-Photo et le 10×15 — et seule la planche portait un profil : les deux autres partaient
/// en sRGB présumé, sur la même machine et le même rouleau. Signalé le 18/08/2026, en
/// comparant une planche sortie de Studio et la même sortie de DiLand.
///
/// <b>Les finitions sont remises à zéro.</b> Le profil d'une finition l'emporte sur celui du
/// produit (voir <c>PrintOrchestrator</c>) : la laisser en place donnerait un réglage qui
/// paraît pris et ne sort pas. Sur une DNP la finition n'est que le surlaminage — brillant
/// ou mat —, elle ne change pas la colorimétrie : un seul profil pour la machine est le bon
/// modèle.
/// </summary>
public static class ProfilCouleurDnp
{
    /// <summary>Les produits du catalogue qui sortent sur une DNP, par leur file Windows.</summary>
    public static IReadOnlyList<Product> Produits(IEnumerable<Product> tous) =>
        tous.Where(p => p.Output == ProductOutput.Printer && ImprimanteDnp.EstUneDnp(p.PrinterName))
            .ToList();

    /// <summary>
    /// Ce que la DNP applique aujourd'hui.
    /// </summary>
    /// <param name="Profil">
    /// Le fichier ICC partagé, ou null quand il n'y en a pas — ou que les produits ne
    /// s'accordent pas.
    /// </param>
    /// <param name="Accord">
    /// Vrai quand tous les produits DNP portent la même chose. Faux, l'écran doit le DIRE :
    /// c'est exactement le cas où la machine sort deux couleurs et où personne ne comprend
    /// pourquoi.
    /// </param>
    public readonly record struct Reglage(string? Profil, bool Accord);

    /// <summary>
    /// Le profil réellement appliqué à chaque produit — celui de sa finition s'il en a une,
    /// le sien sinon — puis le verdict : tout le monde d'accord, ou non.
    /// </summary>
    public static Reglage Lire(IEnumerable<Product> produitsDnp)
    {
        var poses = produitsDnp.SelectMany(Effectifs).ToList();
        if (poses.Count == 0) return new Reglage(null, Accord: true);

        var premier = poses[0];
        var accord = poses.All(p => string.Equals(p, premier, StringComparison.OrdinalIgnoreCase));

        return new Reglage(accord ? premier : null, accord);
    }

    /// <summary>
    /// Pose <paramref name="profil"/> (null = aucun, le pilote fait la couleur) sur tous les
    /// produits, et efface les profils de finition qui l'auraient masqué.
    /// </summary>
    /// <returns>Les produits réellement modifiés — ce que l'écran annonce à l'opérateur.</returns>
    public static IReadOnlyList<Product> Appliquer(IEnumerable<Product> produitsDnp, string? profil)
    {
        var voulu = string.IsNullOrWhiteSpace(profil) ? null : profil;
        var changes = new List<Product>();

        foreach (var produit in produitsDnp)
        {
            var avant = Effectifs(produit).ToList();

            produit.IccProfile = voulu;
            foreach (var finition in produit.Finishes ?? [])
                finition.IccProfile = null;

            if (avant.Count != 1 || !string.Equals(avant[0], voulu, StringComparison.OrdinalIgnoreCase))
                changes.Add(produit);
        }

        return changes;
    }

    /// <summary>
    /// Les profils que ce produit applique VRAIMENT. Une finition qui en porte un couvre
    /// celui du produit ; un produit à plusieurs finitions peut donc en appliquer plusieurs.
    /// </summary>
    private static IEnumerable<string?> Effectifs(Product produit)
    {
        var finitions = (produit.Finishes ?? []).ToList();
        if (finitions.Count == 0) return [Vide(produit.IccProfile)];

        return finitions.Select(f => Vide(f.IccProfile) ?? Vide(produit.IccProfile));
    }

    private static string? Vide(string? nom) => string.IsNullOrWhiteSpace(nom) ? null : nom;
}
