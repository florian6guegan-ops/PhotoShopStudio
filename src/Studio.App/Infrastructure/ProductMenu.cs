using System;
using System.Windows.Controls;
using Studio.Core.Domain;

namespace Studio.App.Infrastructure;

/// <summary>
/// Le catalogue déroulé sous un bouton : une entrée par produit, ou par finition quand le
/// produit en déclare.
///
/// Le même menu sert à choisir un produit dans la grille et à changer de format depuis
/// l'écran « Modifier ». Deux listes séparées auraient fini par diverger, et l'opérateur
/// aurait vu deux catalogues différents selon l'endroit d'où il vient.
/// </summary>
public static class ProductMenu
{
    /// <param name="bouton">Le bouton sous lequel le menu s'ouvre.</param>
    /// <param name="produitActuel">Coché dans la liste, pour qu'on voie où l'on en est.</param>
    /// <param name="finitionActuelle">Idem pour la finition.</param>
    /// <param name="choisi">
    /// Appelé avec le produit et la finition retenus. Un produit qui ne déclare pas de
    /// finition rend <paramref name="finitionActuelle"/> telle quelle : la finition d'une
    /// commande de borne ne vient pas du catalogue et ne doit pas se perdre en changeant
    /// de format.
    /// </param>
    /// <param name="personnalise">
    /// Appelé quand l'opérateur demande une taille qui n'est pas au catalogue. Null =
    /// l'entrée n'apparaît pas.
    ///
    /// Elle existe parce qu'une commande arrive souvent dans un format standard — une borne,
    /// un envoi de téléphone — et que le client change d'avis au comptoir. Sans elle, il
    /// fallait repartir de l'accueil et retrouver le dossier, en perdant recadrages et
    /// corrections déjà faits.
    /// </param>
    /// <param name="agrandissement">
    /// Appelé quand l'opérateur demande un AGRANDISSEMENT à taille libre — A2, A3… Null =
    /// l'entrée n'apparaît pas.
    ///
    /// Distinct de <paramref name="personnalise"/>, et ce n'est pas un détail : celui-là
    /// compose des planches sur du papier minilab, celui-ci sort un tirage unique en fichier
    /// pour l'Epson. Les confondre enverrait un A2 au minilab, qui le refuserait.
    /// </param>
    public static void Ouvrir(Button bouton, Product? produitActuel, string? finitionActuelle,
        Action<Product, string?> choisi, Action? personnalise = null, Action? agrandissement = null)
    {
        ArgumentNullException.ThrowIfNull(bouton);
        ArgumentNullException.ThrowIfNull(choisi);

        var menu = new ContextMenu();

        if (personnalise is not null)
        {
            var libre = new MenuItem
            {
                Header = "Personnalisé…  (taille au choix)",
                FontSize = 18,
                FontWeight = System.Windows.FontWeights.SemiBold,
            };
            libre.Click += (_, _) => personnalise();
            menu.Items.Add(libre);
        }

        if (agrandissement is not null)
        {
            var grand = new MenuItem
            {
                Header = "Agrandissement personnalisé…  (A2, A3…)",
                FontSize = 18,
                FontWeight = System.Windows.FontWeights.SemiBold,
            };
            grand.Click += (_, _) => agrandissement();
            menu.Items.Add(grand);
        }

        if (personnalise is not null || agrandissement is not null)
            menu.Items.Add(new Separator());

        foreach (var produit in App.Services.Catalog.Enabled)
        {
            var retenu = produit;

            // un produit sans finition déclarée reste une seule entrée ; sinon une par finition
            if (produit.Finishes.Count == 0)
            {
                var entree = new MenuItem
                {
                    Header = $"{produit.Name} — {produit.Price:0.00} €",
                    FontSize = 18,
                    IsChecked = produitActuel?.Code == produit.Code,
                };
                // La finition en cours est CONSERVÉE, et non remise à zéro. Sur le DE100
                // elle ne vient pas du catalogue mais du CLIENT, qui l'a choisie à la
                // borne, et c'est elle qui décide du rouleau donc de la machine. La
                // remettre à null ici faisait repartir une commande lustrée en brillant
                // au premier changement de format fait au comptoir — sans un mot.
                entree.Click += (_, _) => choisi(retenu, finitionActuelle);
                menu.Items.Add(entree);
                continue;
            }

            foreach (var finition in produit.Finishes)
            {
                var retenue = finition.Name;
                var entree = new MenuItem
                {
                    Header = $"{produit.Name} — {retenue} — {produit.Price:0.00} €",
                    FontSize = 18,
                    IsChecked = produitActuel?.Code == produit.Code && finitionActuelle == retenue,
                };
                entree.Click += (_, _) => choisi(retenu, retenue);
                menu.Items.Add(entree);
            }
        }

        menu.PlacementTarget = bouton;
        menu.IsOpen = true;
    }
}
