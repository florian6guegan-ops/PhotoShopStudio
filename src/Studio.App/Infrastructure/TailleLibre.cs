namespace Studio.App.Infrastructure;

/// <summary>
/// Le PRODUIT FANTÔME d'une taille libre : comment on le nomme, et comment on le reconnaît.
///
/// <b>Ce que c'est.</b> Une taille libre — « 7 × 10 », « 35 × 45 » — n'est pas au catalogue :
/// l'écran fabrique un produit de circonstance pour que le cadrage, l'aperçu et le
/// récapitulatif aient une forme à montrer. Ce produit ne vit que le temps de l'écran, et il
/// cède la place au vrai PAPIER au moment de la commande.
///
/// <b>Pourquoi c'est ici.</b> Un fantôme qui arrive jusqu'à la commande y écrit un code que
/// le catalogue ne connaît pas. Rien ne s'en aperçoit : la commande est créée, à prix zéro
/// et sans imprimante, et le défaut n'éclate qu'à l'impression — en tâche de fond, dans
/// <c>ProductCatalog.Require</c>, loin de l'opérateur qui vient de rendre la monnaie. C'est
/// arrivé le 21/08/2026 sur la commande 21-014.
///
/// ⚠ <b>Sorti de la vue pour être ESSAYABLE</b>, comme <see cref="RepriseDeLaPlanche"/> et
/// <see cref="CopieDeTravail"/>, et pour la même raison : la règle est trop silencieuse
/// quand elle casse pour vivre dans un code-behind de trois mille lignes que rien ne couvre.
/// </summary>
public static class TailleLibre
{
    /// <summary>
    /// Ce qui préfixe le code d'un produit fantôme.
    ///
    /// ⚠ Aucun produit du catalogue ne doit commencer par là. C'est une convention, et elle
    /// tient parce que les codes du catalogue décrivent un papier (<c>10x15</c>,
    /// <c>bord-blanc-20x25</c>, <c>e-photo-dnp</c>) et jamais une taille demandée au
    /// comptoir.
    /// </summary>
    public const string Prefixe = "perso-";

    /// <summary>
    /// Le code du produit fantôme d'une taille, en millimètres.
    ///
    /// ⚠ <b>Les COTES sont dans le code</b>, et ce n'est pas décoratif : le fantôme
    /// s'appelait « perso » quelle que soit la taille, et poser un produit ne change rien
    /// quand le code ne change pas — passer une photo de 7 × 10 à 5,5 × 8 laissait donc le
    /// cadre au format d'avant, en silence.
    /// </summary>
    public static string Code(double largeurMm, double hauteurMm) =>
        $"{Prefixe}{largeurMm:0.#}x{hauteurMm:0.#}";

    /// <summary>
    /// Ce code désigne-t-il un produit fantôme — donc un produit qu'aucun catalogue ne
    /// connaît ?
    ///
    /// Sert de garde-fou avant d'écrire une commande : un fantôme qui y arrive sans sa
    /// taille est un tirage qui ne sortira pas.
    /// </summary>
    public static bool EstUnFantome(string? code) =>
        code is not null && code.StartsWith(Prefixe, StringComparison.OrdinalIgnoreCase);
}
