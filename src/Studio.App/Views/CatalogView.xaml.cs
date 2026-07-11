using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            .Select(p => new ProductRow(p))
            .ToList();
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

        public string Details =>
            $"{Product.WidthMm:0}×{Product.HeightMm:0} mm — {Product.PrinterName}" +
            (Product.Sheet is not null ? $" — planche {Product.Sheet.Copies}×" : "") +
            $" — réglages pilote : {(Product.DevmodeFile is not null ? "capturés ✓" : "par défaut")}" +
            (Product.Enabled ? "" : " — DÉSACTIVÉ");
    }
}
