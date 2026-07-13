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

    private static Product Clone(Product p) => new()
    {
        Code = p.Code,
        Name = p.Name,
        WidthMm = p.WidthMm,
        HeightMm = p.HeightMm,
        PrinterName = p.PrinterName,
        PrinterChannel = p.PrinterChannel,
        Dpi = p.Dpi,
        Price = p.Price,
        DefaultFit = p.DefaultFit,
        BorderMm = p.BorderMm,
        IccProfile = p.IccProfile,
        DevmodeFile = p.DevmodeFile,
        Finishes = p.Finishes
            .Select(f => new FinishOption
            {
                Name = f.Name,
                DevmodeFile = f.DevmodeFile,
                IccProfile = f.IccProfile,
            })
            .ToList(),
        Sheet = p.Sheet is null ? null : new SheetSpec
        {
            Copies = p.Sheet.Copies,
            CellWidthMm = p.Sheet.CellWidthMm,
            CellHeightMm = p.Sheet.CellHeightMm,
            GapMm = p.Sheet.GapMm,
            CutMarks = p.Sheet.CutMarks,
        },
        Enabled = p.Enabled,
    };

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
