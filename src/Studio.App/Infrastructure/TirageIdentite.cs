using System.Windows;
using System.Windows.Input;
using Studio.App.Views;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;
using Studio.Store;

namespace Studio.App.Infrastructure;

/// <summary>
/// Engager le papier sur une planche d'identité : UNE commande pour tout le lot.
///
/// <b>Pourquoi c'est sorti du récapitulatif.</b> Le poste identité n'a plus d'écran de
/// récapitulatif — sur ce logiciel-là, l'opérateur voit la planche dans le panneau et
/// imprime, comme le fait ID Maker. Le Studio complet, lui, garde son récapitulatif. Les
/// deux boutons mènent donc au même endroit, et il ne doit exister qu'un seul chemin :
/// la commande, son prix, la planche mise de côté qu'elle solde, l'envoi à la machine.
///
/// C'est la règle du dépôt, et elle a déjà coûté deux pannes invisibles ici
/// (<c>ReglagesRetenus</c> / <c>ReglagesDe</c>) : <b>les BOUTONS se doublent, ce qu'ils
/// font, non.</b>
/// </summary>
public static class TirageIdentite
{
    /// <summary>
    /// Crée la commande et lance le tirage, puis rentre à l'accueil du poste.
    ///
    /// Comme sur les tirages, seule la CRÉATION de la commande est attendue — c'est court :
    /// un numéro, une enveloppe, la copie des originaux. Le rendu des planches et l'envoi à
    /// la machine partent en tâche de fond, et l'avancement se lit dans le bandeau du haut.
    /// </summary>
    /// <param name="planches">Le lot. Celles à zéro exemplaire sont laissées de côté.</param>
    /// <param name="document">La norme visée : elle fixe la case ET le prix.</param>
    /// <param name="attenteId">
    /// La planche mise de côté que cette impression solde, s'il y en a une. Sans elle,
    /// l'accueil continuerait de proposer « Reprendre » sur une planche déjà tirée — et on
    /// la tirerait deux fois.
    /// </param>
    /// <param name="surCommande">
    /// Appelé une fois la commande CRÉÉE, avec elle.
    ///
    /// C'est le moment exact où la photo devient « faite » : le papier est engagé et le
    /// numéro existe. L'écran s'en sert pour porter ses photos à l'historique des trente
    /// jours — il est le seul à tenir les repères de crâne et de menton, que la commande, elle,
    /// ne garde pas. Avant l'appel, rien n'est parti ; après, la page est déjà rentrée à
    /// l'accueil.
    ///
    /// Une exception venue d'ici n'arrête pas le tirage : elle est journalisée et le papier
    /// part quand même — l'historique est un confort, la commande est le geste.
    /// </param>
    /// <returns>
    /// Faux si rien n'est parti : l'appelant remet son bouton en service. L'opérateur a
    /// déjà été prévenu à l'écran, il n'y a rien à ajouter.
    /// </returns>
    public static async Task<bool> LancerAsync(
        IReadOnlyList<IdSheetRecapView.Planche> planches,
        IdDocumentSpec document,
        Guid? attenteId,
        Action<Order>? surCommande = null)
    {
        ArgumentNullException.ThrowIfNull(planches);

        var aTirer = planches.Where(p => p.Quantite > 0).ToList();
        if (aTirer.Count == 0)
        {
            MessageBox.Show(
                "Toutes les planches sont à zéro : il n'y a rien à imprimer.\n\n" +
                "Remontez la quantité d'au moins une planche.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var services = App.Services;

        // Le prix vient du DOCUMENT, pas du papier : 10 € pour un document français, 15 €
        // pour un étranger. C'est le même produit du catalogue dans les deux cas.
        var prix = services.TarifsIdentite.Pour(document.Country);

        var articles = aTirer
            .Select(p => new DraftItem(
                p.SourcePath, p.Produit, p.Quantite, p.Crop, 0, p.RedressementDegres,
                null, p.Reglages, p.Copies, p.Finition,
                // la case suit le DOCUMENT, jamais celle inscrite au produit
                SheetCell: new SheetCellSize(document.WidthMm, document.HeightMm),
                // et le PRIX aussi : c'est le document qui le fixe, pas le papier
                UnitPriceOverride: prix))
            .ToList();

        Mouse.OverrideCursor = CurseurStudio.Attente;

        try
        {
            var commande = await Task.Run(() => services.Orders.CreateOrder("Operateur", articles));

            // La planche est passée en caisse : ce qui attendait en son nom n'a plus
            // d'objet. Le laisser ferait proposer « Reprendre » sur l'accueil pour une
            // planche déjà tirée, et on la tirerait deux fois.
            if (attenteId is { } attente) services.CommandesEnAttente.Effacer(attente);

            // La photo est faite : elle entre à l'historique des trente jours. Un échec
            // ici ne doit rien arrêter — le papier part, et c'est ce qui compte.
            try
            {
                surCommande?.Invoke(commande);
            }
            catch (Exception ex)
            {
                FileLog.Write("Planche non portée à l'historique des photos d'identité", ex);
            }

            Mouse.OverrideCursor = null;

            services.Impressions.Lancer(commande,
                imprimer: (avancement, arret) =>
                {
                    foreach (var enveloppe in commande.Envelopes)
                        services.Printer.PrintEnvelope(commande, enveloppe,
                            progression: avancement, ct: arret);
                });

            AccueilStudio.Rentrer();
            return true;
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Échec de la création de la commande (planches identité)", ex);
            MessageBox.Show($"Commande impossible à créer : {ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
