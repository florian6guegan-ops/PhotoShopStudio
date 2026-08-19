using Studio.Core.Domain;

namespace Studio.App.Infrastructure;

/// <summary>
/// À quel onglet des « Commandes récentes » une enveloppe appartient.
///
/// La règle vit ici, et non dans la vue, parce qu'elle s'est trompée trois fois : la
/// séparation des tirages et de l'identité ne tient qu'à ce qu'on range TOUT le parcours
/// identité du bon côté, et ce parcours ne se réduit pas aux planches.
/// </summary>
public static class GenreDeCommande
{
    /// <summary>
    /// Cette ligne relève-t-elle des photos d'identité ?
    ///
    /// <b>Trois choses en relèvent, et deux manquaient</b> (signalé le 19/08/2026 : « l'onglet
    /// tirages photos ne devrait afficher que les tirages, et non tout ce qui concerne photos
    /// d'identité ») :
    ///
    /// 1. <b>La planche</b> — un produit à <c>Sheet</c>. C'est le cas qu'on voyait, et le seul
    ///    qui était traité.
    /// 2. <b>L'E-PHOTO</b>, qui n'est PAS une planche : la photo part entière sur un 10×15,
    ///    sans gabarit, donc rien dans le produit ne la distingue d'un tirage ordinaire. Ce
    ///    qui la distingue, c'est qu'on y arrive par l'écran d'identité — d'où
    ///    <paramref name="produitsDIdentite"/>, qui porte les codes des raccourcis de cet
    ///    écran. Elle représentait quatre des vingt-cinq dernières commandes de la boutique,
    ///    toutes rangées dans « Tirages photo ».
    /// 3. <b>L'ENVOI PAR COURRIEL</b> (<see cref="ProductOutput.Email"/>) : celui-là n'est
    ///    pas seulement mal rangé, ce n'est pas un tirage du tout — rien ne sort de la
    ///    machine. Il n'a sa place dans « Tirages photo » sous aucun angle.
    ///
    /// Le repli sur la taille de case reste : il sert aux commandes enregistrées avant que
    /// ce champ existe, et à celles dont le produit a été supprimé du catalogue depuis.
    /// </summary>
    /// <param name="ligne">La ligne examinée.</param>
    /// <param name="catalogue">Le produit portant ce code, ou null s'il n'existe plus.</param>
    /// <param name="produitsDIdentite">
    /// Codes des produits que l'écran d'identité propose tels quels — les raccourcis de type
    /// produit. Vide = on s'en tient aux deux autres règles.
    /// </param>
    public static bool EstIdentite(
        OrderLine ligne,
        Func<string, Product?> catalogue,
        IReadOnlyCollection<string> produitsDIdentite)
    {
        ArgumentNullException.ThrowIfNull(ligne);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(produitsDIdentite);

        var produit = catalogue(ligne.ProductCode);

        if (produit?.Sheet is not null) return true;
        if (produit?.Output == ProductOutput.Email) return true;

        if (produitsDIdentite.Any(c =>
                c.Equals(ligne.ProductCode, StringComparison.OrdinalIgnoreCase)))
            return true;

        return ligne.Items.Any(i => i.SheetCellWidthMm is > 0);
    }

    /// <summary>
    /// Une enveloppe de TIRAGES, c'est-à-dire sans une seule ligne d'identité.
    ///
    /// <b>La règle s'est durcie le 06/08/2026.</b> Il suffisait d'une ligne de tirage pour
    /// qu'une enveloppe MIXTE — des 10×15 et une planche d'identité dans la même commande —
    /// paraisse dans « Tirages photo ». On y retrouvait donc les planches qu'on avait
    /// justement rangées dans leur propre onglet, et l'intérêt de séparer les deux tombait.
    ///
    /// Rien ne se perd : une enveloppe mixte reste dans « Photos d'identité » et dans
    /// « Tout ».
    /// </summary>
    public static bool EstDesTirages(
        Envelope enveloppe,
        Func<string, Product?> catalogue,
        IReadOnlyCollection<string> produitsDIdentite)
    {
        ArgumentNullException.ThrowIfNull(enveloppe);

        var identite = enveloppe.Lines
            .Select(l => EstIdentite(l, catalogue, produitsDIdentite))
            .ToList();

        return identite.Any(e => !e) && !identite.Any(e => e);
    }
}
