using System.IO;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;

namespace Studio.App.Views;

public partial class CatalogView : UserControl
{
    public CatalogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        ProductsList.ItemsSource = App.Services.Catalog.All
            .OrderBy(p => !p.Enabled).ThenBy(p => p.Name)
            .Select(p => new ProductRow(p))
            .ToList();
    }

    /// <summary>Sauvegarde atomique du catalogue complet puis rechargement des services.</summary>
    private static string? SaveCatalog(IEnumerable<Product> products)
    {
        try
        {
            ProductCatalog.Save(App.Services.ProductsJson, products);
            App.Services.ReloadCatalog();
            return null;
        }
        catch (Exception ex)
        {
            return $"Échec de l'enregistrement du catalogue : {ex.Message}";
        }
    }

    private void OnNewProduct(object sender, RoutedEventArgs e)
    {
        var product = new Product { Dpi = 300, Enabled = true };
        Navigator.Go(new ProductEditView(product, isNew: true, saved =>
        {
            if (App.Services.Catalog.Find(saved.Code) is not null)
                return $"Le code « {saved.Code} » existe déjà.";
            return SaveCatalog(App.Services.Catalog.All.Append(saved));
        }), "Nouveau produit");
    }

    /// <summary>
    /// Formats mis en avant dans le module photo d'identité. Ils vivent ici parce que
    /// c'est l'écran où l'on décide ce que la boutique vend, et non dans le code.
    /// </summary>
    private void OnIdShortcuts(object sender, RoutedEventArgs e) =>
        Navigator.Go(new IdShortcutsView(), "Raccourcis photo d'identité");

    private void OnEditProduct(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProductRow row) return;
        // on édite une copie : Annuler ne doit rien laisser dans le catalogue en mémoire
        var copy = Clone(row.Product);
        Navigator.Go(new ProductEditView(copy, isNew: false, saved =>
            SaveCatalog(App.Services.Catalog.All.Select(p => p.Code == saved.Code ? saved : p))
        ), "Modifier le produit");
    }

    private void OnDuplicateProduct(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProductRow row) return;

        var copy = Clone(row.Product);
        copy.Name = $"{row.Product.Name} (copie)";
        var baseCode = row.Product.Code;
        var n = 2;
        while (App.Services.Catalog.Find($"{baseCode}-{n}") is not null) n++;
        copy.Code = $"{baseCode}-{n}";

        var error = SaveCatalog(App.Services.Catalog.All.Append(copy));
        if (error is not null)
            MessageBox.Show(error, "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
        Refresh();
    }

    private void OnToggleProduct(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProductRow row) return;
        row.Product.Enabled = !row.Product.Enabled;
        var error = SaveCatalog(App.Services.Catalog.All);
        if (error is not null)
            MessageBox.Show(error, "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
        Refresh();
    }

    /// <summary>
    /// Supprime un produit du catalogue.
    ///
    /// <b>Un produit cité par une commande récente n'est pas supprimé mais DÉSACTIVÉ.</b>
    /// Tout le circuit d'impression appelle <c>ProductCatalog.Require(code)</c>, qui lève
    /// si le code a disparu : une commande en attente de réimpression deviendrait
    /// inexploitable, et l'opérateur ne pourrait plus la rejouer. Désactivé, le produit
    /// sort des listes de vente mais reste lisible par les commandes qui le citent.
    ///
    /// La fenêtre d'examen est celle des écrans qui montrent les commandes (30 jours) :
    /// au-delà, une commande n'est plus atteignable depuis l'application.
    /// </summary>
    private void OnDeleteProduct(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProductRow row) return;
        var product = row.Product;

        var commandes = App.Services.Store.ScanRecent(days: 30);
        var citations = ProductCatalog.CountReferences(product.Code, commandes);

        if (citations > 0)
        {
            if (!product.Enabled)
            {
                MessageBox.Show(
                    $"« {product.Name} » est cité par {citations} commande(s) des 30 derniers jours : " +
                    "le supprimer les rendrait impossibles à réimprimer.\n\n" +
                    "Il est déjà désactivé — il ne sera plus proposé à la vente. Vous pourrez le " +
                    "supprimer quand ces commandes seront sorties de la fenêtre des 30 jours.",
                    "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var reponse = MessageBox.Show(
                $"« {product.Name} » est cité par {citations} commande(s) des 30 derniers jours : " +
                "le supprimer les rendrait impossibles à réimprimer.\n\n" +
                "Le désactiver à la place ? Il disparaîtra des listes de vente sans rien casser.",
                "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (reponse != MessageBoxResult.Yes) return;

            product.Enabled = false;
            Signaler(SaveCatalog(App.Services.Catalog.All));
            Refresh();
            return;
        }

        var confirmation = MessageBox.Show(
            $"Supprimer « {product.Name} » du catalogue ?\n\n" +
            "Aucune commande récente ne le cite. Cette action est définitive.",
            "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        Signaler(SaveCatalog(App.Services.Catalog.All.Where(p => p.Code != product.Code)));
        Refresh();
    }

    private static void Signaler(string? erreur)
    {
        if (erreur is not null)
            MessageBox.Show(erreur, "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Copie complète : voir <see cref="Product.Copy"/>, où vit la règle.</summary>
    private static Product Clone(Product p) => p.Copy();

    private void OnEditFinishes(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProductRow row) return;
        Navigator.Go(new FinishesView(row.Product), "Finitions");
    }

    private void OnCaptureDevmode(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProductRow row) return;
        var services = App.Services;
        var product = row.Product;

        try
        {
            byte[]? current = product.DevmodeFile is not null
                ? File.ReadAllBytes(Path.Combine(services.CatalogDir, product.DevmodeFile))
                : null;

            var captured = DevMode.ShowDriverDialog(product.PrinterName, current);
            if (captured is null) return; // dialogue annulé

            var fileName = $"devmode-{product.Code}.bin";
            File.WriteAllBytes(Path.Combine(services.CatalogDir, fileName), captured);
            product.DevmodeFile = fileName;
            ProductCatalog.Save(services.ProductsJson, services.Catalog.All);
            services.ReloadCatalog();

            MessageBox.Show(
                $"Réglages du pilote enregistrés pour « {product.Name} ».\n" +
                "Ils seront appliqués à chaque impression de ce produit.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible de capturer les réglages : {ex.Message}\n\n" +
                $"Vérifiez que l'imprimante « {product.PrinterName} » est installée et allumée.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Refresh();
    }

    private sealed record ProductRow(Product Product)
    {
        public string Title => $"{Product.Name} — {Product.Price:0.00} €";

        public string ToggleLabel => Product.Enabled ? "Désactiver" : "Activer";

        public string Details =>
            $"{Product.WidthMm:0}×{Product.HeightMm:0} mm — {Product.PrinterName}" +
            (Product.Sheet is not null ? $" — planche {Product.Sheet.Copies}×" : "") +
            $" — réglages pilote : {(Product.DevmodeFile is not null ? "capturés ✓" : "par défaut")}" +
            $" — couleur : {(Product.IccProfile ?? "pilote")}" +
            (Product.Finishes.Count > 0
                ? $" — finitions : {string.Join(", ", Product.Finishes.Select(f => f.Name))}"
                : "") +
            (Product.Enabled ? "" : " — DÉSACTIVÉ");
    }
}
