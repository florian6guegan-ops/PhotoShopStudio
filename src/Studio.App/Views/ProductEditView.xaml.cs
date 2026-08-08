using System.Drawing.Printing;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

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
        OutputCombo.ItemsSource = Sorties.Select(s => s.Libelle).ToList();
        FitCombo.ItemsSource = Cadrages.Select(c => c.Libelle).ToList();

        NameBox.Text = product.Name;
        CodeBox.Text = product.Code;
        CodeBox.IsEnabled = isNew; // le code identifie le produit dans les commandes passées
        PriceBox.Text = product.Price.ToString("0.00", CultureInfo.CurrentCulture);
        WidthBox.Text = product.WidthMm.ToString(CultureInfo.CurrentCulture);
        HeightBox.Text = product.HeightMm.ToString(CultureInfo.CurrentCulture);
        PrinterCombo.SelectedItem = string.IsNullOrEmpty(product.PrinterName) ? null : product.PrinterName;
        OutputCombo.SelectedIndex = Math.Max(0, Array.FindIndex(Sorties, s => s.Valeur == product.Output));
        FitCombo.SelectedIndex = Math.Max(0, Array.FindIndex(Cadrages, c => c.Valeur == product.DefaultFit));
        BorderBox.Text = product.BorderMm.ToString(CultureInfo.CurrentCulture);
        DpiBox.Text = product.Dpi.ToString(CultureInfo.CurrentCulture);
        PrintExposureBox.Text = product.PrintExposure.ToString("0.##", CultureInfo.CurrentCulture);
        MinilabSizeNameBox.Text = product.MinilabPrintSizeName ?? "";
        EnabledCheck.IsChecked = product.Enabled;

        RefreshIccList(product.IccProfile);

        SheetCheck.IsChecked = product.Sheet is not null;
        SheetCopiesBox.Text = (product.Sheet?.Copies ?? 6).ToString(CultureInfo.CurrentCulture);
        SheetWBox.Text = (product.Sheet?.CellWidthMm ?? 35).ToString(CultureInfo.CurrentCulture);
        SheetHBox.Text = (product.Sheet?.CellHeightMm ?? 45).ToString(CultureInfo.CurrentCulture);
        OnSheetToggled(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Les trois circuits d'impression, dans les mots du comptoir.
    ///
    /// Ce choix n'était pas saisissable : <see cref="Product.Output"/> retombait donc sur
    /// son défaut (file Windows) à chaque enregistrement, et un tirage du minilab devenait
    /// un tirage imprimante sans que personne ne l'ait demandé.
    /// </summary>
    private static readonly (ProductOutput Valeur, string Libelle)[] Sorties =
    [
        (ProductOutput.Printer, "File d'impression Windows (DS620…)"),
        (ProductOutput.FujiMinilab, "Minilab Fuji DE100 (SDK, pas le spouleur)"),
        (ProductOutput.ManualFile, "Fichier repris à la main (Epson, Photoshop)"),
        // Toute valeur de ProductOutput DOIT figurer ici. La sortie d'un produit absent de
        // la liste retombe sur la première — la file Windows — et l'enregistrement la lui
        // impose sans rien dire : ouvrir la fiche de l'envoi par courriel en aurait fait
        // un produit imprimé. C'est le même piège que Product.Copy(), qui oubliait Output.
        (ProductOutput.Email, "Envoi par courriel (rien n'est imprimé)"),
    ];

    /// <summary>
    /// Seule la sortie « file Windows » exige une imprimante. Le minilab est piloté par le
    /// SDK Fuji et les agrandissements sortent en fichiers : leur imposer une file
    /// obligerait à en désigner une au hasard, et c'est exactement ce qui brouille le
    /// routage des enveloppes.
    /// </summary>
    private bool ImprimanteRequise =>
        OutputCombo.SelectedIndex >= 0 && Sorties[OutputCombo.SelectedIndex].Valeur == ProductOutput.Printer;

    private void OnOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrinterCombo is null) return;   // appelé pendant InitializeComponent
        PrinterCombo.IsEnabled = ImprimanteRequise;
        if (!ImprimanteRequise) PrinterCombo.SelectedItem = null;
    }

    /// <summary>
    /// Les cadrages proposés. Le Polaroid en est un : il pose la photo dans une fenêtre
    /// presque carrée entourée du cadre blanc du film 600, bande large en bas.
    /// </summary>
    private static readonly (FitMode Valeur, string Libelle)[] Cadrages =
    [
        (FitMode.Fill, "Remplir le format (recadre si besoin)"),
        (FitMode.Fit, "Photo entière (marges blanches si besoin)"),
        (FitMode.Polaroid, "Polaroid (fenêtre carrée, bande blanche en bas)"),
    ];

    /// <summary>Entrée « aucun profil » : la couleur est alors laissée au pilote (comportement d'origine).</summary>
    private const string NoIcc = "Aucun — le pilote gère la couleur";

    private void RefreshIccList(string? selected)
    {
        var profiles = IccProfiles.List(App.Services.CatalogDir);
        IccCombo.ItemsSource = new[] { NoIcc }.Concat(profiles).ToList();
        IccCombo.SelectedItem = selected is not null && profiles.Contains(selected) ? selected : NoIcc;
    }

    /// <summary>Importe un profil livré par le pilote (DS620-R0.icc, DE100 Lustre.icc…) dans catalog/icc.</summary>
    private void OnImportIcc(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir un profil ICC",
            Filter = "Profils couleur (*.icc;*.icm)|*.icc;*.icm",
            InitialDirectory = IccProfiles.WindowsColorDir,
        };
        DossiersFavoris.Epingler(dialog);
        if (dialog.ShowDialog() != true) return;

        try
        {
            var fileName = IccProfiles.Import(App.Services.CatalogDir, dialog.FileName);
            RefreshIccList(fileName);
            ErrorText.Text = "";
        }
        catch (Exception ex)
        {
            FileLog.Write("Import du profil ICC impossible", ex);
            ErrorText.Text = $"Import impossible : {ex.Message}";
        }
    }

    private void OnSheetToggled(object sender, RoutedEventArgs e)
    {
        var on = SheetCheck.IsChecked == true;
        SheetCopiesBox.IsEnabled = on;
        SheetWBox.IsEnabled = on;
        SheetHBox.IsEnabled = on;
    }

    /// <summary>
    /// Rappelle en centimètres ce qu'on est en train de saisir en millimètres.
    ///
    /// Le champ dit « (mm) » depuis toujours, et cela n'a pas suffi : les formats du métier
    /// se nomment en centimètres, et « 40 × 50 » saisi dans deux cases voisines ressemble à
    /// s'y méprendre à ce qu'on voulait. L'équivalent affiché à côté rend l'erreur visible
    /// AVANT d'enregistrer — « 4 × 5 cm » ne se confond avec rien.
    /// </summary>
    private void OnCotesChanged(object sender, TextChangedEventArgs e)
    {
        if (CotesEnCmText is null) return; // frappe pendant l'initialisation du XAML

        if (TryParseDouble(WidthBox.Text, out var largeur) && largeur > 0
            && TryParseDouble(HeightBox.Text, out var hauteur) && hauteur > 0)
        {
            CotesEnCmText.Text = $"= {largeur / 10:0.#} × {hauteur / 10:0.#} cm";

            // le rapport de dix se signale dès la frappe, sans attendre l'enregistrement
            var suspect = CotesProduit.SiSaisiEnCentimetres(
                NameBox.Text, CodeBox.Text, largeur, hauteur) is not null;

            CotesEnCmText.Foreground = (Brush)Application.Current.Resources[
                suspect ? "DangerBrush" : "MutedBrush"];
        }
        else
        {
            CotesEnCmText.Text = "";
        }
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

        // DES CENTIMÈTRES SAISIS DANS UN CHAMP EN MILLIMÈTRES ?
        //
        // Le 08/08/2026, un poste équipé d'un traceur grand format a sorti un « 40×50 » en
        // 4 × 5 cm : le produit était réglé sur 40 × 50 mm, et l'application a imprimé
        // exactement ce qu'on lui demandait. Les noms du métier sont en centimètres, les
        // cotes en millimètres — lire « 40x50 » sur une fiche et saisir 40 puis 50 est le
        // raisonnement le plus naturel du monde, et rien ne l'arrêtait.
        //
        // On propose, on n'impose pas : le format peut légitimement ne pas coller à son
        // nom, et c'est l'exploitant qui sait ce qu'il vend.
        if (CotesProduit.SiSaisiEnCentimetres(
                NameBox.Text, CodeBox.Text, parsed.Width, parsed.Height) is { } voulu)
        {
            var reponse = MessageBox.Show(
                $"Ce produit s'appelle « {NameBox.Text.Trim()} » mais mesure " +
                $"{parsed.Width / 10:0.#} × {parsed.Height / 10:0.#} cm.\n\n" +
                $"Les cotes se saisissent en MILLIMÈTRES. Vouliez-vous " +
                $"{voulu.LargeurMm:0.#} × {voulu.HauteurMm:0.#} mm ?\n\n" +
                "« Oui » corrige les cotes · « Non » les garde telles quelles.",
                "Studio Photo", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            if (reponse == MessageBoxResult.Cancel) return;

            if (reponse == MessageBoxResult.Yes)
            {
                parsed = parsed with { Width = voulu.LargeurMm, Height = voulu.HauteurMm };
                WidthBox.Text = voulu.LargeurMm.ToString(CultureInfo.CurrentCulture);
                HeightBox.Text = voulu.HauteurMm.ToString(CultureInfo.CurrentCulture);
            }
        }

        _product.Name = NameBox.Text.Trim();
        if (_isNew) _product.Code = CodeBox.Text.Trim();
        _product.Price = parsed.Price;
        _product.WidthMm = parsed.Width;
        _product.HeightMm = parsed.Height;
        _product.Output = Sorties[OutputCombo.SelectedIndex].Valeur;
        _product.PrinterName = ImprimanteRequise ? (string)PrinterCombo.SelectedItem! : "";
        _product.DefaultFit = Cadrages[FitCombo.SelectedIndex].Valeur;
        _product.BorderMm = parsed.Border;
        _product.Dpi = parsed.Dpi;
        _product.PrintExposure = parsed.PrintExposure;
        // vide = Studio le déduit du rouleau ; null et non "" pour que le JSON reste lisible
        _product.MinilabPrintSizeName = MinilabSizeNameBox.Text.Trim() is { Length: > 0 } nomFormat
            ? nomFormat
            : null;
        _product.IccProfile = IccCombo.SelectedItem as string is { } icc && icc != NoIcc ? icc : null;
        _product.Enabled = EnabledCheck.IsChecked == true;
        // la fiche ne montre que trois des sept réglages de planche : les quatre autres
        // (écart, repères, contour, date) sont CONSERVÉS. Les recréer à neuf effaçait
        // l'horodatage exigé par l'administration sur les photos d'identité.
        if (SheetCheck.IsChecked == true)
        {
            var planche = _product.Sheet ?? new SheetSpec();
            planche.Copies = parsed.SheetCopies;
            planche.CellWidthMm = parsed.SheetW;
            planche.CellHeightMm = parsed.SheetH;
            _product.Sheet = planche;
        }
        else
        {
            _product.Sheet = null;
        }

        var saveError = _onSave(_product);
        if (saveError is not null)
        {
            ErrorText.Text = saveError;
            return;
        }
        Navigator.Back();
    }

    private sealed record Parsed(decimal Price, double Width, double Height, double Border,
        int Dpi, double PrintExposure, int SheetCopies, double SheetW, double SheetH);

    private string? Validate(out Parsed parsed)
    {
        parsed = new Parsed(0, 0, 0, 0, 300, 0, 6, 35, 45);

        if (string.IsNullOrWhiteSpace(NameBox.Text)) return "Le nom est obligatoire.";
        if (string.IsNullOrWhiteSpace(CodeBox.Text)) return "Le code est obligatoire.";
        if (!TryParseDecimal(PriceBox.Text, out var price) || price < 0) return "Prix invalide.";
        if (!TryParseDouble(WidthBox.Text, out var width) || width <= 0) return "Largeur invalide.";
        if (!TryParseDouble(HeightBox.Text, out var height) || height <= 0) return "Hauteur invalide.";
        if (OutputCombo.SelectedIndex < 0) return "Choisissez une sortie.";
        if (ImprimanteRequise && PrinterCombo.SelectedItem is null)
            return "Choisissez une imprimante — c'est une sortie « file d'impression Windows ».";
        if (!TryParseDouble(BorderBox.Text, out var border) || border < 0) return "Marge invalide.";
        if (!int.TryParse(DpiBox.Text, out var dpi) || dpi is < 72 or > 1200) return "Résolution invalide (72 à 1200 dpi).";

        // ±2 IL : au-delà, ce n'est plus une correction de machine mais une erreur de
        // saisie — un facteur quatre sur la lumière ne se rattrape pas sur du papier
        var exposition = 0.0;
        if (PrintExposureBox.Text.Trim().Length > 0
            && (!TryParseDouble(PrintExposureBox.Text, out exposition) || Math.Abs(exposition) > 2))
            return "Exposition à l'impression invalide (−2 à +2 diaphragmes, 0 = aucune correction).";

        int sheetCopies = 6;
        double sheetW = 35, sheetH = 45;
        if (SheetCheck.IsChecked == true)
        {
            if (!int.TryParse(SheetCopiesBox.Text, out sheetCopies) || sheetCopies is < 1 or > 24)
                return "Nombre de copies de planche invalide (1 à 24).";
            if (!TryParseDouble(SheetWBox.Text, out sheetW) || sheetW <= 0) return "Largeur de cellule invalide.";
            if (!TryParseDouble(SheetHBox.Text, out sheetH) || sheetH <= 0) return "Hauteur de cellule invalide.";
            if (sheetW >= width || sheetH >= height) return "La cellule doit être plus petite que le tirage.";

            // sans ce contrôle, une planche impossible s'enregistre et n'échoue qu'à l'impression,
            // devant le client (IdSheetLayout.Layout lève quand les copies ne tiennent pas)
            var capacity = IdSheetLayout.MaxCopies(
                MmPx.ToPixels(width, dpi), MmPx.ToPixels(height, dpi),
                MmPx.ToPixels(sheetW, dpi), MmPx.ToPixels(sheetH, dpi),
                MmPx.ToPixels(SheetSpec.DefaultGapMm, dpi));
            if (sheetCopies > capacity)
                return $"{sheetCopies} photos de {sheetW:0.#}×{sheetH:0.#} mm ne tiennent pas sur {width:0.#}×{height:0.#} mm " +
                       $"(maximum {capacity}).";
        }

        parsed = new Parsed(price, width, height, border, dpi, exposition, sheetCopies, sheetW, sheetH);
        return null;
    }

    // accepte la virgule française comme le point
    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
