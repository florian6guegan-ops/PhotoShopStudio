using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Printing;
using Studio.Printing.LargeFormat;

namespace Studio.App.Views;

/// <summary>
/// Boîte d'impression des agrandissements, calquée sur celle de Photoshop : aperçu de la
/// feuille, réglages du pilote, gestion des couleurs, échelle et position.
///
/// Le bouton « Paramètres d'impression… » ouvre la boîte du pilote Epson elle-même —
/// c'est bien la fenêtre d'origine, pas une imitation.
/// </summary>
public partial class LargeFormatPrintView : UserControl
{
    private readonly string _imagePath;
    private readonly string _catalogDir;
    private readonly Action _onDone;
    private readonly LargeFormatPrintSettings _settings = new();
    private readonly Bitmap _image;
    private readonly double _sourceDpi;

    private double _pageWidthMm = 210;
    private double _pageHeightMm = 297;
    private bool _loading = true;
    private bool _syncing;

    /// <param name="imagePath">Fichier rendu à imprimer.</param>
    /// <param name="catalogDir">Dossier catalog/, pour la liste des profils ICC.</param>
    /// <param name="title">Libellé affiché en tête (produit et commande).</param>
    /// <param name="onDone">Appelé à la fermeture, que l'on ait imprimé ou annulé.</param>
    public LargeFormatPrintView(string imagePath, string catalogDir, string title, Action onDone)
    {
        InitializeComponent();

        _imagePath = imagePath;
        _catalogDir = catalogDir;
        _onDone = onDone;

        _image = new Bitmap(imagePath);
        // une image sans résolution déclarée est traitée à 300 ppp, la valeur de l'atelier
        _sourceDpi = _image.HorizontalResolution > 1 ? _image.HorizontalResolution : 300;

        TitleText.Text = title;
        DocProfileText.Text = DescribeDocumentProfile();

        LoadPrinters();
        LoadIccProfiles();

        _loading = false;
        RefreshPageSize();
        UpdateAll();
    }

    private string DescribeDocumentProfile()
    {
        // 0x8773 = ICC profile embarqué dans le fichier
        var hasEmbedded = Array.IndexOf(_image.PropertyIdList, 0x8773) >= 0;
        return hasEmbedded ? "Profil ICC incorporé au fichier" : "sRGB IEC61966-2.1 (présumé)";
    }

    private void LoadPrinters()
    {
        foreach (string printer in PrinterSettings.InstalledPrinters)
            PrinterCombo.Items.Add(printer);

        // l'Epson est la machine des agrandissements : on la présélectionne si elle est là
        var epson = PrinterCombo.Items.Cast<string>()
            .FirstOrDefault(p => p.Contains("SC-P800", StringComparison.OrdinalIgnoreCase));

        PrinterCombo.SelectedItem = epson ?? PrinterCombo.Items.Cast<string>().FirstOrDefault();
    }

    private void LoadIccProfiles()
    {
        PrinterProfileCombo.Items.Add("(aucun)");
        foreach (var profile in IccProfiles.List(_catalogDir))
            PrinterProfileCombo.Items.Add(profile);
        PrinterProfileCombo.SelectedIndex = 0;
    }

    // — lecture des champs —

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            ? v
            : fallback;

    private PrintUnits SelectedUnits => UnitsCombo.SelectedIndex switch
    {
        1 => PrintUnits.Millimeters,
        2 => PrintUnits.Inches,
        _ => PrintUnits.Centimeters,
    };

    private void ReadSettingsFromForm()
    {
        _settings.PrinterName = PrinterCombo.SelectedItem as string ?? "";
        _settings.Copies = (int)Math.Max(1, ParseDouble(CopiesBox.Text, 1));
        _settings.Landscape = OrientationCombo.SelectedIndex == 1;

        _settings.ColorHandling = ColorHandlingCombo.SelectedIndex == 1
            ? ColorHandling.ApplicationManagesColor
            : ColorHandling.PrinterManagesColor;

        _settings.PrinterProfile = PrinterProfileCombo.SelectedIndex > 0
            ? PrinterProfileCombo.SelectedItem as string
            : null;

        _settings.RenderingIntent = IntentCombo.SelectedIndex switch
        {
            1 => RenderingIntent.Perceptual,
            2 => RenderingIntent.Saturation,
            3 => RenderingIntent.AbsoluteColorimetric,
            _ => RenderingIntent.RelativeColorimetric,
        };
        _settings.BlackPointCompensation = BlackPointCheck.IsChecked == true;

        _settings.Units = SelectedUnits;
        _settings.Center = CenterCheck.IsChecked == true;
        _settings.FitToMedia = FitCheck.IsChecked == true;
        _settings.ScalePercent = Math.Max(0.01, ParseDouble(ScaleBox.Text, 100));
        _settings.TopMm = PrintLayout.ToMm(ParseDouble(TopBox.Text, 0), _settings.Units);
        _settings.LeftMm = PrintLayout.ToMm(ParseDouble(LeftBox.Text, 0), _settings.Units);
    }

    private void RefreshPageSize()
    {
        if (PrinterCombo.SelectedItem is not string printer || string.IsNullOrEmpty(printer)) return;
        try
        {
            var (w, h) = LargeFormatPrinter.GetPageSizeMm(printer, _settings.DevModeBytes,
                OrientationCombo.SelectedIndex == 1);
            _pageWidthMm = w;
            _pageHeightMm = h;
        }
        catch (InvalidOperationException)
        {
            // imprimante hors ligne : on garde la dernière taille connue plutôt que de planter
        }
    }

    // — rafraîchissement —

    private void UpdateAll()
    {
        if (_loading) return;

        ReadSettingsFromForm();
        var placement = _settings.ComputePlacement(_image.Width, _image.Height, _sourceDpi,
            _pageWidthMm, _pageHeightMm);

        SyncSizeBoxes(placement);
        DrawPreview(placement);

        var u = PrintLayout.UnitSuffix(_settings.Units);
        SizeText.Text =
            $"Feuille {PrintLayout.FromMm(_pageWidthMm, _settings.Units):0.##} × " +
            $"{PrintLayout.FromMm(_pageHeightMm, _settings.Units):0.##} {u}    •    " +
            $"Tirage {PrintLayout.FromMm(placement.WidthMm, _settings.Units):0.##} × " +
            $"{PrintLayout.FromMm(placement.HeightMm, _settings.Units):0.##} {u}";

        ResolutionText.Text = $"Résolution d'impr. : {placement.EffectiveDpi:0} PPP";

        var problems = _settings.Validate().ToList();
        if (placement.OverflowsPaper(_pageWidthMm, _pageHeightMm))
            problems.Add("Le tirage déborde de la feuille : réduisez l'échelle ou cochez « Ajuster au support ».");
        if (placement.EffectiveDpi < 150)
            problems.Add($"Résolution faible ({placement.EffectiveDpi:0} PPP) : le tirage risque d'être flou.");

        WarningText.Text = string.Join("\n", problems);
        PrintButton.IsEnabled = _settings.Validate().Count == 0;
    }

    /// <summary>Reflète l'échelle dans les champs Largeur/Hauteur, sans boucler sur les événements.</summary>
    private void SyncSizeBoxes(PrintPlacement placement)
    {
        _syncing = true;
        WidthBox.Text = PrintLayout.FromMm(placement.WidthMm, _settings.Units).ToString("0.##", CultureInfo.CurrentCulture);
        HeightBox.Text = PrintLayout.FromMm(placement.HeightMm, _settings.Units).ToString("0.##", CultureInfo.CurrentCulture);
        if (_settings.FitToMedia)
            ScaleBox.Text = placement.ScalePercent.ToString("0.##", CultureInfo.CurrentCulture);
        _syncing = false;
    }

    private void DrawPreview(PrintPlacement placement)
    {
        PreviewCanvas.Children.Clear();
        var availW = PreviewCanvas.ActualWidth;
        var availH = PreviewCanvas.ActualHeight;
        if (availW < 10 || availH < 10) return;

        var scale = Math.Min(availW / _pageWidthMm, availH / _pageHeightMm);
        var pageW = _pageWidthMm * scale;
        var pageH = _pageHeightMm * scale;
        var originX = (availW - pageW) / 2;
        var originY = (availH - pageH) / 2;

        var sheet = new System.Windows.Shapes.Rectangle
        {
            Width = pageW,
            Height = pageH,
            Fill = System.Windows.Media.Brushes.White,
            Stroke = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(sheet, originX);
        Canvas.SetTop(sheet, originY);
        PreviewCanvas.Children.Add(sheet);

        var photo = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, placement.WidthMm * scale),
            Height = Math.Max(1, placement.HeightMm * scale),
            Fill = new System.Windows.Media.ImageBrush(
                new System.Windows.Media.Imaging.BitmapImage(new Uri(_imagePath)))
            {
                Stretch = System.Windows.Media.Stretch.Fill,
            },
            Stroke = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(photo, originX + placement.LeftMm * scale);
        Canvas.SetTop(photo, originY + placement.TopMm * scale);
        PreviewCanvas.Children.Add(photo);
    }

    // — événements —

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAll();

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        UpdateAll();
    }

    private void OnPrinterChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.DevModeBytes = null; // les réglages pilote ne valent que pour une machine
        RefreshPageSize();
        UpdateAll();
    }

    private void OnColorHandlingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var studioGere = ColorHandlingCombo.SelectedIndex == 1;
        PrinterProfileCombo.IsEnabled = studioGere;
        IntentCombo.IsEnabled = studioGere;
        BlackPointCheck.IsEnabled = studioGere;
        UpdateAll();
    }

    private void OnCenterChanged(object sender, RoutedEventArgs e)
    {
        var centre = CenterCheck.IsChecked == true;
        TopBox.IsEnabled = !centre;
        LeftBox.IsEnabled = !centre;
        UpdateAll();
    }

    private void OnFitChanged(object sender, RoutedEventArgs e)
    {
        ScaleBox.IsEnabled = FitCheck.IsChecked != true;
        WidthBox.IsEnabled = FitCheck.IsChecked != true;
        HeightBox.IsEnabled = FitCheck.IsChecked != true;
        UpdateAll();
    }

    private void OnUnitsChanged(object sender, RoutedEventArgs e) => UpdateAll();

    private void OnScaleTyped(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        UpdateAll();
    }

    private void OnWidthTyped(object sender, RoutedEventArgs e)
    {
        if (_syncing || _loading || FitCheck.IsChecked == true) return;
        var voulueMm = PrintLayout.ToMm(ParseDouble(WidthBox.Text, 0), SelectedUnits);
        if (voulueMm <= 0) return;

        _syncing = true;
        ScaleBox.Text = PrintLayout.ScaleForWidth(_image.Width, _sourceDpi, voulueMm)
            .ToString("0.####", CultureInfo.CurrentCulture);
        _syncing = false;
        UpdateAll();
    }

    private void OnHeightTyped(object sender, RoutedEventArgs e)
    {
        if (_syncing || _loading || FitCheck.IsChecked == true) return;
        var voulueMm = PrintLayout.ToMm(ParseDouble(HeightBox.Text, 0), SelectedUnits);
        if (voulueMm <= 0) return;

        _syncing = true;
        ScaleBox.Text = PrintLayout.ScaleForHeight(_image.Height, _sourceDpi, voulueMm)
            .ToString("0.####", CultureInfo.CurrentCulture);
        _syncing = false;
        UpdateAll();
    }

    /// <summary>Ouvre la boîte du pilote Epson — la vraie, celle de Windows.</summary>
    private void OnOpenDriverDialog(object sender, RoutedEventArgs e)
    {
        if (PrinterCombo.SelectedItem is not string printer || string.IsNullOrEmpty(printer))
            return;

        try
        {
            var devMode = DevMode.ShowDriverDialog(printer, _settings.DevModeBytes);
            if (devMode is not null)
            {
                _settings.DevModeBytes = devMode;
                RefreshPageSize();
                UpdateAll();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir les réglages du pilote :\n{ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromForm();
        try
        {
            LargeFormatPrinter.Print(_image, _settings, _sourceDpi,
                $"Studio Photo — {Path.GetFileNameWithoutExtension(_imagePath)}");
            MessageBox.Show("Tirage envoyé à l'imprimante.", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"L'impression a échoué :\n{ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        _image.Dispose();
        _onDone();
    }
}
