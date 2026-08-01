using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;
using Studio.Sources;
using Studio.Store;

namespace Studio.App.Views;

/// <summary>
/// Photos d'identité : détection du visage → pré-cadrage 35×45 conforme,
/// gabarit surimprimé (vert quand conforme), ajustement manuel, impression
/// de la planche (produit à SheetSpec).
/// </summary>
public partial class IdPhotoView : UserControl
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly Brush NeutralBrush = Brushes.White;

    private readonly string _rootPath;
    private readonly List<StripItem> _photos = new();
    private CancellationTokenSource? _loadCts;
    private int _quantity = 1;
    private int _copies = 6;   // photos sur la planche ; recalé sur le produit à la sélection

    private StripItem? _current;
    private BitmapSource? _displayBitmap;
    private CropSpec _crop = CropSpec.Full;
    private NormRect? _head;

    private Point _dragLast;
    private bool _dragging;

    /// <summary>
    /// Document visé. Toute la géométrie en découle : format du tirage, bornes du visage,
    /// taille des ovales du gabarit. Un passeport espagnol fait 26 × 32 mm là où le
    /// français fait 35 × 45 — afficher le gabarit français sur l'un ou l'autre
    /// donnerait une planche refusée au guichet.
    /// </summary>
    private IdDocumentSpec _document = IdDocumentSpec.France;

    /// <summary>Repères posés par l'opérateur : sommet du crâne et bas du menton.</summary>
    private NormPoint? _crown;
    private NormPoint? _chin;
    private string? _markerDrag;   // "Crown", "Chin", ou null

    private readonly SmoothZoomDriver _smoothZoom;

    /// <param name="rootPath">Dossier des photos.</param>
    /// <param name="document">
    /// Norme visée. Null = norme française, le cas courant de la boutique.
    /// </param>
    public IdPhotoView(string rootPath, IdDocumentSpec? document = null)
    {
        _rootPath = rootPath;
        _document = document ?? IdDocumentSpec.France;
        _smoothZoom = new SmoothZoomDriver(Zoom);
        InitializeComponent();

        TitleText.Text = _document.Country == "France"
            ? $"Photo d'identité {_document.WidthMm:0.#}×{_document.HeightMm:0.#}"
            : $"{_document.Country} — {_document.Document} ({_document.WidthMm:0.#}×{_document.HeightMm:0.#} mm)";

        var sheetProducts = App.Services.Catalog.Enabled
            .Where(p => p.Sheet is not null)
            .Select(p => new ProductChoice(p))
            .ToList();
        ProductCombo.ItemsSource = sheetProducts;
        ProductCombo.SelectedIndex = 0;

        // sans produit « planche » actif, l'écran était muet : combo vide, bouton grisé,
        // aucune explication. On le dit à l'opérateur, qui peut activer le produit au Catalogue.
        if (sheetProducts.Count == 0)
            Loaded += (_, _) => MessageBox.Show(
                "Aucun produit « planche identité » n'est activé dans le catalogue.\n\n" +
                "Ouvrez Catalogue et activez un produit de type planche (ex. « Photos d'identité 35×45 ») " +
                "pour pouvoir imprimer des photos d'identité.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);

        Loaded += async (_, _) => await LoadStripAsync();
        Unloaded += (_, _) =>
        {
            _loadCts?.Cancel();
            _smoothZoom.Cancel();
        };
    }

    private sealed record ProductChoice(Product Product)
    {
        public string Label => $"{Product.Name} — {Product.Price:0.00} €";
    }

    private async Task LoadStripAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        if (_photos.Count == 0)
        {
            var files = await Task.Run(() => PhotoScanner.Scan(_rootPath), ct);
            foreach (var file in files)
                _photos.Add(new StripItem(file));
            PhotoStrip.ItemsSource = _photos;
        }

        var thumbnails = App.Services.Thumbnails;
        foreach (var photo in _photos)
        {
            if (ct.IsCancellationRequested) return;
            if (photo.Thumbnail is not null) continue;
            try
            {
                var bytes = await Task.Run(() => thumbnails.GetJpeg(photo.Path, 220), ct);
                photo.Thumbnail = ToBitmap(bytes);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
        }
    }

    private static BitmapImage ToBitmap(byte[] jpegBytes)
    {
        using var stream = new MemoryStream(jpegBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ----- choix de la photo -----

    private async void OnStripPhotoClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not StripItem item) return;

        foreach (var p in _photos) p.Selected = p == item;
        _current = item;
        EmptyText.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            var path = item.Path;
            var bytes = await Task.Run(() => App.Services.Thumbnails.GetJpeg(path, 1600));
            _displayBitmap = ToBitmap(bytes);
            ApplyGrayscalePreview();

            var face = await Task.Run(() => App.Services.Faces.DetectMain(path));
            var detecte = face is null ? null : IdPhotoFr.EstimateHead(face.Box);
            PoserReperes(detecte);
            AutoCrop();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Photo illisible : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _current = null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        PrintButton.IsEnabled = _current is not null;
        Redraw();
    }

    /// <summary>
    /// Place les deux anneaux : sur la tête détectée si elle l'a été, sinon à une position
    /// plausible que l'opérateur ajustera. Ils sont toujours proposés — la détection se
    /// trompe sur les cheveux volumineux, les couvre-chefs et les bébés, et c'est
    /// précisément là que le placement manuel sauve la photo.
    /// </summary>
    private void PoserReperes(NormRect? detecte)
    {
        if (detecte is { } tete)
        {
            _crown = new NormPoint(tete.CenterX, tete.Y);
            _chin = new NormPoint(tete.CenterX, tete.Bottom);
        }
        else
        {
            _crown = new NormPoint(0.5, 0.22);
            _chin = new NormPoint(0.5, 0.62);
        }
    }

    /// <summary>Recalcule la tête et le cadre à partir des deux repères.</summary>
    private void AutoCrop()
    {
        if (_displayBitmap is null) return;

        if (_crown is not null && _chin is not null)
        {
            try
            {
                _head = IdPhotoFr.HeadFromMarkers(_crown, _chin);
                _crop = IdPhotoFr.ComputeCrop(_head, _displayBitmap.PixelWidth, _displayBitmap.PixelHeight,
                    _document);
                return;
            }
            catch (ArgumentException)
            {
                // repères confondus : on garde le cadre précédent plutôt que de tout perdre
                return;
            }
        }

        _head = null;
        _crop = CropMath.CenterCrop(_displayBitmap.PixelWidth, _displayBitmap.PixelHeight, TargetAspect);
    }

    private void OnRedetect(object sender, RoutedEventArgs e)
    {
        if (_current is not null)
        {
            var face = App.Services.Faces.DetectMain(_current.Path);
            PoserReperes(face is null ? null : IdPhotoFr.EstimateHead(face.Box));
        }
        AutoCrop();
        Redraw();
    }

    // ----- anneaux de placement -----

    private void OnMarkerDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement anneau) return;

        _markerDrag = anneau.Tag as string;
        Stage.CaptureMouse();   // la souris peut sortir de l'anneau pendant le glissement
        e.Handled = true;       // ne pas déclencher le déplacement du cadre
    }

    /// <summary>Déplace l'anneau saisi ; renvoie vrai si le glissement a été consommé.</summary>
    private bool DeplacerRepere(Point positionStage)
    {
        if (_markerDrag is null) return false;

        var display = DisplayRect();
        if (display.IsEmpty || display.Width <= 0 || display.Height <= 0) return true;

        var point = new NormPoint(
            Math.Clamp((positionStage.X - display.X) / display.Width, 0, 1),
            Math.Clamp((positionStage.Y - display.Y) / display.Height, 0, 1));

        if (_markerDrag == "Crown") _crown = point;
        else _chin = point;

        AutoCrop();
        Redraw();
        return true;
    }

    private void PlacerAnneaux(Rect display)
    {
        var visible = _crown is not null && _chin is not null && !display.IsEmpty;
        var etat = visible ? Visibility.Visible : Visibility.Collapsed;
        CrownMarker.Visibility = ChinMarker.Visibility = etat;
        CrownLabel.Visibility = ChinLabel.Visibility = MarkerAxis.Visibility = etat;
        if (!visible) return;

        var crane = new Point(display.X + _crown!.X * display.Width, display.Y + _crown.Y * display.Height);
        var menton = new Point(display.X + _chin!.X * display.Width, display.Y + _chin.Y * display.Height);

        Centrer(CrownMarker, crane);
        Centrer(ChinMarker, menton);

        Canvas.SetLeft(CrownLabel, crane.X + 30);
        Canvas.SetTop(CrownLabel, crane.Y - 10);
        Canvas.SetLeft(ChinLabel, menton.X + 30);
        Canvas.SetTop(ChinLabel, menton.Y - 10);

        MarkerAxis.X1 = crane.X;
        MarkerAxis.Y1 = crane.Y;
        MarkerAxis.X2 = menton.X;
        MarkerAxis.Y2 = menton.Y;
    }

    private static void Centrer(FrameworkElement anneau, Point centre)
    {
        Canvas.SetLeft(anneau, centre.X - anneau.Width / 2);
        Canvas.SetTop(anneau, centre.Y - anneau.Height / 2);
    }

    private void OnGrayscaleChanged(object sender, RoutedEventArgs e) => ApplyGrayscalePreview();

    private void ApplyGrayscalePreview()
    {
        if (_displayBitmap is null) return;
        if (GrayscaleCheck.IsChecked == true)
        {
            var gray = new FormatConvertedBitmap(_displayBitmap, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            Photo.Source = gray;
        }
        else
        {
            Photo.Source = _displayBitmap;
        }
    }

    // ----- gabarit et dessin -----

    private Rect DisplayRect()
    {
        if (_displayBitmap is null || Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0)
            return Rect.Empty;
        var scale = Math.Min(Stage.ActualWidth / _displayBitmap.PixelWidth,
                             Stage.ActualHeight / _displayBitmap.PixelHeight);
        var w = _displayBitmap.PixelWidth * scale;
        var h = _displayBitmap.PixelHeight * scale;
        return new Rect((Stage.ActualWidth - w) / 2, (Stage.ActualHeight - h) / 2, w, h);
    }

    private void Redraw()
    {
        var display = DisplayRect();
        Overlay.Visibility = display.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (display.IsEmpty) return;

        var cropRect = new Rect(
            display.X + _crop.X * display.Width,
            display.Y + _crop.Y * display.Height,
            _crop.Width * display.Width,
            _crop.Height * display.Height);

        Shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(display), new RectangleGeometry(cropRect));
        Canvas.SetLeft(CropBorder, cropRect.X);
        Canvas.SetTop(CropBorder, cropRect.Y);
        CropBorder.Width = cropRect.Width;
        CropBorder.Height = cropRect.Height;

        // lignes du gabarit : crâne à 4 mm, menton entre 36 et 40 mm du bord haut
        // les lignes du gabarit suivent la norme du document visé, pas une norme figée
        var reperesVisibles = _document.HasHeadBounds;
        CrownLine.Visibility = ChinMinLine.Visibility = ChinMaxLine.Visibility =
            reperesVisibles ? Visibility.Visible : Visibility.Collapsed;

        if (reperesVisibles)
        {
            PlaceGuide(CrownLine, cropRect, _document.TargetCrownMarginMm);
            PlaceGuide(ChinMinLine, cropRect, _document.TargetCrownMarginMm + _document.HeadMinMm);
            PlaceGuide(ChinMaxLine, cropRect, _document.TargetCrownMarginMm + _document.HeadMaxMm);
        }

        PlacerGabaritVisage(cropRect);
        PlacerAnneaux(display);
        UpdateCompliance();
    }

    /// <summary>
    /// Trace le gabarit du visage : deux ovales concentriques, l'un à la taille minimale
    /// de tête admise, l'autre à la maximale, plus l'axe vertical.
    ///
    /// C'est le repère de DiLand : l'opérateur amène le tour de tête entre les deux
    /// ovales et le visage sur l'axe, sans avoir à lire une mesure en millimètres.
    /// </summary>
    private void PlacerGabaritVisage(Rect cropRect)
    {
        var centreX = cropRect.X + cropRect.Width / 2;

        FaceAxis.X1 = FaceAxis.X2 = centreX;
        FaceAxis.Y1 = cropRect.Y;
        FaceAxis.Y2 = cropRect.Bottom;

        // Sans bornes de visage, il n'y a pas de gabarit à montrer : une trentaine des
        // 274 documents n'en donnent aucune. Dessiner des ovales quand même laisserait
        // croire à une norme qui n'existe pas.
        if (!_document.HasHeadBounds)
        {
            HeadMinOval.Visibility = HeadMaxOval.Visibility = Visibility.Collapsed;
            return;
        }

        HeadMinOval.Visibility = HeadMaxOval.Visibility = Visibility.Visible;

        // centre de la tête visée, exprimé dans les cotes du document
        var centreY = cropRect.Y + cropRect.Height
            * (_document.TargetCrownMarginMm + _document.TargetHeadMm / 2) / _document.HeightMm;

        PlacerOvale(HeadMinOval, cropRect, centreX, centreY, _document.HeadMinMm);
        PlacerOvale(HeadMaxOval, cropRect, centreX, centreY, _document.HeadMaxMm);
    }

    /// <summary>Un ovale de gabarit, dimensionné dans les millimètres du document visé.</summary>
    private void PlacerOvale(System.Windows.Shapes.Ellipse ovale, Rect cropRect,
        double centreX, double centreY, double hauteurMm)
    {
        const double largeurSurHauteur = 0.75;   // proportion moyenne d'un visage

        var hauteur = cropRect.Height * hauteurMm / _document.HeightMm;
        var largeur = cropRect.Width * (hauteurMm * largeurSurHauteur) / _document.WidthMm;

        ovale.Width = largeur;
        ovale.Height = hauteur;
        Canvas.SetLeft(ovale, centreX - largeur / 2);
        Canvas.SetTop(ovale, centreY - hauteur / 2);
    }

    private void PlaceGuide(System.Windows.Shapes.Line line, Rect cropRect, double mmFromTop)
    {
        var y = cropRect.Y + cropRect.Height * mmFromTop / _document.HeightMm;
        line.X1 = cropRect.X;
        line.X2 = cropRect.Right;
        line.Y1 = line.Y2 = y;
    }

    private void UpdateCompliance()
    {
        if (_head is null)
        {
            SetGuideBrush(NeutralBrush);
            ComplianceText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            ComplianceText.Text = "Visage non détecté — cadrez à l'œil avec le gabarit.";
            return;
        }

        var c = IdPhotoFr.Check(_crop, _head, _document);

        // sans borne de visage dans la norme, on ne peut pas juger : le dire plutôt
        // que d'annoncer « conforme » sur un document qu'on ne sait pas contrôler
        if (!c.CanBeChecked)
        {
            SetGuideBrush(NeutralBrush);
            ComplianceText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            ComplianceText.Text =
                $"{_document.Country} — {_document.Document} : cette norme ne fixe pas de hauteur de visage, " +
                "conformité non vérifiable.";
            return;
        }

        SetGuideBrush(c.Compliant ? OkBrush : WarnBrush);
        ComplianceText.Foreground = c.Compliant
            ? (Brush)Application.Current.Resources["OkBrush"]
            : (Brush)Application.Current.Resources["DangerBrush"];

        if (c.Compliant)
        {
            ComplianceText.Text =
                $"Conforme ✓ — tête {c.HeadHeightMm:0.0} mm ({_document.HeadMinMm:0.#}–{_document.HeadMaxMm:0.#} mm)";
            return;
        }

        var issues = new List<string>();
        if (!c.HeadHeightOk)
            issues.Add(c.HeadHeightMm > _document.HeadMaxMm
                ? $"tête trop grande ({c.HeadHeightMm:0.0} mm) : reculez le zoom"
                : $"tête trop petite ({c.HeadHeightMm:0.0} mm) : zoomez");
        if (!c.CrownOk)
            issues.Add(c.CrownMarginMm < IdPhotoFr.CrownMarginMinMm
                ? "crâne trop près du bord haut : descendez le cadre"
                : "trop d'espace au-dessus du crâne : montez le cadre");
        if (!c.CenteredOk)
            issues.Add(c.CenterOffsetMm > 0 ? "décalé : glissez vers la droite" : "décalé : glissez vers la gauche");
        ComplianceText.Text = string.Join(" · ", issues);
    }

    private void SetGuideBrush(Brush brush)
    {
        CropBorder.Stroke = brush;
        CrownLine.Stroke = brush;
        ChinMinLine.Stroke = brush;
        ChinMaxLine.Stroke = brush;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // ----- interactions (mêmes gestes que l'éditeur de recadrage) -----

    /// <summary>Proportions du cadre : celles du document visé, pas un 35/45 figé.</summary>
    private double TargetAspect => _document.WidthMm / _document.HeightMm;

    private void Pan(double dxPx, double dyPx)
    {
        var display = DisplayRect();
        if (display.IsEmpty) return;
        _crop = CropMath.Pan(_crop, dxPx / display.Width, dyPx / display.Height);
        Redraw();
    }

    private void Zoom(double cropFactor)
    {
        if (_displayBitmap is null) return;
        _crop = CropMath.Zoom(_crop, cropFactor,
            _displayBitmap.PixelWidth, _displayBitmap.PixelHeight, TargetAspect);
        Redraw();
    }

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_current is null) return;
        _dragging = true;
        _dragLast = e.GetPosition(Stage);
        Stage.CaptureMouse();
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _markerDrag = null;
        Stage.ReleaseMouseCapture();
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        // un anneau saisi a la priorité sur le déplacement du cadre
        if (DeplacerRepere(e.GetPosition(Stage))) return;

        if (!_dragging) return;
        var pos = e.GetPosition(Stage);
        Pan(pos.X - _dragLast.X, pos.Y - _dragLast.Y);
        _dragLast = pos;
    }

    // Pas de zoom pour un cran de molette standard (Delta = 120), étalé sur ~150 ms
    // par le SmoothZoomDriver : franc au total, continu à l'écran.
    private const double WheelZoomStep = 1.10;

    private void OnStageWheel(object sender, MouseWheelEventArgs e) =>
        _smoothZoom.Add(Math.Pow(WheelZoomStep, -e.Delta / 120.0));

    private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = Stage;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        Pan(e.DeltaManipulation.Translation.X, e.DeltaManipulation.Translation.Y);
        var scale = e.DeltaManipulation.Scale.X;
        if (Math.Abs(scale - 1) > 0.001)
        {
            // Le pincement colle aux doigts : pas de lissage, et on solde le zoom molette en vol.
            _smoothZoom.Cancel();
            Zoom(1 / scale);
        }
        e.Handled = true;
    }

    private void OnQuantityMinus(object sender, RoutedEventArgs e) => SetQuantity(_quantity - 1);
    private void OnQuantityPlus(object sender, RoutedEventArgs e) => SetQuantity(_quantity + 1);

    private void SetQuantity(int value)
    {
        _quantity = Math.Clamp(value, 1, 20);
        QuantityText.Text = _quantity.ToString();
    }

    private void OnCopiesMinus(object sender, RoutedEventArgs e) => SetCopies(_copies - 1);
    private void OnCopiesPlus(object sender, RoutedEventArgs e) => SetCopies(_copies + 1);

    /// <summary>Nombre de photos sur la planche, borné par ce qui tient réellement sur le tirage.</summary>
    private void SetCopies(int value)
    {
        _copies = Math.Clamp(value, 1, Math.Max(1, MaxCopiesForSelectedProduct()));
        CopiesText.Text = _copies.ToString();
    }

    private int MaxCopiesForSelectedProduct()
    {
        if (ProductCombo.SelectedItem is not ProductChoice choice || choice.Product.Sheet is not { } sheet)
            return 1;

        var product = choice.Product;
        return IdSheetLayout.MaxCopies(
            MmPx.ToPixels(product.WidthMm, product.Dpi),
            MmPx.ToPixels(product.HeightMm, product.Dpi),
            MmPx.ToPixels(sheet.CellWidthMm, product.Dpi),
            MmPx.ToPixels(sheet.CellHeightMm, product.Dpi),
            MmPx.ToPixels(sheet.GapMm, product.Dpi));
    }

    /// <summary>Changer de produit repart de sa disposition par défaut (planche de 6, de 8…).</summary>
    private void OnProductChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductCombo.SelectedItem is not ProductChoice choice || choice.Product.Sheet is not { } sheet) return;
        SetCopies(sheet.Copies);
        ShowFinishes(choice.Product);
    }

    /// <summary>Le choix de finition n'apparaît que si le produit en propose (voir Catalogue → Finitions).</summary>
    private void ShowFinishes(Product product)
    {
        var names = product.Finishes.Select(f => f.Name).ToList();
        FinishCombo.ItemsSource = names;
        if (names.Count > 0) FinishCombo.SelectedIndex = 0;

        var visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FinishCombo.Visibility = visibility;
        FinishLabel.Visibility = visibility;
    }

    // ----- impression -----

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        // ne jamais sortir en silence : sans produit planche activé, le bouton semblait mort
        if (ProductCombo.SelectedItem is not ProductChoice choice)
        {
            MessageBox.Show(
                "Aucun produit « planche identité » n'est activé dans le catalogue.\n\n" +
                "Ouvrez Catalogue et activez un produit de type planche pour imprimer des photos d'identité.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_current is null)
        {
            MessageBox.Show("Choisissez d'abord une photo dans la bande du bas.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // avertit sans bloquer : l'opérateur reste juge (visage non détecté, photo médiocre…)
        if (_head is not null && !IdPhotoFr.Check(_crop, _head, _document).Compliant)
        {
            var answer = MessageBox.Show(
                "Le cadrage ne respecte pas le gabarit 35×45.\nImprimer quand même ?",
                "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        var services = App.Services;
        var adjustments = new ImageAdjustments { Grayscale = GrayscaleCheck.IsChecked == true };
        var items = new List<DraftItem>
        {
            new(_current.Path, choice.Product, _quantity, _crop, 0, 0, null, adjustments, _copies,
                FinishCombo.SelectedItem as string),
        };

        PrintButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var order = await Task.Run(() =>
            {
                var created = services.Orders.CreateOrder("Operateur", items);
                foreach (var envelope in created.Envelopes)
                    services.Printer.PrintEnvelope(created, envelope);
                return created;
            });

            Mouse.OverrideCursor = null;
            MessageBox.Show(
                $"Commande {order.DisplayNumber} envoyée à l'impression.\n" +
                $"{_quantity} planche(s) de {_copies} photo(s) — total {order.Total:0.00} €",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigator.Home(new HomeView(), "Studio Photo");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Échec de l'impression (planche identité)", ex);
            MessageBox.Show($"Échec de l'impression : {ex.Message}\n\n" +
                            "La commande est visible dans « Commandes du jour » pour réimpression.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            PrintButton.IsEnabled = true;
        }
    }

    private sealed class StripItem : ObservableObject
    {
        private ImageSource? _thumbnail;
        private bool _selected;

        public StripItem(string path) => Path = path;

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set => Set(ref _thumbnail, value);
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                OnPropertyChanged(nameof(BorderBrush));
            }
        }

        public Brush BorderBrush => Selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;
    }
}
