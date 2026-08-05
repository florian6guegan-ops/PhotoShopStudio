using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
///
/// <b>Rien de lourd n'est fait à l'ouverture.</b> Elle chargeait l'image entière en mémoire
/// dans son constructeur — 195 Mo pour un 50×70 à 300 ppp — puis la REDÉCODAIT à chaque
/// rafraîchissement de l'aperçu, c'est-à-dire à chaque clic. La fenêtre mettait plusieurs
/// secondes à s'ouvrir et les cases suivantes « ne marchaient qu'une fois » : les clics
/// tombaient pendant que le fil d'interface décodait. Désormais : l'en-tête du fichier seul à
/// l'ouverture, un aperçu réduit chargé en tâche de fond, et l'image pleine résolution
/// seulement au moment d'imprimer.
/// </summary>
public partial class LargeFormatPrintView : UserControl
{
    /// <summary>Largeur de décodage de l'aperçu : bien au-delà du cadre, sans commune mesure
    /// avec l'image d'origine.</summary>
    private const int ApercuPx = 1400;

    private readonly string _imagePath;
    private readonly string _catalogDir;
    private readonly Action _onDone;
    private readonly LargeFormatPrintSettings _settings = new();

    private readonly int _imageWidth;
    private readonly int _imageHeight;
    private readonly double _sourceDpi;
    private readonly byte[]? _documentProfile;

    /// <summary>Aperçu réduit, gelé, chargé une seule fois. Null tant qu'il arrive.</summary>
    private System.Windows.Media.ImageBrush? _apercu;

    private double _pageWidthMm = 210;
    private double _pageHeightMm = 297;
    private bool _loading = true;
    private bool _syncing;
    private bool _impressionEnCours;

    /// <summary>
    /// L'échelle saisie avant de cocher « Ajuster au support », pour la rendre au décochage.
    ///
    /// Sans elle, l'ajustement écrivait sa propre échelle (320 %…) dans le champ, et décocher
    /// reprenait cette valeur : la case ne revenait jamais en arrière, ce qui la faisait passer
    /// pour bloquée.
    /// </summary>
    private string _echelleAvantAjustement = "100";

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

        // en-tête du fichier UNIQUEMENT : définition, résolution, profil incorporé. Aucun
        // pixel n'est décodé ici, et c'est tout l'enjeu de l'ouverture immédiate. Le flux est
        // refermé aussitôt : rien ne doit rester ouvert sur un fichier qu'on va relire.
        using (var flux = File.OpenRead(imagePath))
        {
            var cadre = BitmapFrame.Create(flux,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            _imageWidth = cadre.PixelWidth;
            _imageHeight = cadre.PixelHeight;

            // Une image sans résolution déclarée est traitée à 300 ppp, la valeur de
            // l'atelier — ce que le code disait déjà sans le faire : GDI+ ne rend jamais 0,
            // il invente 96, et le test « > 1 » le laissait passer. Un fichier sans densité
            // partait donc à 96 ppp, c'est-à-dire trois fois trop grand, et débordait de la
            // feuille dès 100 %. Les rendus de l'atelier portent tous leur densité
            // (ImagePipeline la pose) : ce repli ne concerne que les fichiers du dehors.
            _sourceDpi = cadre.DpiX > 1 ? cadre.DpiX : 300;
            _documentProfile = LireProfilIncorpore(cadre);
        }

        TitleText.Text = title;
        DocProfileText.Text = _documentProfile is null
            ? "sRGB IEC61966-2.1 (présumé)"
            : "Profil ICC incorporé au fichier";
        _settings.DocumentProfileIcc = _documentProfile;

        LoadIccProfiles();

        Loaded += async (_, _) =>
        {
            await ChargerImprimantesAsync();
            _loading = false;
            UpdateAll();
            await ChargerApercuAsync();
        };
    }

    /// <summary>
    /// Le profil ICC du fichier, s'il en porte un. Lu depuis les métadonnées, sans décoder.
    /// </summary>
    private static byte[]? LireProfilIncorpore(BitmapFrame cadre)
    {
        try
        {
            var contexte = cadre.ColorContexts?.FirstOrDefault();
            if (contexte is null) return null;

            using var flux = contexte.OpenProfileStream();
            if (flux is null) return null;

            using var memoire = new MemoryStream();
            flux.CopyTo(memoire);
            var octets = memoire.ToArray();
            return octets.Length > 0 ? octets : null;
        }
        catch (Exception)
        {
            // profil illisible ou format qui n'en porte pas : on repart de sRGB
            return null;
        }
    }

    /// <summary>
    /// Liste des imprimantes et premier format papier, HORS du fil d'interface.
    ///
    /// <c>InstalledPrinters</c> comme <c>GetPageSizeMm</c> interrogent le spouleur, et une
    /// file réseau endormie prend plusieurs secondes à répondre. La fenêtre s'affiche avant.
    /// </summary>
    private async Task ChargerImprimantesAsync()
    {
        List<string> imprimantes;
        try
        {
            imprimantes = await Task.Run(() =>
                PrinterSettings.InstalledPrinters.Cast<string>().ToList());
        }
        catch (Exception ex)
        {
            FileLog.Write("Liste des imprimantes indisponible", ex);
            imprimantes = new List<string>();
        }

        foreach (var imprimante in imprimantes)
            PrinterCombo.Items.Add(imprimante);

        // La machine des agrandissements. Elle était cherchée sur « SC-P800 », c'est-à-dire
        // sur l'exemplaire de la boutique : une P700, une imagePROGRAF ou une DesignJet
        // n'était pas reconnue, et l'écran s'ouvrait sur la première file venue — le
        // télécopieur, dans l'ordre alphabétique. On reconnaît maintenant par FAMILLE, et
        // le réglage du poste l'emporte s'il est renseigné.
        var choisie = DetectionImprimantes.Choisir(
            RoleImprimante.GrandFormat, App.Services.Poste.ImprimanteGrandFormat);

        PrinterCombo.SelectedItem = choisie ?? imprimantes.FirstOrDefault();
        _settings.PrinterName = PrinterCombo.SelectedItem as string ?? "";

        await RefreshPageSizeAsync();
    }

    /// <summary>
    /// Charge l'aperçu en tâche de fond, à la taille de l'écran et non à celle du tirage.
    ///
    /// Un seul décodage, une seule fois, réutilisé par tous les redessins : c'est le
    /// remplacement du <c>new BitmapImage(new Uri(...))</c> que <see cref="DrawPreview"/>
    /// refaisait à chaque appel.
    /// </summary>
    private async Task ChargerApercuAsync()
    {
        try
        {
            var image = await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_imagePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Math.Min(ApercuPx, Math.Max(1, _imageWidth));
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            });

            var brosse = new System.Windows.Media.ImageBrush(image)
            {
                Stretch = System.Windows.Media.Stretch.Fill,
            };
            brosse.Freeze();
            _apercu = brosse;

            UpdateAll();
        }
        catch (Exception ex)
        {
            // l'aperçu n'est qu'un aperçu : sans lui, la boîte reste utilisable
            FileLog.Write($"Aperçu de l'agrandissement illisible : {_imagePath}", ex);
        }
    }

    private void LoadIccProfiles()
    {
        PrinterProfileCombo.Items.Add(AucunProfil);
        foreach (var profil in IccProfiles.Available(_catalogDir))
            PrinterProfileCombo.Items.Add(profil);

        PrinterProfileCombo.SelectedIndex = 0;
    }

    private const string AucunProfil = "(aucun)";

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

        // le CHEMIN, pas le nom affiché : c'est lui qu'IccTransform ouvre
        _settings.PrinterProfile = PrinterProfileCombo.SelectedItem is IccProfiles.Entry profil
            ? profil.Path
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

        // les deux cases s'excluent : elles répondent à l'opposé à la même question, et
        // OnFitChanged/OnFillChanged décochent déjà l'autre. La lecture le refait pour que
        // l'état des réglages ne dépende pas de l'ordre des événements.
        _settings.Scaling =
            FillCheck.IsChecked == true ? MediaScaling.FillMedia
            : FitCheck.IsChecked == true ? MediaScaling.FitToMedia
            : MediaScaling.Manual;

        _settings.ScalePercent = Math.Max(0.01, ParseDouble(ScaleBox.Text, 100));
        _settings.TopMm = PrintLayout.ToMm(ParseDouble(TopBox.Text, 0), _settings.Units);
        _settings.LeftMm = PrintLayout.ToMm(ParseDouble(LeftBox.Text, 0), _settings.Units);
        _settings.CutBorder = CutBorderCheck.IsChecked == true;
    }

    /// <summary>
    /// Interroge le pilote pour la taille de feuille, hors du fil d'interface.
    /// </summary>
    private async Task RefreshPageSizeAsync()
    {
        if (PrinterCombo.SelectedItem is not string imprimante || string.IsNullOrEmpty(imprimante))
            return;

        var devMode = _settings.DevModeBytes;
        var paysage = OrientationCombo.SelectedIndex == 1;

        try
        {
            var (w, h) = await Task.Run(() =>
                LargeFormatPrinter.GetPageSizeMm(imprimante, devMode, paysage));
            _pageWidthMm = w;
            _pageHeightMm = h;
        }
        catch (InvalidOperationException)
        {
            // imprimante hors ligne : on garde la dernière taille connue plutôt que de planter
        }
        catch (Exception ex)
        {
            FileLog.Write($"Format papier indisponible pour « {imprimante} »", ex);
        }
    }

    // — rafraîchissement —

    private void UpdateAll()
    {
        if (_loading) return;

        ReadSettingsFromForm();
        var placement = _settings.ComputePlacement(_imageWidth, _imageHeight, _sourceDpi,
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

        // En remplissage, déborder est le but : ce qui dépasse est coupé net au bord du
        // papier, et l'aperçu le montre ainsi. On annonce donc ce qui sera perdu — un
        // chiffre sur lequel l'opérateur peut décider — au lieu d'un avertissement qui
        // demanderait de corriger ce qu'il vient de choisir.
        if (placement.OverflowsPaper(_pageWidthMm, _pageHeightMm))
        {
            var coupe = placement.CroppedShare(_pageWidthMm, _pageHeightMm);
            problems.Add(_settings.Scaling == MediaScaling.FillMedia
                ? $"Remplissage : {coupe:P0} de la photo sort de la feuille et sera coupé aux bords."
                : $"Le tirage déborde de la feuille et sera coupé ({coupe:P0} perdu) : réduisez " +
                  "l'échelle, ou cochez « Ajuster au support » pour tout garder, ou « Remplir " +
                  "le support » pour un plein papier cadré.");
        }

        if (placement.EffectiveDpi < 150)
            problems.Add($"Résolution faible ({placement.EffectiveDpi:0} PPP) : le tirage risque d'être flou.");

        WarningText.Text = string.Join("\n", problems);
        PrintButton.IsEnabled = !_impressionEnCours && _settings.Validate().Count == 0;
    }

    /// <summary>Reflète l'échelle dans les champs Largeur/Hauteur, sans boucler sur les événements.</summary>
    private void SyncSizeBoxes(PrintPlacement placement)
    {
        _syncing = true;
        WidthBox.Text = PrintLayout.FromMm(placement.WidthMm, _settings.Units).ToString("0.##", CultureInfo.CurrentCulture);
        HeightBox.Text = PrintLayout.FromMm(placement.HeightMm, _settings.Units).ToString("0.##", CultureInfo.CurrentCulture);
        if (_settings.Scaling != MediaScaling.Manual)
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

        // Le tirage est dessiné DANS la feuille, et rogné par elle.
        //
        // Il était posé sur le canevas, qui est plus grand : une photo trop grande pour le
        // papier s'affichait donc à cheval, débordant sur le fond de l'écran — l'inverse de
        // ce qui sort de l'imprimante, qui ne peut rien poser hors du papier. On voyait
        // « la feuille dans la photo » là où le tirage montre « la photo dans la feuille »
        // (signalé le 04/08/2026). Le pilote reçoit le même rognage, voir
        // <c>LargeFormatPrinter.Imprimer</c>.
        var feuille = new Canvas
        {
            Width = pageW,
            Height = pageH,
            Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, pageW, pageH)),
        };
        Canvas.SetLeft(feuille, originX);
        Canvas.SetTop(feuille, originY);
        PreviewCanvas.Children.Add(feuille);

        var photo = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, placement.WidthMm * scale),
            Height = Math.Max(1, placement.HeightMm * scale),
            // tant que l'aperçu réduit n'est pas là, le tirage est figuré en gris : la boîte
            // reste réglable sans attendre le décodage
            Fill = (System.Windows.Media.Brush?)_apercu
                   ?? new System.Windows.Media.SolidColorBrush(
                       System.Windows.Media.Color.FromRgb(0xBB, 0xBB, 0xBB)),
            // le contour de découpe se voit dans l'aperçu : noir franc et plus marqué que le
            // simple liseré qui délimite la photo, pour que l'opérateur sache ce qui sortira
            Stroke = _settings.CutBorder
                ? System.Windows.Media.Brushes.Black
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)),
            StrokeThickness = _settings.CutBorder ? 2 : 1,
        };
        // coordonnées RELATIVES à la feuille, qui porte désormais le rognage
        Canvas.SetLeft(photo, placement.LeftMm * scale);
        Canvas.SetTop(photo, placement.TopMm * scale);
        feuille.Children.Add(photo);

        // Ce qui est coupé se voit quand même : un liseré tireté dessine le tirage ENTIER,
        // par-dessus le canevas et non dans la feuille. Sans lui, un débordement subi ne se
        // lirait plus que dans le texte d'avertissement, et l'opérateur ne saurait pas de
        // quel côté ça sort.
        if (!placement.OverflowsPaper(_pageWidthMm, _pageHeightMm)) return;

        var entier = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(1, placement.WidthMm * scale),
            Height = Math.Max(1, placement.HeightMm * scale),
            Stroke = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0x8A, 0x2E)),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
        };
        Canvas.SetLeft(entier, originX + placement.LeftMm * scale);
        Canvas.SetTop(entier, originY + placement.TopMm * scale);
        PreviewCanvas.Children.Add(entier);
    }

    // — événements —

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAll();

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        UpdateAll();
    }

    /// <summary>
    /// Portrait ou paysage : il faut REDEMANDER la taille de feuille au pilote.
    ///
    /// La combo était câblée sur <see cref="OnSettingChanged"/>, qui ne fait que recalculer le
    /// placement : <c>_pageWidthMm</c> et <c>_pageHeightMm</c> ne permutaient jamais, et
    /// changer de disposition ne bougeait ni l'aperçu ni le centrage.
    /// </summary>
    private async void OnOrientationChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        await RefreshPageSizeAsync();
        UpdateAll();
    }

    private async void OnPrinterChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.DevModeBytes = null; // les réglages pilote ne valent que pour une machine
        await RefreshPageSizeAsync();
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

    /// <summary>« Ajuster au support » : une vraie bascule, aller ET retour.</summary>
    private void OnFitChanged(object sender, RoutedEventArgs e)
    {
        // les deux cases s'excluent : ajuster laisse du blanc, remplir coupe les bords, et
        // les cocher ensemble ne veut rien dire
        if (FitCheck.IsChecked == true) FillCheck.IsChecked = false;
        CalageChange();
    }

    /// <summary>« Remplir le support » : la bascule opposée. Voir <see cref="OnFitChanged"/>.</summary>
    private void OnFillChanged(object sender, RoutedEventArgs e)
    {
        if (FillCheck.IsChecked == true) FitCheck.IsChecked = false;
        CalageChange();
    }

    /// <summary>Vrai quand l'échelle est CALCULÉE — ajustement ou remplissage — et non saisie.</summary>
    private bool CalageAutomatique => FitCheck.IsChecked == true || FillCheck.IsChecked == true;

    /// <summary>
    /// Suites communes aux deux bascules : l'échelle de l'opérateur est mise de côté tant
    /// que la feuille la commande, et lui revient dès qu'il reprend la main.
    /// </summary>
    private void CalageChange()
    {
        var automatique = CalageAutomatique;

        if (automatique)
        {
            // on met de côté l'échelle de l'opérateur AVANT que le calage n'écrive la sienne.
            // Rien à sauvegarder si l'on passe d'une bascule à l'autre : ce qui est dans la
            // case est alors l'échelle CALCULÉE, et l'écraser perdrait la sienne pour de bon.
            if (!_calageAutomatiqueActif) _echelleAvantAjustement = ScaleBox.Text;
        }
        else
        {
            _syncing = true;
            ScaleBox.Text = _echelleAvantAjustement;
            _syncing = false;
        }

        _calageAutomatiqueActif = automatique;

        ScaleBox.IsEnabled = !automatique;
        WidthBox.IsEnabled = !automatique;
        HeightBox.IsEnabled = !automatique;
        UpdateAll();
    }

    /// <summary>L'état du calage au dernier passage, pour ne sauvegarder l'échelle qu'une fois.</summary>
    private bool _calageAutomatiqueActif;

    private void OnUnitsChanged(object sender, RoutedEventArgs e) => UpdateAll();

    private void OnScaleTyped(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        UpdateAll();
    }

    private void OnWidthTyped(object sender, RoutedEventArgs e)
    {
        if (_syncing || _loading || CalageAutomatique) return;
        var voulueMm = PrintLayout.ToMm(ParseDouble(WidthBox.Text, 0), SelectedUnits);
        if (voulueMm <= 0) return;

        _syncing = true;
        ScaleBox.Text = PrintLayout.ScaleForWidth(_imageWidth, _sourceDpi, voulueMm)
            .ToString("0.####", CultureInfo.CurrentCulture);
        _syncing = false;
        UpdateAll();
    }

    private void OnHeightTyped(object sender, RoutedEventArgs e)
    {
        if (_syncing || _loading || CalageAutomatique) return;
        var voulueMm = PrintLayout.ToMm(ParseDouble(HeightBox.Text, 0), SelectedUnits);
        if (voulueMm <= 0) return;

        _syncing = true;
        ScaleBox.Text = PrintLayout.ScaleForHeight(_imageHeight, _sourceDpi, voulueMm)
            .ToString("0.####", CultureInfo.CurrentCulture);
        _syncing = false;
        UpdateAll();
    }

    /// <summary>Ouvre la boîte du pilote Epson — la vraie, celle de Windows.</summary>
    private async void OnOpenDriverDialog(object sender, RoutedEventArgs e)
    {
        if (PrinterCombo.SelectedItem is not string printer || string.IsNullOrEmpty(printer))
            return;

        try
        {
            var devMode = DevMode.ShowDriverDialog(printer, _settings.DevModeBytes);
            if (devMode is not null)
            {
                _settings.DevModeBytes = devMode;
                await RefreshPageSizeAsync();
                UpdateAll();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir les réglages du pilote :\n{ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// L'image pleine résolution n'est lue qu'ICI, une fois, au moment d'imprimer — et sur un
    /// fil de fond, l'interface restant vivante.
    /// </summary>
    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (_impressionEnCours) return;

        ReadSettingsFromForm();

        _impressionEnCours = true;
        PrintButton.IsEnabled = false;
        var libelle = PrintButton.Content;
        PrintButton.Content = "Lecture du fichier…";

        try
        {
            var reglages = _settings.Clone();
            var dpi = _sourceDpi;
            var chemin = _imagePath;

            // Le bouton dit à quoi le temps passe. Sur un 50×70, la lecture du PNG et la
            // remise au spouleur pèsent chacune plusieurs secondes : sans ces libellés, la
            // fenêtre paraît figée et l'opérateur reclique.
            var etape = new Progress<string>(texte => PrintButton.Content = texte);

            await Task.Run(() =>
            {
                // le décodage est chronométré à part : sur un 50×70 à 300 ppp, le PNG fait
                // 48 Mpx et c'est peut-être là que le temps passe. Sans cette ligne, la
                // durée se confondait avec celle de l'impression.
                var chrono = System.Diagnostics.Stopwatch.StartNew();
                using var image = new Bitmap(chemin);
                FileLog.Write(
                    $"Agrandissement : décodage de {Path.GetFileName(chemin)} " +
                    $"({image.Width}×{image.Height} px) en {chrono.ElapsedMilliseconds} ms");

                ((IProgress<string>)etape).Report("Envoi à l'imprimante…");

                chrono.Restart();
                LargeFormatPrinter.Print(image, reglages, dpi,
                    $"Studio Photo — {Path.GetFileNameWithoutExtension(chemin)}");
                FileLog.Write($"Agrandissement : impression complète en {chrono.ElapsedMilliseconds} ms");
            });

            MessageBox.Show("Tirage envoyé à l'imprimante.", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"Impression grand format échouée : {_imagePath}", ex);
            MessageBox.Show($"L'impression a échoué :\n{ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _impressionEnCours = false;
            PrintButton.Content = libelle;
            UpdateAll();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Close() => _onDone();
}
