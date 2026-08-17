namespace Studio.App.Infrastructure;

/// <summary>
/// Qui entre dans le lot d'une planche d'identité, et qui n'est que MONTRÉ.
///
/// <b>La bande de gauche et le lot ne sont pas la même chose</b>, et c'est toute la règle.
/// Studio Photo Identité s'ouvre directement sur la carte mémoire du client : la bande
/// porte donc les quatre-vingts photos de la carte, parce que l'opérateur doit y chercher
/// la bonne. Aucune n'a été demandée pour autant.
///
/// <b>Le défaut d'Arcueil, 17/08/2026.</b> L'impression fabriquait une planche pour chaque
/// photo de la bande, en remontant à un exemplaire celles qui étaient à zéro
/// (<c>Math.Max(1, Quantite)</c>). Toucher « Imprimer » sortait donc la carte entière. Le
/// garde-fou existait pourtant en aval — <see cref="TirageIdentite"/> laisse de côté les
/// planches à zéro exemplaire, c'est écrit dans sa documentation — mais ce
/// <c>Math.Max</c> le désarmait : plus aucune planche n'arrivait à zéro.
///
/// La règle vit ici, nommée et éprouvée, plutôt qu'en trois expressions recopiées dans un
/// code-behind de deux mille lignes.
/// </summary>
public static class LotIdentite
{
    /// <summary>
    /// Ce que porte une photo qui arrive dans la bande.
    /// </summary>
    /// <param name="choisieDavance">
    /// Vraie quand la photo vient de l'écran de SÉLECTION : l'opérateur l'a désignée, elle
    /// est donc déjà demandée. Fausse quand la bande a été remplie en parcourant une carte
    /// ou un dossier — là, rien n'a encore été choisi.
    /// </param>
    public static int QuantiteDeDepart(bool choisieDavance) => choisieDavance ? 1 : 0;

    /// <summary>
    /// Ce qu'affiche le compteur « Planches » quand on ouvre une photo.
    ///
    /// <b>Ouvrir une photo, c'est la choisir</b> : l'opérateur a cliqué dessus pour la
    /// traiter, elle entre dans le lot à une planche. Mais seulement la PREMIÈRE fois — aux
    /// ouvertures suivantes on respecte ce qu'il a réglé, zéro compris, sans quoi le zéro
    /// qu'il vient de poser pour écarter une photo reviendrait à un dès qu'il en regarde une
    /// autre puis revient.
    /// </summary>
    /// <param name="quantiteEnregistree">Ce que la photo porte déjà.</param>
    /// <param name="dejaOuverte">Vraie si la photo a déjà été ouverte au moins une fois.</param>
    public static int QuantiteALOuverture(int quantiteEnregistree, bool dejaOuverte) =>
        quantiteEnregistree > 0 ? quantiteEnregistree
        : dejaOuverte ? 0
        : 1;

    /// <summary>
    /// Cette photo sort-elle sur du papier ? C'est la question que l'impression doit poser,
    /// et la seule.
    /// </summary>
    public static bool EstRetenue(int quantite) => quantite > 0;
}
