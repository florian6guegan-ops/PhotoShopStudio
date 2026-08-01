using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.App.Views;

/// <summary>
/// Éditeur de recadrage : cadre au ratio du produit, glisser pour déplacer,
/// molette/pincement/boutons pour zoomer, rotation par quarts de tour,
/// bascule Remplir/Entier. Ne touche jamais au fichier : ne produit qu'un CropSpec.
/// </summary>
public partial class CropEditorView : UserControl
{
    /// <summary>Résultat de l'édition, appliqué au panier par l'appelant.</summary>
    public sealed record State(CropSpec Crop, int RotationQuarterTurns, FitMode Fit);

    private readonly string _photoPath;
    private readonly Product _product;
    private readonly Action<State> _onApply;

    private BitmapSource? _sourceBitmap;   // vignette grande taille, orientée EXIF
    private BitmapSource? _displayBitmap;  // + rotation utilisateur
    private CropSpec _crop;
    private int _turns;
    private FitMode _fit;

    private Point _dragLast;
    private bool _dragging;

    private readonly SmoothZoomDriver _smoothZoom;

    public CropEditorView(string photoPath, Product product, State initial, Action<State> onApply)
    {
        _photoPath = photoPath;
        _product = product;
        _onApply = onApply;
        _crop = initial.Crop;
        _turns = initial.RotationQuarterTurns;
        _fit = initial.Fit;
        _smoothZoom = new SmoothZoomDriver(Zoom);

        InitializeComponent();
        TitleText.Text = $"Recadrage — {product.Name}";
        UpdateFitToggle();
        UpdateFrameToggle();

        AttachShortcuts();

        Loaded += async (_, _) =>
        {
            await LoadPhotoAsync();
            // sans le focus, les flèches et Ctrl+I n'arriveraient jamais jusqu'ici
            Focus();
        };
        Unloaded += (_, _) => _smoothZoom.Cancel();
    }

    /// <summary>
    /// Les touches de DiLand, à l'identique — un opérateur qui a dix ans de DiLand dans
    /// les doigts ne doit rien réapprendre. Relevées dans son <c>EditPhotosView</c>.
    /// </summary>
    private void AttachShortcuts()
    {
        Focusable = true;

        new KeyMap()
            .On(Key.R, () => Reset())
            .On(Key.Z, () => _smoothZoom.Add(1 / 1.25))
            .On([Key.Add, Key.OemPlus], () => _smoothZoom.Add(1 / 1.25))
            .On([Key.Subtract, Key.OemMinus], () => _smoothZoom.Add(1.25))
            .On(Key.C, ToggleFit)
            .On(Key.T, ToggleFrame)
            .On(Key.Escape, Navigator.Back)
            .OnCtrl(Key.I, Apply)
            .OnCtrl(Key.P, Navigator.Back)
            .OnCtrl(Key.Left, () => Rotate(-1))
            .OnCtrl(Key.Right, () => Rotate(1))
            .Attach(this);
    }

    /// <summary>
    /// Aspect pixel du cadre : celui du produit, orienté comme la photo par défaut
    /// (cf. OrientCanvas au rendu), ou selon le choix de l'opérateur s'il l'a forcé.
    ///
    /// Faire tenir une photo verticale dans un tirage horizontal est un besoin courant :
    /// pivoter la photo ne le permet pas, il faut pivoter le CADRE.
    /// </summary>
    private double TargetAspect
    {
        get
        {
            var aspect = _product.WidthMm / _product.HeightMm;

            bool cadrePaysage;
            if (_frameLandscape is { } choisi)
            {
                cadrePaysage = choisi;
            }
            else
            {
                if (_displayBitmap is null) return aspect;
                cadrePaysage = _displayBitmap.PixelWidth > _displayBitmap.PixelHeight;
            }

            return cadrePaysage == aspect > 1 ? aspect : 1 / aspect;
        }
    }

    /// <summary>Orientation du cadre imposée par l'opérateur ; null = suit la photo.</summary>
    private bool? _frameLandscape;

    private async Task LoadPhotoAsync()
    {
        try
        {
            var bytes = await Task.Run(() => App.Services.Thumbnails.GetJpeg(_photoPath, boxPx: 1600));
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _sourceBitmap = bitmap;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Photo illisible : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Navigator.Back();
            return;
        }

        ApplyRotation();
        if (_crop.IsFull) ResetCrop();
        UpdateFrameToggle(); // l'orientation par défaut n'est connue qu'une fois la photo lue
        Redraw();
    }

    private void ApplyRotation()
    {
        if (_sourceBitmap is null) return;
        _displayBitmap = _turns == 0
            ? _sourceBitmap
            : new TransformedBitmap(_sourceBitmap, new RotateTransform(90 * _turns));
        if (_displayBitmap.CanFreeze) _displayBitmap.Freeze();
        Photo.Source = _displayBitmap;
    }

    private void ResetCrop()
    {
        if (_displayBitmap is null) return;
        _crop = CropMath.CenterCrop(_displayBitmap.PixelWidth, _displayBitmap.PixelHeight, TargetAspect);
    }

    // ----- géométrie d'affichage -----

    /// <summary>Rectangle occupé par la photo dans la scène (Stretch=Uniform centré).</summary>
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
        if (display.IsEmpty) return;

        var cropping = _fit == FitMode.Fill;
        Overlay.Visibility = cropping ? Visibility.Visible : Visibility.Collapsed;
        FitMessage.Visibility = cropping ? Visibility.Collapsed : Visibility.Visible;
        if (!cropping) return;

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
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // ----- interactions -----

    private void Pan(double dxPx, double dyPx)
    {
        var display = DisplayRect();
        if (display.IsEmpty || _fit != FitMode.Fill) return;
        _crop = CropMath.Pan(_crop, dxPx / display.Width, dyPx / display.Height);
        Redraw();
    }

    private void Zoom(double cropFactor)
    {
        if (_displayBitmap is null || _fit != FitMode.Fill) return;
        _crop = CropMath.Zoom(_crop, cropFactor,
            _displayBitmap.PixelWidth, _displayBitmap.PixelHeight, TargetAspect);
        Redraw();
    }

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragLast = e.GetPosition(Stage);
        Stage.CaptureMouse();
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Stage.ReleaseMouseCapture();
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(Stage);
        Pan(pos.X - _dragLast.X, pos.Y - _dragLast.Y);
        _dragLast = pos;
    }

    // Pas de zoom pour un cran de molette standard (Delta = 120), étalé sur ~150 ms
    // par le SmoothZoomDriver : franc au total, continu à l'écran.
    private const double WheelZoomStep = 1.10;

    private void OnStageWheel(object sender, MouseWheelEventArgs e) =>
        // molette vers soi = zoom (cadre plus serré) ; l'exposant suit Delta pour
        // gérer les molettes haute résolution qui envoient moins de 120 par cran.
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
            // Le pincement doit coller aux doigts : pas de lissage, et on solde
            // un éventuel zoom molette encore en vol pour éviter qu'il dérive sous les doigts.
            _smoothZoom.Cancel();
            Zoom(1 / scale); // écarter les doigts = zoom = cadre plus serré
        }
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => _smoothZoom.Add(1 / 1.25);
    private void OnZoomOut(object sender, RoutedEventArgs e) => _smoothZoom.Add(1.25);

    private void OnRotate(object sender, RoutedEventArgs e) => Rotate(1);

    /// <param name="direction">+1 pour un quart de tour horaire, −1 antihoraire.</param>
    private void Rotate(int direction)
    {
        _turns = (_turns + direction + 4) % 4;
        ApplyRotation();
        ResetCrop(); // les repères changent : on repart du recadrage centré maximal
        UpdateFrameToggle();
        Redraw();
    }

    private void OnToggleFit(object sender, RoutedEventArgs e) => ToggleFit();

    private void ToggleFit()
    {
        _fit = _fit == FitMode.Fill ? FitMode.Fit : FitMode.Fill;
        UpdateFitToggle();
        Redraw();
    }

    private void UpdateFitToggle() =>
        FitToggle.Content = _fit == FitMode.Fill ? "Mode : Remplir" : "Mode : Entier";

    private void OnToggleFrame(object sender, RoutedEventArgs e) => ToggleFrame();

    /// <summary>Bascule le cadre entre portrait et paysage, la photo restant telle quelle.</summary>
    private void ToggleFrame()
    {
        _frameLandscape = !(TargetAspect > 1);
        ResetCrop(); // le cadre change de proportions : on repart d'un recadrage centré
        UpdateFrameToggle();
        Redraw();
    }

    private void UpdateFrameToggle() =>
        FrameToggle.Content = TargetAspect > 1 ? "Cadre : paysage" : "Cadre : portrait";

    private void OnReset(object sender, RoutedEventArgs e) => Reset();

    private void Reset()
    {
        _turns = 0;
        _fit = FitMode.Fill;
        _frameLandscape = null; // le cadre reprend l'orientation de la photo
        ApplyRotation();
        ResetCrop();
        UpdateFitToggle();
        UpdateFrameToggle();
        Redraw();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        // en mode Entier le recadrage n'a pas de sens : on repart de l'image complète
        var crop = _fit == FitMode.Fill ? _crop : CropSpec.Full;
        _onApply(new State(crop, _turns, _fit));
        Navigator.Back();
    }
}
