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
    /// <param name="genre">
    /// Ce qu'on vend : la planche ordinaire, celle de la rentrée, ou la planche accompagnée
    /// d'un tirage 10×15. Il décide de DEUX choses, et de rien d'autre — le prix, et la
    /// feuille supplémentaire. Voir <see cref="GenreDePlanche"/>.
    /// </param>
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
        Action<Order>? surCommande = null,
        GenreDePlanche genre = GenreDePlanche.Standard)
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
        //
        // Les deux formats de la rentrée ont le leur — 11 € et 12 € —, le même pour tous
        // les pays : ce sont des produits de saison, pas des démarches administratives.
        var prix = services.TarifsIdentite.Pour(document.Country, genre);

        var articles = aTirer
            .Select(p => new DraftItem(
                p.SourcePath, p.Produit, p.Quantite, p.Crop, 0, p.RedressementDegres,
                null, p.Reglages, p.Copies, p.Finition,
                // la case suit le DOCUMENT, jamais celle inscrite au produit
                SheetCell: new SheetCellSize(document.WidthMm, document.HeightMm),
                // et le PRIX aussi : c'est le document qui le fixe, pas le papier
                UnitPriceOverride: prix,
                // le cadrage du portrait, sur la planche de rentrée : la commande doit le
                // garder, sans quoi une réimpression sortirait un autre cadrage
                CropGrande: genre == GenreDePlanche.Rentree ? p.CropGrande : null,
                // les repères du visage : c'est ce qui permet de rouvrir la planche telle
                // qu'elle est sortie depuis « Commandes du jour › Photos d'identité »
                Reperes: p.Reperes,
                // hors norme déclaré au récapitulatif : la commande le garde, pour qu'une
                // réimpression ressorte avec le même avertissement — c'est justement quand
                // la photo revient au comptoir qu'il sert
                PhotosNonConformes: p.NonConforme))
            .ToList();

        // LA FEUILLE EN PLUS, quand c'est « la planche ET une 10×15 ».
        //
        // Un article de plus dans la MÊME commande, jamais une seconde commande : le client
        // passe une fois en caisse, et le ticket porte une ligne « planche » et une ligne
        // « tirage ». Son prix est à zéro — les douze euros sont déjà sur la planche —,
        // faute de quoi le format se facturerait deux fois.
        //
        // Un catalogue sans 10×15 utilisable ne fait pas échouer le tirage : la planche
        // part, et l'opérateur est prévenu qu'il manque le papier. Mieux vaut une feuille
        // manquante qu'un client sans rien.
        if (genre == GenreDePlanche.PlancheEtTirage)
        {
            var papier = PortraitDeLaPlanche.TirageQuiAccompagne(aTirer[0].Produit);

            if (papier is null)
            {
                MessageBox.Show(
                    "Aucun 10×15 activé au catalogue : la planche va sortir seule.\n\n" +
                    "Ouvrez Catalogue et activez un tirage 10×15 pour que la grande photo " +
                    "parte avec.",
                    "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                var cotes = PortraitDeLaPlanche.Cotes(genre, aTirer[0].Produit, document, 1);

                articles.AddRange(aTirer.Select(p => new DraftItem(
                    p.SourcePath, papier, p.Quantite,
                    PortraitDeLaPlanche.Cadre(p.SourcePath, p.Crop, p.CropGrande, cotes,
                        p.RedressementDegres),
                    0, p.RedressementDegres,
                    // La finition de la planche ne se recopie PAS : c'est un DEVMODE nommé
                    // du produit planche, et le 10×15 a les siens. Null = les réglages
                    // pilote par défaut de son propre produit.
                    FitMode.Fill, p.Reglages, null, null,
                    UnitPriceOverride: 0m)));
            }
        }

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
