using System.IO;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;

namespace Studio.App.Views;

/// <summary>
/// Finitions d'un produit (brillant, mat, lustré…). Chaque finition est un DEVMODE
/// capturé dans le dialogue du pilote — c'est là que la finition se choisit réellement
/// (surlaminage DNP, type de média). L'app ne code aucun nom de finition en dur.
/// </summary>
public partial class FinishesView : UserControl
{
    private readonly Product _product;

    public FinishesView(Product product)
    {
        _product = product;
        InitializeComponent();
        TitleText.Text = $"Finitions — {product.Name}";
        Refresh();
    }

    private void Refresh() =>
        FinishesList.ItemsSource = _product.Finishes
            .Select(f => new FinishRow(f.Name, f.DevmodeFile))
            .ToList();

    private sealed record FinishRow(string Name, string File);

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "Donnez d'abord un nom à la finition (Brillant, Mat, Lustré…).";
            return;
        }
        if (_product.Finishes.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorText.Text = $"La finition « {name} » existe déjà — utilisez « Recapturer ».";
            return;
        }

        if (Capture(name) is { } file)
        {
            _product.Finishes.Add(new FinishOption { Name = name, DevmodeFile = file });
            if (Save()) NameBox.Clear();
            Refresh();
        }
    }

    private void OnRecapture(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FinishRow row) return;
        var finish = _product.Finishes.First(f => f.Name == row.Name);

        if (Capture(finish.Name) is not null)
        {
            Save();
            Refresh();
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FinishRow row) return;

        var answer = MessageBox.Show(
            $"Retirer la finition « {row.Name} » de « {_product.Name} » ?",
            "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _product.Finishes.RemoveAll(f => f.Name == row.Name);
        Save();
        Refresh();
    }

    /// <summary>Ouvre le dialogue du pilote et écrit le DEVMODE ; renvoie le nom du fichier, null si annulé.</summary>
    private string? Capture(string finishName)
    {
        ErrorText.Text = "";
        var services = App.Services;
        var fileName = $"devmode-{_product.Code}-{Slug(finishName)}.bin";
        var path = Path.Combine(services.CatalogDir, fileName);

        try
        {
            byte[]? current = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var captured = DevMode.ShowDriverDialog(_product.PrinterName, current);
            if (captured is null) return null; // dialogue annulé

            File.WriteAllBytes(path, captured);
            return fileName;
        }
        catch (Exception ex)
        {
            FileLog.Write($"Capture de la finition « {finishName} » impossible", ex);
            ErrorText.Text = $"Capture impossible : {ex.Message} — l'imprimante " +
                             $"« {_product.PrinterName} » est-elle allumée ?";
            return null;
        }
    }

    private bool Save()
    {
        try
        {
            var services = App.Services;
            ProductCatalog.Save(services.ProductsJson,
                services.Catalog.All.Select(p => p.Code == _product.Code ? _product : p));
            services.ReloadCatalog();
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write("Enregistrement des finitions impossible", ex);
            ErrorText.Text = $"Échec de l'enregistrement : {ex.Message}";
            return false;
        }
    }

    /// <summary>Nom de fichier sûr : « Lustré » → « lustre ».</summary>
    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant()
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => char.IsLetterOrDigit(c) && c < 128)
            .ToArray();
        return chars.Length == 0 ? "finition" : new string(chars);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Navigator.Back();
}
