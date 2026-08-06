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
        UpdateFrameToggle();

        AttachShortcuts();

        Loaded += async (_, _) =>
        {
            await LoadPhotoAsync();
            // sans le focus, les flèches et Ctrl+I n'arriveraient jamais jusqu'ici
            Focus();
        };
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
            .On(Key.Z, () => Zoom(1 / PasBouton))
            .On([Key.Add, Key.OemPlus], () => Zoom(1 / PasBouton))
            .On([Key.Subtract, Key.OemMinus], () => Zoom(PasBouton))
            .On(Key.C, ToggleFit)
            // T pivotait le cadre ET armait le redressement : la même touche faisait deux
            // choses sur le même écran, et l'opérateur voyait son cadre basculer chaque
            // fois qu'il voulait redresser. T va au REDRESSEMENT (voir CropSurface), qui
            // est ce qu'on demande vingt fois par jour ; pivoter le cadre passe sur F, et
            // reste surtout à un clic droit sur la photo.
            .On(Key.F, ToggleFrame)
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
            // Polaroid : ce que l'opérateur cadre, c'est la FENÊTRE du film — presque
            // carrée — et non la feuille. Elle n'a pas d'orientation à choisir.
            if (_fit == FitMode.Polaroid) return PolaroidFrame.WindowAspect;

            // Bord blanc : on cadre la FENÊTRE, liseré déduit, et non la feuille — voir
            // Product.FenetreMm. Cadrer au rapport du papier faisait perdre une bande.
            var (fenetreW, fenetreH) = _product.FenetreMm;
            var aspect = fenetreW / fenetreH;

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

    /// <summary>
    /// Y a-t-il un cadrage à poser ? Oui en « remplir le format », oui sur un Polaroid — sa
    /// fenêtre presque carrée découpe forcément dans la photo — non en « photo entière »,
    /// où l'image est montrée telle quelle et bordée de blanc.
    /// </summary>
    private bool Recadre => _fit is FitMode.Fill or FitMode.Polaroid;

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

        var cropping = Recadre;
        Overlay.Visibility = cropping ? Visibility.Visible : Visibility.Collapsed;
        FitMessage.Visibility = cropping ? Visibility.Collapsed : Visibility.Visible;

        DessinerLePapier(cropping, display);
        if (!cropping) return;

        var cropRect = new Rect(
            display.X + _crop.X * display.Width,
            display.Y + _crop.Y * display.Height,
            _crop.Width * display.Width,
            _crop.Height * display.Height);

        Canvas.SetLeft(CropBorder, cropRect.X);
        Canvas.SetTop(CropBorder, cropRect.Y);
        CropBorder.Width = cropRect.Width;
        CropBorder.Height = cropRect.Height;
    }

    /// <summary>
    /// Montre le papier et ses marges, en mode « photo entière ».
    ///
    /// Le tirage, lui, fait déjà ce qu'il faut : <c>ImagePipeline</c> pose l'image dans le
    /// format et complète en blanc (<c>Extent(…, MagickColors.White)</c>). C'est l'écran
    /// qui mentait — il affichait la photo bord à bord et se contentait d'annoncer les
    /// marges par une phrase. On ne pouvait donc pas juger de la place qu'elles prennent,
    /// qui est pourtant toute la question quand on choisit ce mode.
    ///
    /// Le papier est le plus grand rectangle au format du tirage qui tienne dans la scène ;
    /// la photo y est réduite pour y entrer entière. Les deux étant centrés sur la scène,
    /// une simple mise à l'échelle autour de son centre suffit à poser la photo au bon
    /// endroit — pas de mise en page à refaire, donc rien qui puisse dériver du calcul de
    /// <see cref="DisplayRect"/> dont dépend le recadrage.
    /// </summary>
    private void DessinerLePapier(bool remplir, Rect display)
    {
        if (remplir)
        {
            Papier.Visibility = Visibility.Collapsed;
            Photo.RenderTransform = Transform.Identity;
            return;
        }

        var papierLargeur = Math.Min(Stage.ActualWidth, Stage.ActualHeight * TargetAspect);
        var papierHauteur = papierLargeur / TargetAspect;
        if (papierLargeur <= 0 || papierHauteur <= 0) return;

        Canvas.SetLeft(Papier, (Stage.ActualWidth - papierLargeur) / 2);
        Canvas.SetTop(Papier, (Stage.ActualHeight - papierHauteur) / 2);
        Papier.Width = papierLargeur;
        Papier.Height = papierHauteur;
        Papier.Visibility = Visibility.Visible;

        var facteur = Math.Min(papierLargeur / display.Width, papierHauteur / display.Height);
        Photo.RenderTransform = new ScaleTransform(
            facteur, facteur, Stage.ActualWidth / 2, Stage.ActualHeight / 2);
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // ----- interactions -----

    /// <summary>
    /// Déplace le cadrage d'un geste donné <b>en pixels de curseur</b> : la photo suit le
    /// doigt, donc la fenêtre de recadrage part à l'inverse — d'où le signe.
    ///
    /// C'est le même sens que sur la surface de recadrage de l'écran d'édition, et il a
    /// fallu deux passages pour le poser (01/08/2026) : deux écrans qui recadrent en sens
    /// contraire, c'est un cadrage sur deux qui part de travers.
    /// </summary>
    private void Pan(double dxPx, double dyPx)
    {
        var display = DisplayRect();
        if (display.IsEmpty || !Recadre) return;
        _crop = CropMath.Pan(_crop, -dxPx / display.Width, -dyPx / display.Height);
        Redraw();
    }

    private void Zoom(double cropFactor)
    {
        if (_displayBitmap is null || !Recadre) return;
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

    /// <summary>
    /// Un cran de molette = <b>un pixel d'écran</b> sur le cadrage, molette vers l'avant
    /// pour serrer. Même geste que sur la surface de l'écran d'édition.
    ///
    /// Le pas valait 10 % de la taille en cours, étalés sur une seconde par un lisseur.
    /// Deux défauts, et le second était le pire :
    ///
    /// — un pas proportionnel est d'autant plus gros que le cadrage l'est déjà, et cela se
    ///   voyait avancer par marches ;
    /// — le lisseur continuait d'appliquer son zoom pendant une seconde APRÈS le dernier
    ///   cran. L'opérateur qui zoomait, voyait que ça n'allait pas et cliquait aussitôt
    ///   sur « Réinitialiser » voyait le cadrage repartir de plus belle : le reste du zoom
    ///   retombait dessus juste après. D'où « le bouton Réinitialiser ne fonctionne pas »
    ///   (signalé le 01/08/2026) — il fonctionnait, c'est le zoom en vol qui le défaisait.
    ///
    /// Au pixel près et sans animation, il n'y a plus ni marche ni zoom en retard : ce
    /// qu'on voit à l'écran est l'état réel, tout le temps.
    /// </summary>
    private void OnStageWheel(object sender, MouseWheelEventArgs e)
    {
        // 120 est le cran de Windows ; une molette fine en envoie des fractions
        var crans = e.Delta / 120.0;
        if (crans == 0) return;

        var display = DisplayRect();
        if (display.IsEmpty) return;

        var largeurEcran = _crop.Width * display.Width;
        if (largeurEcran <= 2) return;

        Zoom((largeurEcran - crans) / largeurEcran);
        e.Handled = true;
    }

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

    /// <summary>Pas des boutons + et − : franc, puisqu'on ne les presse pas en rafale.</summary>
    private const double PasBouton = 1.25;

    private void OnZoomIn(object sender, RoutedEventArgs e) => Zoom(1 / PasBouton);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Zoom(PasBouton);

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
        // le Polaroid n'est pas un mode de cadrage qu'on bascule : c'est la forme du
        // produit. La bascule est grisée, ceci n'est qu'une ceinture de plus.
        if (_fit == FitMode.Polaroid) return;

        _fit = _fit == FitMode.Fill ? FitMode.Fit : FitMode.Fill;
        UpdateFitToggle();
        Redraw();
    }

    private void UpdateFitToggle()
    {
        FitToggle.Content = _fit switch
        {
            FitMode.Fill => "Mode : Remplir",
            FitMode.Polaroid => "Mode : Polaroid",
            _ => "Mode : Entier",
        };

        // ni le cadrage ni l'orientation ne se choisissent sur un Polaroid : la fenêtre du
        // film est presque carrée et n'a pas de sens de pose
        var polaroid = _fit == FitMode.Polaroid;
        FitToggle.IsEnabled = !polaroid;
        FrameToggle.IsEnabled = !polaroid;
    }

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
        // « Réinitialiser » revient au cadrage du PRODUIT : sur un Polaroid, remettre
        // « Remplir » ferait sortir un tirage sans cadre, que rien ne signalerait
        _fit = _product.DefaultFit == FitMode.Polaroid ? FitMode.Polaroid : FitMode.Fill;
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
        var crop = Recadre ? _crop : CropSpec.Full;
        _onApply(new State(crop, _turns, _fit));
        Navigator.Back();
    }
}
