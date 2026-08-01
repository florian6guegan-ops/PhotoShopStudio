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
    /// <param name="choisi">Appelé avec le produit et la finition retenus (finition nulle si le produit n'en déclare pas).</param>
    public static void Ouvrir(Button bouton, Product? produitActuel, string? finitionActuelle,
        Action<Product, string?> choisi)
    {
        ArgumentNullException.ThrowIfNull(bouton);
        ArgumentNullException.ThrowIfNull(choisi);

        var menu = new ContextMenu();

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
                entree.Click += (_, _) => choisi(retenu, null);
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
