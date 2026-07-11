using System.Drawing.Printing;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Domain;

namespace Studio.App.Views;

/// <summary>
/// Fiche produit : création ou modification. Ne touche pas au catalogue lui-même —
/// remplit un Product et le rend à l'appelant, qui sauvegarde et recharge.
/// </summary>
public partial class ProductEditView : UserControl
{
    private readonly Product _product;
    private readonly bool _isNew;
    private readonly Func<Product, string?> _onSave; // renvoie un message d'erreur, ou null si OK

    public ProductEditView(Product product, bool isNew, Func<Product, string?> onSave)
    {
        _product = product;
        _isNew = isNew;
        _onSave = onSave;

        InitializeComponent();
        TitleText.Text = isNew ? "Nouveau produit" : $"Modifier — {product.Name}";

        PrinterCombo.ItemsSource = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        FitCombo.ItemsSource = new[]
        {
            "Remplir le format (recadre si besoin)",
            "Photo entière (marges blanches si besoin)",
        };

        NameBox.Text = product.Name;
        CodeBox.Text = product.Code;
        CodeBox.IsEnabled = isNew; // le code identifie le produit dans les commandes passées
        PriceBox.Text = product.Price.ToString("0.00", CultureInfo.CurrentCulture);
        WidthBox.Text = product.WidthMm.ToString(CultureInfo.CurrentCulture);
        HeightBox.Text = product.HeightMm.ToString(CultureInfo.CurrentCulture);
        PrinterCombo.SelectedItem = string.IsNullOrEmpty(product.PrinterName) ? null : product.PrinterName;
        FitCombo.SelectedIndex = product.DefaultFit == FitMode.Fill ? 0 : 1;
        BorderBox.Text = product.BorderMm.ToString(CultureInfo.CurrentCulture);
        DpiBox.Text = product.Dpi.ToString(CultureInfo.CurrentCulture);
        EnabledCheck.IsChecked = product.Enabled;

        SheetCheck.IsChecked = product.Sheet is not null;
        SheetCopiesBox.Text = (product.Sheet?.Copies ?? 6).ToString(CultureInfo.CurrentCulture);
        SheetWBox.Text = (product.Sheet?.CellWidthMm ?? 35).ToString(CultureInfo.CurrentCulture);
        SheetHBox.Text = (product.Sheet?.CellHeightMm ?? 45).ToString(CultureInfo.CurrentCulture);
        OnSheetToggled(this, new RoutedEventArgs());
    }

    private void OnSheetToggled(object sender, RoutedEventArgs e)
    {
        var on = SheetCheck.IsChecked == true;
        SheetCopiesBox.IsEnabled = on;
        SheetWBox.IsEnabled = on;
        SheetHBox.IsEnabled = on;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var error = Validate(out var parsed);
        if (error is not null)
        {
            ErrorText.Text = error;
            return;
        }

        _product.Name = NameBox.Text.Trim();
        if (_isNew) _product.Code = CodeBox.Text.Trim();
        _product.Price = parsed.Price;
        _product.WidthMm = parsed.Width;
        _product.HeightMm = parsed.Height;
        _product.PrinterName = (string)PrinterCombo.SelectedItem!;
        _product.DefaultFit = FitCombo.SelectedIndex == 0 ? FitMode.Fill : FitMode.Fit;
        _product.BorderMm = parsed.Border;
        _product.Dpi = parsed.Dpi;
        _product.Enabled = EnabledCheck.IsChecked == true;
        _product.Sheet = SheetCheck.IsChecked == true
            ? new SheetSpec { Copies = parsed.SheetCopies, CellWidthMm = parsed.SheetW, CellHeightMm = parsed.SheetH }
            : null;

        var saveError = _onSave(_product);
        if (saveError is not null)
        {
            ErrorText.Text = saveError;
            return;
        }
        Navigator.Back();
    }

    private sealed record Parsed(decimal Price, double Width, double Height, double Border,
        int Dpi, int SheetCopies, double SheetW, double SheetH);

    private string? Validate(out Parsed parsed)
    {
        parsed = new Parsed(0, 0, 0, 0, 300, 6, 35, 45);

        if (string.IsNullOrWhiteSpace(NameBox.Text)) return "Le nom est obligatoire.";
        if (string.IsNullOrWhiteSpace(CodeBox.Text)) return "Le code est obligatoire.";
        if (!TryParseDecimal(PriceBox.Text, out var price) || price < 0) return "Prix invalide.";
        if (!TryParseDouble(WidthBox.Text, out var width) || width <= 0) return "Largeur invalide.";
        if (!TryParseDouble(HeightBox.Text, out var height) || height <= 0) return "Hauteur invalide.";
        if (PrinterCombo.SelectedItem is null) return "Choisissez une imprimante.";
        if (!TryParseDouble(BorderBox.Text, out var border) || border < 0) return "Marge invalide.";
        if (!int.TryParse(DpiBox.Text, out var dpi) || dpi is < 72 or > 1200) return "Résolution invalide (72 à 1200 dpi).";

        int sheetCopies = 6;
        double sheetW = 35, sheetH = 45;
        if (SheetCheck.IsChecked == true)
        {
            if (!int.TryParse(SheetCopiesBox.Text, out sheetCopies) || sheetCopies is < 1 or > 24)
                return "Nombre de copies de planche invalide (1 à 24).";
            if (!TryParseDouble(SheetWBox.Text, out sheetW) || sheetW <= 0) return "Largeur de cellule invalide.";
            if (!TryParseDouble(SheetHBox.Text, out sheetH) || sheetH <= 0) return "Hauteur de cellule invalide.";
            if (sheetW >= width || sheetH >= height) return "La cellule doit être plus petite que le tirage.";
        }

        parsed = new Parsed(price, width, height, border, dpi, sheetCopies, sheetW, sheetH);
        return null;
    }

    // accepte la virgule française comme le point
    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
