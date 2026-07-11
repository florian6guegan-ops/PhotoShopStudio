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

    public CropEditorView(string photoPath, Product product, State initial, Action<State> onApply)
    {
        _photoPath = photoPath;
        _product = product;
        _onApply = onApply;
        _crop = initial.Crop;
        _turns = initial.RotationQuarterTurns;
        _fit = initial.Fit;

        InitializeComponent();
        TitleText.Text = $"Recadrage — {product.Name}";
        UpdateFitToggle();

        Loaded += async (_, _) => await LoadPhotoAsync();
    }

    /// <summary>Aspect pixel du cadre : celui du produit, orienté comme la photo (cf. OrientCanvas au rendu).</summary>
    private double TargetAspect
    {
        get
        {
            var aspect = _product.WidthMm / _product.HeightMm;
            if (_displayBitmap is null) return aspect;
            var imageLandscape = _displayBitmap.PixelWidth > _displayBitmap.PixelHeight;
            return imageLandscape == aspect > 1 ? aspect : 1 / aspect;
        }
    }

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

    private void OnStageWheel(object sender, MouseWheelEventArgs e) =>
        Zoom(e.Delta > 0 ? 1 / 1.15 : 1.15); // molette vers soi = zoom (cadre plus serré)

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
            Zoom(1 / scale); // écarter les doigts = zoom = cadre plus serré
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Zoom(1 / 1.25);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Zoom(1.25);

    private void OnRotate(object sender, RoutedEventArgs e)
    {
        _turns = (_turns + 1) % 4;
        ApplyRotation();
        ResetCrop(); // les repères changent : on repart du recadrage centré maximal
        Redraw();
    }

    private void OnToggleFit(object sender, RoutedEventArgs e)
    {
        _fit = _fit == FitMode.Fill ? FitMode.Fit : FitMode.Fill;
        UpdateFitToggle();
        Redraw();
    }

    private void UpdateFitToggle() =>
        FitToggle.Content = _fit == FitMode.Fill ? "Mode : Remplir" : "Mode : Entier";

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _turns = 0;
        _fit = FitMode.Fill;
        ApplyRotation();
        ResetCrop();
        UpdateFitToggle();
        Redraw();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // en mode Entier le recadrage n'a pas de sens : on repart de l'image complète
        var crop = _fit == FitMode.Fill ? _crop : CropSpec.Full;
        _onApply(new State(crop, _turns, _fit));
        Navigator.Back();
    }
}
