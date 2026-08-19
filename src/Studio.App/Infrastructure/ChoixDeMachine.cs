using Studio.Printing.Devices.Fuji;

namespace Studio.App.Infrastructure;

/// <summary>
/// Quelles machines du minilab proposer à l'opérateur, quand le relais vient de répondre —
/// ou de ne pas répondre.
///
/// <b>Ne rien avoir reçu n'est pas une raison de fermer la liste.</b> Le 18/08/2026 à 15:24,
/// « list-machines » a dépassé ses dix secondes (le relais l'écrit lui-même : « probablement
/// en veille »), l'exception est remontée jusqu'à l'écran de choix des photos, et l'opérateur
/// s'est retrouvé devant une liste GRISÉE annonçant « minilab injoignable » — les deux DE100
/// allumées à côté de lui. Plus aucun moyen de désigner un rouleau.
///
/// C'est la même règle que pour les tirages, écrite ailleurs depuis la 1.4.1 : <b>un délai
/// dépassé ne dit pas « rien n'est là », il dit « je ne sais pas »</b>. La différence, c'est
/// qu'ici on ne risque pas une feuille : proposer une machine n'engage rien, le tirage
/// revérifie la machine et son rouleau avant d'envoyer quoi que ce soit.
/// </summary>
public static class ChoixDeMachine
{
    /// <summary>
    /// Les machines à proposer, et si elles viennent de la mémoire plutôt que du relais.
    ///
    /// <paramref name="deMemoire"/> n'est pas un détail d'implémentation : l'écran DOIT le
    /// dire. Un état vieux de vingt minutes reste utile pour choisir une machine, il ne l'est
    /// plus pour croire ce qu'il annonce du papier restant.
    /// </summary>
    /// <param name="instantane">Ce que le relais vient de rendre. Vide s'il n'a rien rendu.</param>
    /// <param name="dernierConnu">Le dernier instantané abouti, ou null s'il n'y en a jamais eu.</param>
    public static (IReadOnlyList<De100PrinterInfo> Machines, bool DeMemoire) Proposer(
        IReadOnlyList<De100PrinterInfo> instantane,
        IReadOnlyList<De100PrinterInfo>? dernierConnu)
    {
        ArgumentNullException.ThrowIfNull(instantane);

        var enLigne = EnLigne(instantane);
        if (enLigne.Count > 0) return (enLigne, false);

        var memoire = dernierConnu is null ? [] : EnLigne(dernierConnu);
        return (memoire, memoire.Count > 0);
    }

    /// <summary>
    /// Une machine HORS LIGNE ne se propose pas, même de mémoire : l'imposer ferait refuser
    /// la commande en nommant une machine éteinte.
    /// </summary>
    private static List<De100PrinterInfo> EnLigne(IReadOnlyList<De100PrinterInfo> etats) =>
        etats.Where(e => e.Status != De100PrinterStatus.Offline).ToList();
}
