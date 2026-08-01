using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Threading.Tasks;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.App.Views;

/// <summary>
/// L'écran « Modifier » : on travaille la sélection sans jamais la perdre de vue.
///
/// Le menu déroulant de gauche montre toutes les photos retenues, avec leur cadre de
/// tirage dessiné dessus ; le grand aperçu de droite montre la photo courante. Recadrage
/// et corrections se voient EN DIRECT aux deux endroits. C'est ce qui remplace le défilé
/// photo par photo, où l'on ne savait jamais où on en était sur la planche.
///
/// Gestes repris de DiLand :
/// <list type="bullet">
///   <item><c>C</c> maintenue : recadrer à la souris directement sur une vignette,
///   celle que le curseur survole.</item>
///   <item>clic droit : pivoter le CADRE, pas la photo.</item>
///   <item><c>T</c> maintenue + molette : pivoter la photo.</item>
/// </list>
/// </summary>
internal partial class EditSelectionView : UserControl
{
    private readonly List<PhotoGridView.PhotoItem> _photos;
    private readonly Action _imprimer;
    private PhotoGridView.PhotoItem _courante;

    private Point _dernierPoint;
    private bool _glisse;
    private PhotoGridView.PhotoItem? _glisseSur;

    /// <param name="photos">Les photos retenues, dans l'ordre de la planche.</param>
    /// <param name="imprimer">Lance l'impression de la sélection, telle que la prépare l'écran précédent.</param>
    public EditSelectionView(List<PhotoGridView.PhotoItem> photos, Action imprimer)
    {
        ArgumentNullException.ThrowIfNull(photos);

        _photos = photos;
        _imprimer = imprimer;
        _courante = photos[0];

        InitializeComponent();

        Strip.ItemsSource = _photos;
        Sliders.ItemsSource = ConstruireReglages();

        ShowCrop();
        Refresh();

        Loaded += (_, _) => Focus(); // sans le focus, ni C ni T ne nous parviennent
    }

    /// <summary>Libellé du bouton de mode, lu par la liaison du panneau.</summary>
    public string FitLabel =>
        (_courante.FitOverride ?? _courante.Product?.DefaultFit ?? FitMode.Fill) == FitMode.Fill
            ? "Mode : remplir le format"
            : "Mode : photo entière";

    // — affichage —

    private void Refresh()
    {
        // surtout PAS la vignette ici : Refresh est appelé après chaque geste, et il
        // écrasait la source haute définition par les 360 px de la bande — l'aperçu
        // redevenait flou dès qu'on touchait à un réglage.
        RedessinerApercu();

        var rang = _photos.IndexOf(_courante) + 1;
        PreviewCaption.Text =
            $"{rang}/{_photos.Count} · {_courante.Name} · {_courante.ProductLabel} · ×{_courante.Quantity}";

        var tirages = _photos.Sum(p => p.Quantity);
        var total = _photos.Sum(p => (p.Product?.Price ?? 0) * p.Quantity);
        SummaryText.Text = $"{_photos.Count} photo(s) · {tirages} tirage(s) · {total:0.00} €";

        AutoLevelsToggle.IsChecked = _courante.Adjustments.AutoLevels;
        AutoContrastToggle.IsChecked = _courante.Adjustments.AutoContrast;
        AutoColorToggle.IsChecked = _courante.Adjustments.AutoColor;

        foreach (var reglage in (IEnumerable<Reglage>)Sliders.ItemsSource) reglage.Relire(_courante.Adjustments);
    }

    /// <summary>
    /// Sources en haute définition du grand aperçu, chargées à la demande.
    ///
    /// La vignette plafonne à 360 px : agrandie sur la moitié de l'écran, elle était
    /// floue et ne permettait pas de juger une netteté ni un cadrage au pixel près. On
    /// garde donc deux définitions — la petite pour la bande, la grande pour l'aperçu —
    /// et une seule et même composition pour les deux.
    /// </summary>
    private readonly Dictionary<string, BitmapSource> _hautesDefinitions = new();

    private const int PreviewBoxPx = 1600;

    /// <summary>Redessine la photo touchée : vignette ET grand aperçu, pour voir en direct.</summary>
    private void Redessiner(PhotoGridView.PhotoItem photo)
    {
        photo.RefreshThumbnail();
        if (ReferenceEquals(photo, _courante)) RedessinerApercu();
    }

    private void RedessinerApercu()
    {
        if (_hautesDefinitions.TryGetValue(_courante.Path, out var source))
        {
            var compose = _courante.Compose(source);
            Preview.Source = compose;

            // si l'aperçu reste flou, c'est ici qu'on le verra : une image composée à
            // 360 px alors qu'on a chargé 1600 trahit une source qui n'est pas la bonne
            if (compose is System.Windows.Media.Imaging.BitmapSource rendu && rendu.PixelWidth < 800)
                FileLog.Write($"Aperçu : composé en {rendu.PixelWidth}×{rendu.PixelHeight} " +
                              $"alors que la source fait {source.PixelWidth}×{source.PixelHeight}");
            return;
        }

        Preview.Source = _courante.Thumbnail; // le temps que la haute définition arrive
    }

    private async void ChargerHauteDefinition(PhotoGridView.PhotoItem photo)
    {
        if (_hautesDefinitions.ContainsKey(photo.Path)) return;

        try
        {
            var octets = await Task.Run(
                () => App.Services.Thumbnails.GetJpeg(photo.Path, PreviewBoxPx));

            using var flux = new MemoryStream(octets);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = flux;
            bitmap.EndInit();
            bitmap.Freeze();

            _hautesDefinitions[photo.Path] = bitmap;
            FileLog.Write($"Aperçu : {photo.Name} chargé en {bitmap.PixelWidth}×{bitmap.PixelHeight}");

            if (ReferenceEquals(photo, _courante)) RedessinerApercu();
        }
        catch (Exception ex)
        {
            // illisible en grand : la vignette fait l'affaire, on ne bloque pas l'écran.
            // Mais on le DIT dans le journal : c'est la seule façon de savoir pourquoi
            // l'aperçu reste flou au lieu de le supposer.
            FileLog.Write($"Aperçu : chargement haute définition impossible ({photo.Name})", ex);
        }
    }

    private void SetCurrent(PhotoGridView.PhotoItem photo)
    {
        _courante = photo;
        Cadre(photo); // crée le cadre au format du produit, ou le reprend
        ChargerHauteDefinition(photo);
        Refresh();
    }

    /// <summary>
    /// Met le cadre au rapport du produit, centré et aussi grand que possible.
    ///
    /// Sans cela le cadre partait de l'image entière : un 10×15 demandé sur une photo
    /// 2:3 sortait au rapport de la photo, pas à celui du tirage. C'est le défaut le plus
    /// grave signalé le 01/08/2026 — il ne se voit qu'une fois le papier sorti.
    ///
    /// Le rapport se calcule en PIXELS : un cadre est une fraction de l'image, donc un
    /// même rapport en millimètres ne donne pas la même fraction selon la définition.
    /// </summary>


    /// <summary>Le plus grand cadre de ce rapport qui tient dans l'image, centré.</summary>

    /// <summary>
    /// Les photos qu'un « appliquer à tout » touche : celles restées cochées.
    ///
    /// Toutes arrivent cochées ; c'est Ctrl+clic dans la bande qui en écarte. Si l'on a
    /// tout décoché, on retombe sur la photo courante plutôt que de ne rien faire — un
    /// bouton qui ne fait rien laisse croire à une panne.
    /// </summary>
    private List<PhotoGridView.PhotoItem> Visees()
    {
        var cochees = _photos.Where(p => p.Selected).ToList();
        return cochees.Count > 0 ? cochees : [_courante];
    }

    // — recadrage à la souris —

    /// <summary>
    /// Un cadre par photo, tenu entre deux gestes. C'est lui qui porte la vérité : le
    /// <see cref="CropSpec"/> de la photo n'en est que la traduction pour le rendu.
    /// </summary>
    private readonly Dictionary<string, FramedCrop> _cadres = new();

    /// <summary>
    /// Le cadre d'une photo, créé au premier besoin AU FORMAT DU PRODUIT.
    ///
    /// Le format est fixé ici une fois pour toutes : le cadre ne changera plus de
    /// proportions, donc aucun geste ne peut le faire dériver. C'est tout l'intérêt du
    /// modèle de DiLand, et ce qui manquait à la version précédente.
    /// </summary>
    private FramedCrop? Cadre(PhotoGridView.PhotoItem photo)
    {
        if (_cadres.TryGetValue(photo.Path, out var connu)) return connu;
        if (photo.Product is not { } produit) return null;
        if (photo.SourceAspect <= 0) return null; // définition pas encore lue

        // le cadre suit l'orientation de la photo, comme le fait le rendu (OrientCanvas)
        var (largeur, hauteur) = (produit.WidthMm, produit.HeightMm);
        if (photo.SourceAspect >= 1 != largeur >= hauteur)
            (largeur, hauteur) = (hauteur, largeur);

        var pixels = photo.SourcePixels;
        var cadre = new FramedCrop(pixels.Width, pixels.Height, largeur, hauteur);

        // on reprend le cadrage déjà enregistré, s'il y en a un
        if (!photo.Crop.IsFull) cadre.SetFromCropSpec(photo.Crop);

        _cadres[photo.Path] = cadre;
        Appliquer(photo, cadre);
        return cadre;
    }

    /// <summary>Reporte le cadre sur la photo et redessine — le seul point de conversion.</summary>
    private void Appliquer(PhotoGridView.PhotoItem photo, FramedCrop cadre)
    {
        photo.Crop = cadre.ToCropSpec();
        Redessiner(photo);
    }

    /// <summary>Fait glisser la photo derrière le cadre.</summary>
    private void Deplacer(PhotoGridView.PhotoItem photo, double dx, double dy)
    {
        if (Cadre(photo) is not { } cadre) return;

        // les deltas arrivent en fractions ; le cadre travaille dans ses propres unités
        cadre.Move(dx * cadre.FrameWidth, dy * cadre.FrameHeight);
        Appliquer(photo, cadre);
    }

    /// <summary>
    /// Resserre ou élargit le cadre autour de son centre. Le rapport est conservé : un
    /// cadre qui ne serait plus à la forme du produit donnerait un tirage déformé.
    /// </summary>
    private void Zoomer(PhotoGridView.PhotoItem photo, bool avant)
    {
        if (Cadre(photo) is not { } cadre) return;

        if (avant) cadre.ZoomIn();
        else cadre.ZoomOut();

        Appliquer(photo, cadre);
    }

    /// <summary>
    /// Pivote le CADRE d'un quart de tour : ses deux côtés s'échangent, la photo ne bouge
    /// pas. Faire tenir une photo verticale dans un tirage horizontal est un besoin
    /// quotidien, et pivoter la photo ne le résout pas.
    /// </summary>
    private void PivoterCadre(PhotoGridView.PhotoItem photo)
    {
        if (Cadre(photo) is not { } ancien) return;

        // pivoter le cadre, c'est échanger ses deux côtés. Dans ce modèle l'opération est
        // triviale et surtout SANS EFFET DE BORD : la photo se replace toute seule pour
        // couvrir le nouveau cadre. L'ancienne version échangeait les côtés du recadrage,
        // ce qui ne faisait rien du tout quand celui-ci valait l'image entière.
        var pixels = photo.SourcePixels;
        var pivote = new FramedCrop(pixels.Width, pixels.Height, ancien.FrameHeight, ancien.FrameWidth);

        _cadres[photo.Path] = pivote;
        Appliquer(photo, pivote);
    }

    private void PivoterPhoto(PhotoGridView.PhotoItem photo, int sens)
    {
        photo.RotationQuarterTurns = (photo.RotationQuarterTurns + sens + 4) % 4;
        Redessiner(photo);
    }

    /// <summary>Un degré par cran de molette : DiLand stocke un angle ENTIER.</summary>
    private const double PasRedressement = 1;

    /// <summary>
    /// Le « Tilt » de DiLand (touche T) : un redressement de quelques degrés, pour
    /// remettre un horizon d'aplomb. Rien à voir avec les quarts de tour de
    /// Ctrl+←/Ctrl+→ — c'était l'erreur signalée le 01/08/2026.
    /// </summary>
    private void Redresser(PhotoGridView.PhotoItem photo, int sens)
    {
        // bornes de DiLand, relevées dans son PhotoItem : -90 < angle < 90, en degrés
        // entiers. Au-delà, c'est un quart de tour qu'il faut, pas un redressement.
        photo.FineRotationDegrees = Math.Clamp(
            Math.Round(photo.FineRotationDegrees + sens * PasRedressement), -89, 89);

        // le cadre doit suivre : une photo inclinée offre moins de surface utile, donc
        // la photo grandit et se replace pour ne pas laisser de coin vide dans le tirage
        if (Cadre(photo) is { } cadre)
        {
            cadre.RotationDegrees = photo.FineRotationDegrees;
            Appliquer(photo, cadre);
        }

        Redessiner(photo);
        Refresh();
    }

    // — gestes sur les vignettes (C maintenue) —

    private static bool CTenue => Keyboard.IsKeyDown(Key.C);
    private static bool TTenue => Keyboard.IsKeyDown(Key.T);

    /// <summary>
    /// Note dans le journal ce qu'un geste a réellement reçu.
    ///
    /// Les gestes souris ne se vérifient pas depuis la ligne de commande : aucun test ne
    /// presse C ni ne clique. Sans cette trace, on en est réduit à supposer pourquoi un
    /// raccourci « ne marche pas » — et on s'est trompé plusieurs fois le 01/08/2026.
    /// </summary>
    private static void Tracer(string geste, PhotoGridView.PhotoItem? photo = null) =>
        FileLog.Write($"Geste « {geste} » · C={CTenue} T={TTenue} " +
                      $"Ctrl={Keyboard.Modifiers.HasFlag(ModifierKeys.Control)}" +
                      (photo is null ? "" : $" · {photo.Name}"));

    private static PhotoGridView.PhotoItem? Cible(object sender) =>
        (sender as FrameworkElement)?.Tag as PhotoGridView.PhotoItem;

    private void OnStripDown(object sender, MouseButtonEventArgs e)
    {
        if (Cible(sender) is not { } photo) return;
        Tracer(nameof(OnStripDown), photo);

        // Ctrl maintenue : on restreint le tir. Toutes les photos arrivent ici cochées ;
        // Ctrl+clic en écarte, pour qu'un réglage ne parte que sur celles qu'on vise.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            photo.Selected = !photo.Selected;
            SetCurrent(photo);
            return;
        }

        SetCurrent(photo);

        if (!CTenue) return;

        _glisse = true;
        _glisseSur = photo;
        _dernierPoint = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void OnStripMove(object sender, MouseEventArgs e)
    {
        if (!_glisse || _glisseSur is null) return;

        var point = e.GetPosition(this);
        var dx = (point.X - _dernierPoint.X) / 200.0;
        var dy = (point.Y - _dernierPoint.Y) / 200.0;
        _dernierPoint = point;

        // on tire la photo sous le cadre : le cadre part donc en sens inverse
        Deplacer(_glisseSur, -dx, -dy);
    }

    private void OnStripUp(object sender, MouseButtonEventArgs e)
    {
        _glisse = false;
        _glisseSur = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnStripWheel(object sender, MouseWheelEventArgs e)
    {
        if (Cible(sender) is not { } photo) return;
        Tracer(nameof(OnStripWheel), photo);
        if (!CTenue && !TTenue) return;

        if (TTenue) Redresser(photo, e.Delta > 0 ? 1 : -1);
        else Zoomer(photo, e.Delta > 0);

        e.Handled = true;
    }

    /// <summary>
    /// Pivoter le cadrage, mais seulement C maintenue : c'est le geste documenté par
    /// DiLand lui-même (« C + Right click » → <c>S_Buttons_RotateCrop</c>). Sans C, le
    /// clic droit ne doit rien faire, sous peine de pivoter un cadre par mégarde.
    /// </summary>
    private void OnStripRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Cible(sender) is not { } photo) return;
        Tracer(nameof(OnStripRightClick), photo);
        if (!CTenue) return;

        SetCurrent(photo);
        PivoterCadre(photo);
        e.Handled = true;
    }

    // — mêmes gestes sur le grand aperçu, sans touche à maintenir —

    private void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        _glisse = true;
        _glisseSur = _courante;
        _dernierPoint = e.GetPosition(this);
        Stage.CaptureMouse();
    }

    private void OnPreviewMove(object sender, MouseEventArgs e) => OnStripMove(sender, e);

    private void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        _glisse = false;
        _glisseSur = null;
        Stage.ReleaseMouseCapture();
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (TTenue) Redresser(_courante, e.Delta > 0 ? 1 : -1);
        else Zoomer(_courante, e.Delta > 0);

        e.Handled = true;
    }

    private void OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!CTenue) return;

        PivoterCadre(_courante);
        e.Handled = true;
    }

    // — panneaux —

    private void OnShowCrop(object sender, RoutedEventArgs e) => ShowCrop();
    private void OnShowCorrect(object sender, RoutedEventArgs e) => ShowCorrect();

    private void ShowCrop()
    {
        CropPanel.Visibility = Visibility.Visible;
        CorrectPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowCorrect()
    {
        CropPanel.Visibility = Visibility.Collapsed;
        CorrectPanel.Visibility = Visibility.Visible;
    }

    private void OnRotateFrame(object sender, RoutedEventArgs e) => PivoterCadre(_courante);
    private void OnRotatePhoto(object sender, RoutedEventArgs e) => PivoterPhoto(_courante, 1);

    private void OnToggleFit(object sender, RoutedEventArgs e)
    {
        var produit = _courante.Product;
        if (produit is null) return;

        var actuel = _courante.FitOverride ?? produit.DefaultFit;
        var voulu = actuel == FitMode.Fill ? FitMode.Fit : FitMode.Fill;
        _courante.FitOverride = voulu == produit.DefaultFit ? null : voulu;

        Redessiner(_courante);
        Refresh();
    }

    private void OnResetCrop(object sender, RoutedEventArgs e)
    {
        _courante.Crop = CropSpec.Full;
        _courante.RotationQuarterTurns = 0;
        Redessiner(_courante);
    }

    /// <summary>Le cadrage de la photo courante, repris sur toute la planche d'un geste.</summary>
    private void OnCropToAll(object sender, RoutedEventArgs e)
    {
        foreach (var photo in Visees())
        {
            photo.Crop = _courante.Crop;
            photo.RotationQuarterTurns = _courante.RotationQuarterTurns;
            photo.FitOverride = _courante.FitOverride;
            Redessiner(photo);
        }
    }

    // — corrections —

    private void OnAutoChanged(object sender, RoutedEventArgs e)
    {
        _courante.Adjustments.AutoLevels = AutoLevelsToggle.IsChecked == true;
        _courante.Adjustments.AutoContrast = AutoContrastToggle.IsChecked == true;
        _courante.Adjustments.AutoColor = AutoColorToggle.IsChecked == true;
        Redessiner(_courante);
    }

    private void OnResetAdjustments(object sender, RoutedEventArgs e)
    {
        _courante.Adjustments = new ImageAdjustments();
        Redessiner(_courante);
        Refresh();
    }

    private void OnCorrectToAll(object sender, RoutedEventArgs e)
    {
        foreach (var photo in Visees())
        {
            // un exemplaire par photo : un objet partagé ferait qu'un réglage ultérieur
            // sur l'une déborderait sur toutes les autres
            photo.Adjustments = _courante.Adjustments.Clone();
            Redessiner(photo);
        }
    }

    /// <summary>Un curseur du panneau, façon Lightroom.</summary>
    private sealed class Reglage : ObservableObject
    {
        private readonly Func<ImageAdjustments, double> _lire;
        private readonly Action<ImageAdjustments, double> _ecrire;
        private readonly Action _change;
        private ImageAdjustments _cible = new();
        private double _valeur;

        public Reglage(string nom, double min, double max,
            Func<ImageAdjustments, double> lire, Action<ImageAdjustments, double> ecrire, Action change)
        {
            Nom = nom;
            Min = min;
            Max = max;
            _lire = lire;
            _ecrire = ecrire;
            _change = change;
        }

        public string Nom { get; }
        public double Min { get; }
        public double Max { get; }

        public string Affichage => _valeur.ToString(Max <= 5 ? "+0.00;-0.00;0" : "+0;-0;0");

        public double Valeur
        {
            get => _valeur;
            set
            {
                if (!Set(ref _valeur, value)) return;
                OnPropertyChanged(nameof(Affichage));
                _ecrire(_cible, value);
                _change();
            }
        }

        /// <summary>Reprend la valeur de la photo courante, sans déclencher de rendu.</summary>
        public void Relire(ImageAdjustments cible)
        {
            _cible = cible;
            _valeur = _lire(cible);
            OnPropertyChanged(nameof(Valeur));
            OnPropertyChanged(nameof(Affichage));
        }
    }

    private List<Reglage> ConstruireReglages()
    {
        void Change() => Redessiner(_courante);

        return
        [
            new("Exposition (IL)", -2, 2, a => a.Exposure, (a, v) => a.Exposure = v, Change),
            new("Contraste", -100, 100, a => a.Contrast, (a, v) => a.Contrast = v, Change),
            new("Hautes lumières", -100, 100, a => a.Highlights, (a, v) => a.Highlights = v, Change),
            new("Ombres", -100, 100, a => a.Shadows, (a, v) => a.Shadows = v, Change),
            new("Blancs", -100, 100, a => a.Whites, (a, v) => a.Whites = v, Change),
            new("Noirs", -100, 100, a => a.Blacks, (a, v) => a.Blacks = v, Change),
            new("Température", -100, 100, a => a.Temperature, (a, v) => a.Temperature = v, Change),
            new("Teinte", -100, 100, a => a.Tint, (a, v) => a.Tint = v, Change),
            new("Vibrance", -100, 100, a => a.Vibrance, (a, v) => a.Vibrance = v, Change),
            new("Saturation", -100, 100, a => a.Saturation, (a, v) => a.Saturation = v, Change),
            new("Clarté", -100, 100, a => a.Clarity, (a, v) => a.Clarity = v, Change),
            new("Netteté", 0, 100, a => a.Sharpness, (a, v) => a.Sharpness = v, Change),
        ];
    }

    // — sortie —

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnPrint(object sender, RoutedEventArgs e) => _imprimer();
}
