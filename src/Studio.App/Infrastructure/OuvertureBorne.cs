using System.IO;
using System.Windows;
using Studio.App.Views;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.App.Infrastructure;

/// <summary>
/// Ouvrir une commande de borne dans l'écran des photos — <b>le seul chemin</b>.
///
/// Il y en avait deux, mot pour mot identiques : <c>HomeView.OuvrirLaBorne</c> et
/// <c>KioskOrdersView.Ouvrir</c>. Les commandes de bornes sont listées à deux endroits
/// (voir <c>system_architecture.md</c>) et toute action ajoutée à l'un doit l'être à
/// l'autre — c'est vrai des BOUTONS, mais leur comportement n'a aucune raison d'être écrit
/// deux fois. Ce qui se perdait à l'ouverture se perdait deux fois, et se corrigeait une.
/// </summary>
internal static class OuvertureBorne
{
    /// <summary>
    /// Archive les photos, marque la commande en cours, et ouvre la grille.
    ///
    /// <b>Rien n'est créé ici</b> : la commande Studio naîtra à l'impression, comme pour
    /// une commande faite au comptoir. La commande de borne passe « en cours » et reste
    /// affichée — si l'opérateur abandonne en route, elle ne se perd pas.
    /// </summary>
    /// <param name="taille">
    /// Taille libre demandée au comptoir, ou null pour le format commandé à la borne.
    /// </param>
    /// <param name="reprendreLAttente">
    /// Reprendre le travail mis de côté, s'il y en a un. Faux repart du cadrage validé par
    /// le client : c'est le « ✕ attente » de la liste, et sans lui une mise de côté serait
    /// définitive.
    /// </param>
    public static void Ouvrir(DiLandOrder commande, CustomSize? taille,
        bool reprendreLAttente = true)
    {
        ArgumentNullException.ThrowIfNull(commande);

        var importateur = App.Services.DiLandImport;

        try
        {
            // les photos sont rangées chez NOUS, pour trente jours : l'écran travaille sur
            // notre copie et non sur les dossiers de DiLand, qu'il purge quand il veut
            var prete = importateur.Archiver(commande);

            if (prete.PhotoCount == 0)
            {
                MessageBox.Show("Aucune photo n'a pu être récupérée pour cette commande.",
                    "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            importateur.MarkInProgress(commande);

            var enAttente = reprendreLAttente ? App.Services.CommandesEnAttente.PourLaBorne(commande.Oid) : null;

            // Un travail fait en taille libre rouvre DANS cette taille : le format commandé
            // n'a plus cours, et rouvrir en 10×15 des cadres réglés pour du 5,5 × 8 les
            // remettrait tous au centre. La taille demandée à l'instant, elle, l'emporte —
            // c'est le geste le plus récent.
            taille ??= enAttente is { EnTaillePersonnalisee: true }
                ? new CustomSize(enAttente.CustomWidthMm, enAttente.CustomHeightMm, enAttente.PaperCode)
                : null;

            Navigator.Go(
                new PhotoGridView(prete.PhotosDirectory,
                    taille is null ? prete.ProductCode : null,
                    commande.Oid,
                    taillePerso: taille,
                    cadragesBorne: prete.Cadrages,
                    enAttente: enAttente),
                Titre(commande, taille, prete.PhotoCount, enAttente));
        }
        catch (Exception ex)
        {
            FileLog.Write("Commandes des bornes : ouverture impossible", ex);
            MessageBox.Show($"Ouverture impossible : {ex.Message}",
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Titre(DiLandOrder commande, CustomSize? taille, int photos,
        TravailEnAttente? enAttente)
    {
        var titre = $"Borne #{commande.Number}";
        if (taille is not null) titre += $" — {taille.Libelle}";
        titre += $" — {photos} photo(s)";
        if (enAttente is not null) titre += " — reprise";
        return titre;
    }

    /// <summary>
    /// Abandonne ce qui attend au nom d'une commande de borne, après confirmation.
    ///
    /// C'est la seule porte de retour vers le cadrage du CLIENT : une fois la commande
    /// mise de côté, c'est ce travail-là qui est repris à chaque ouverture, et sans cette
    /// sortie on ne reverrait plus jamais ce que le client avait validé à la borne.
    /// </summary>
    /// <returns>Vrai si le travail a été abandonné.</returns>
    public static bool RepartirDeZero(DiLandOrder commande)
    {
        ArgumentNullException.ThrowIfNull(commande);

        var reponse = MessageBox.Show(
            $"Abandonner le travail en attente sur la commande #{commande.Number} ?\n\n" +
            "Ce qui a été mis de côté est perdu. La commande se rouvrira avec le cadrage " +
            "que le client a validé à la borne.",
            "En attente", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (reponse != MessageBoxResult.Yes) return false;

        App.Services.CommandesEnAttente.EffacerPourBorne(commande.Oid);
        FileLog.Write($"Travail en attente abandonné pour la commande de borne {commande.Oid}");
        return true;
    }

    /// <summary>Ce qui attend au nom d'une commande, ou null. Sert aux deux listes pour l'annoncer.</summary>
    public static TravailEnAttente? EnAttenteDe(DiLandOrder commande) =>
        App.Services.CommandesEnAttente.PourLaBorne(commande.Oid);

    /// <summary>
    /// Rouvre les photos d'une commande CLOSE, depuis l'historique.
    ///
    /// La commande ne revient pas dans la liste du jour : elle a été servie, et la revoir
    /// le lendemain matin ferait croire à un tirage en retard. <c>MarkInProgress</c> refuse
    /// déjà de rouvrir une entrée close — on s'appuie dessus plutôt que d'ajouter une
    /// seconde règle qui pourrait la contredire.
    ///
    /// Elle n'a ni cadrage de borne ni brouillon à reprendre : l'un comme l'autre ont
    /// disparu avec la clôture, et la commande a déjà été tirée.
    /// </summary>
    public static void OuvrirDepuisLHistorique(KioskOrderEntry entree, string dossier)
    {
        ArgumentNullException.ThrowIfNull(entree);

        var combien = Directory.EnumerateFiles(dossier).Count();
        if (combien == 0)
        {
            MessageBox.Show("Aucune photo n'a pu être récupérée pour cette commande.",
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Navigator.Go(
            new PhotoGridView(dossier, produitParDefaut: null, entree.Oid),
            $"Borne #{entree.Number} (historique) — {combien} photo(s)");
    }
}
