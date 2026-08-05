using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;
using Studio.Printing;
using Studio.Sources;
using Studio.Store;

namespace Studio.App.Views;

/// <summary>
/// Photos d'identité : détection du visage → pré-cadrage 35×45 conforme,
/// gabarit surimprimé (vert quand conforme), ajustement manuel, impression
/// de la planche (produit à SheetSpec).
/// </summary>
public partial class IdPhotoView : UserControl, ITravailReprenable
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly Brush NeutralBrush = Brushes.White;

    private readonly string _rootPath;
    private readonly bool _avecSousDossiers;
    private readonly List<StripItem> _photos = new();
    private CancellationTokenSource? _loadCts;
    private int _quantity = 1;
    private int _copies = 6;   // photos sur la planche ; recalé sur le produit à la sélection

    private StripItem? _current;
    private BitmapSource? _displayBitmap;

    /// <summary>Aperçu détouré, quand « fond blanc » est coché. Null sinon.</summary>
    private BitmapSource? _detoure;

    /// <summary>
    /// Corrections d'image posées par le module « Corriger », hors noir et blanc et fond
    /// blanc — ces deux-là restent des cases de cet écran.
    ///
    /// Elles manquaient : une photo d'identité prise au comptoir est sous-exposée ou tire
    /// au jaune comme n'importe quelle autre, et il n'y avait ici aucun moyen de la
    /// reprendre. Demandé par l'exploitant le 04/08/2026.
    ///
    /// Elles valent pour la photo COURANTE : changer de photo dans la bande les remet à
    /// neutre. Une planche d'identité ne porte qu'un visage — appliquer les réglages de
    /// l'un à l'autre n'aurait pas de sens, et se remarquerait au tirage.
    /// </summary>
    private ImageAdjustments _corrections = new();

    /// <summary>Aperçu corrigé, gardé pour ne pas repasser ImageMagick à chaque redessin.</summary>
    private BitmapSource? _corrige;

    /// <summary>
    /// Redressement, en degrés — le « Tilt » de DiLand.
    ///
    /// Une photo d'identité prise à main levée penche presque toujours d'un demi-degré ou
    /// deux, et le guichet le voit. Le geste est celui des autres écrans qui recadrent
    /// (T maintenue + molette), pour ne pas faire réapprendre l'outil.
    /// </summary>
    private double _redressement;

    /// <summary>Pas de redressement, en degrés, par cran de molette.</summary>
    private const double PasDeRedressement = 0.25;

    /// <summary>Redressement maximal admis, en degrés, de part et d'autre.</summary>
    private const double RedressementMax = 15;
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

    /// <summary>
    /// Axe vertical commun aux deux anneaux, en fraction de la largeur de l'image.
    ///
    /// Les anneaux ne mesurent qu'une HAUTEUR — du sommet du crâne au bas du menton — et
    /// c'est elle seule qui fixe le cadre. Les laisser glisser latéralement ne changeait
    /// donc rien à la mesure, mais faisait pencher l'axe du visage et donnait un cadrage
    /// qui partait de travers pendant qu'on ajustait la hauteur. Ils restent alignés sur
    /// cet axe ; c'est le cadre qu'on déplace pour recentrer le visage.
    /// </summary>
    private double _axeVisage = 0.5;

    /// <summary>
    /// Les photos imposées par l'écran de sélection, dans SON ordre. Null = on scanne le
    /// dossier, comme avant.
    ///
    /// Les deux entrées coexistent : le parcours normal passe par la sélection, mais
    /// « Modifier » sur une commande du jour rouvre un dossier de commande entier, où tout
    /// est à traiter.
    /// </summary>
    private readonly IReadOnlyList<string>? _cheminsImposes;

    /// <param name="chemins">Photos retenues à l'écran précédent, dans l'ordre du choix.</param>
    /// <param name="document">Norme visée. Null = norme française.</param>
    public IdPhotoView(IReadOnlyList<string> chemins, IdDocumentSpec? document = null)
        : this("", document, false, chemins)
    {
    }

    /// <param name="rootPath">Dossier des photos.</param>
    /// <param name="document">
    /// Norme visée. Null = norme française, le cas courant de la boutique.
    /// </param>
    /// <param name="avecSousDossiers">Descendre ou non sous <paramref name="rootPath"/>.</param>
    public IdPhotoView(string rootPath, IdDocumentSpec? document = null, bool avecSousDossiers = true)
        : this(rootPath, document, avecSousDossiers, null)
    {
    }

    /// <summary>
    /// Reprend une planche mise de côté, telle qu'elle a été laissée : même norme, mêmes
    /// photos, mêmes repères, même photo affichée.
    /// </summary>
    /// <param name="travail">Le travail mis de côté ; son <c>Identite</c> ne doit pas être null.</param>
    public IdPhotoView(TravailEnAttente travail)
        : this(travail?.PhotosDirectory ?? "",
               DocumentDe(travail?.Identite ?? throw new ArgumentNullException(nameof(travail))),
               travail.AvecSousDossiers,
               travail.Identite.Chemins.Count > 0 ? travail.Identite.Chemins : null)
    {
        _enAttente = travail;
        _attenteId = travail.Id;
    }

    private IdPhotoView(string rootPath, IdDocumentSpec? document, bool avecSousDossiers,
        IReadOnlyList<string>? chemins)
    {
        _rootPath = rootPath;
        _avecSousDossiers = avecSousDossiers;
        _cheminsImposes = chemins;
        _document = document ?? IdDocumentSpec.France;
        InitializeComponent();

        TitleText.Text = _document.Country == "France"
            ? $"Photo d'identité {_document.WidthMm:0.#}×{_document.HeightMm:0.#}"
            : $"{_document.Country} — {_document.Document} ({_document.WidthMm:0.#}×{_document.HeightMm:0.#} mm)";

        // La capacité est celle du DOCUMENT visé, pas celle inscrite au produit : un
        // passeport espagnol (26 × 32) tient à douze sur le papier où le français (35 × 45)
        // tient à huit. Les papiers qui ne peuvent pas porter une seule case sont écartés
        // de la liste plutôt que proposés puis refusés à l'impression.
        var sheetProducts = App.Services.Catalog.Enabled
            .Where(p => p.Sheet is not null)
            .Select(p => new ProductChoice(p, CapaciteDe(p, _document)))
            .Where(c => c.Capacite > 0)
            .ToList();
        ProductCombo.ItemsSource = sheetProducts;
        ProductCombo.SelectedIndex = 0;

        // sans produit « planche » actif, l'écran était muet : combo vide, bouton grisé,
        // aucune explication. On le dit à l'opérateur, qui peut activer le produit au Catalogue.
        if (sheetProducts.Count == 0)
            Loaded += (_, _) => MessageBox.Show(
                $"Aucun papier du catalogue ne peut porter une photo de " +
                $"{_document.WidthMm:0.#} × {_document.HeightMm:0.#} mm.\n\n" +
                "Ouvrez Catalogue et activez (ou créez) un produit de type planche assez grand " +
                "pour ce document.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);

        Loaded += async (_, _) => await LoadStripAsync();
        Unloaded += (_, _) => _loadCts?.Cancel();

        // Le mode redressement se capte sur la FENÊTRE : voir _redressementArme. Sur cet
        // écran-ci, le focus part sur la liste des papiers dès que l'opérateur choisit son
        // tirage — la touche ne serait jamais revenue jusqu'ici.
        //
        // L'abonnement passe par ToucheFenetre : posé à la main, un second Loaded le
        // doublait et T — qui est une bascule — jouait deux fois, donc le mode ne
        // s'armait jamais. Voir ToucheFenetre.
        _ = new ToucheFenetre(this, OnFenetreKeyDown, auDepart: () => RedressementArme = false);
    }

    /// <param name="Capacite">Cases du document visé qui tiennent sur ce papier.</param>
    private sealed record ProductChoice(Product Product, int Capacite)
    {
        public string Label => $"{Product.Name} — {Capacite} par planche — {Product.Price:0.00} €";
    }

    /// <summary>
    /// Nombre de photos du document visé qui tiennent sur ce papier, 0 si pas même une.
    ///
    /// Le calcul se fait en PIXELS et par <see cref="IdSheetLayout.MaxCopies"/> — celui-là
    /// même qui posera la grille au rendu. Compter en millimètres donnerait parfois une case
    /// de plus que la planche n'accepte, et l'impression échouerait après l'annonce du prix.
    /// </summary>
    private static int CapaciteDe(Product product, IdDocumentSpec document)
    {
        if (product.Sheet is not { } sheet) return 0;

        return IdSheetLayout.MaxCopies(
            MmPx.ToPixels(product.WidthMm, product.Dpi),
            MmPx.ToPixels(product.HeightMm, product.Dpi),
            MmPx.ToPixels(document.WidthMm, product.Dpi),
            MmPx.ToPixels(document.HeightMm, product.Dpi),
            // l'écart RÉEL, celui que le rendu appliquera : à fond perdu il se réduit au
            // trait de découpe, et compter avec 2 mm annoncerait moins de photos que la
            // planche n'en porte (voir SheetSpec.LayoutGapMm)
            MmPx.ToPixels(sheet.LayoutGapMm, product.Dpi));
    }

    private async Task LoadStripAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        if (_photos.Count == 0)
        {
            // Photos imposées par l'écran de sélection : SON ordre, et rien d'autre. Le
            // rescanner ramènerait les quatre-vingts photos de la carte, dont l'opérateur
            // vient justement de désigner trois.
            //
            // Sinon on scanne, la plus récente en premier comme sur la grille des tirages :
            // la photo d'identité qu'on vient de prendre est en bout de carte.
            //
            // Les PDF sont écartés : on ne fait pas une photo d'identité depuis un
            // document, et la détection de visage n'aurait rien à y chercher.
            var files = _cheminsImposes is not null
                ? _cheminsImposes.Where(f => !PhotoScanner.IsPdf(f)).ToList()
                : await Task.Run(
                    () => PhotoScanner.TrierParDateDecroissante(
                            PhotoScanner.Scan(_rootPath, _avecSousDossiers, PhotoScanner.MaxAffichable, ct)
                                .Where(f => !PhotoScanner.IsPdf(f)))
                        .ToList(),
                    ct);

            var rang = 0;
            foreach (var file in files)
                _photos.Add(new StripItem(file) { Rang = ++rang });
            PhotoStrip.ItemsSource = _photos;

            // le travail mis de côté est reposé AVANT toute ouverture : ouvrir d'abord
            // relancerait la détection de visage sur une photo dont les repères sont
            // pourtant déjà connus
            if (_enAttente?.Identite is { } garde) AppliquerLAttente(garde);

            // sans photo, la bande reste vide : on le dit là où l'aperçu s'afficherait
            if (_photos.Count == 0)
                EmptyText.Text = "Aucune photo dans ce dossier — revenez en arrière " +
                                 "pour en choisir un autre.";

            AnnoncerLeLot();

            // La première photo s'ouvre TOUTE SEULE quand elles ont été choisies à l'écran
            // précédent : l'opérateur vient de les désigner, lui redemander de cliquer sur
            // la première serait un clic pour rien. Sur un dossier scanné, en revanche, il
            // n'a encore rien choisi — l'écran attend.
            // Une planche reprise rouvre la photo qu'on regardait, et non la première :
            // l'opérateur repart exactement là où il s'était arrêté.
            var aOuvrir = _enAttente?.Identite?.PhotoCourante is { Length: > 0 } nom
                ? _photos.FirstOrDefault(p => string.Equals(p.Name, nom, StringComparison.OrdinalIgnoreCase))
                : _cheminsImposes is not null ? _photos.FirstOrDefault() : null;

            if (aOuvrir is not null) await OuvrirLaPhotoAsync(aOuvrir);
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
        await OuvrirLaPhotoAsync(item);
    }

    /// <summary>
    /// Ouvre une photo dans la scène de cadrage, en DÉPOSANT d'abord le travail fait sur
    /// la précédente.
    ///
    /// L'ordre compte : sans le dépôt préalable, changer de photo perdrait tout ce qui vient
    /// d'être réglé sur celle qu'on quitte — c'était le comportement d'avant, et il
    /// obligeait à imprimer une photo avant d'en toucher une autre.
    /// </summary>
    private async Task OuvrirLaPhotoAsync(StripItem item)
    {
        if (ReferenceEquals(_current, item)) return;

        SauverDansLaPhoto();

        foreach (var p in _photos) p.Selected = p == item;
        _current = item;

        // Les aperçus calculés appartiennent à la photo précédente : les garder
        // afficherait le fond blanc et l'exposition d'un autre client. Ils se refont ; le
        // travail, lui, est repris de l'objet.
        _detoure = null;
        _corrige = null;

        EmptyText.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            var path = item.Path;
            var bytes = await Task.Run(() => App.Services.Thumbnails.GetJpeg(path, 1600));
            _displayBitmap = ToBitmap(bytes);

            ReprendreDeLaPhoto(item);

            // Première ouverture seulement : la détection écraserait le placement manuel
            // que l'opérateur vient de corriger à la main.
            if (!item.Prete)
            {
                var face = await Task.Run(() => App.Services.Faces.DetectMain(path));
                var detecte = face is null ? null : IdPhotoFr.EstimateHead(face.Box);
                PoserReperes(detecte);
                AutoCrop();
                item.Prete = true;
            }

            // le fond blanc se recalcule sur la nouvelle image, il ne se reporte pas
            if (item.FondBlanc) await RefaireLeFondBlancAsync();
            await RecalculerLApercuCorrigeAsync();
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
        MailButton.IsEnabled = _current is not null;
        Redraw();
        AnnoncerLeLot();
    }

    /// <summary>Dépose sur la photo courante tout ce que l'écran porte.</summary>
    private void SauverDansLaPhoto()
    {
        if (_current is not { } photo) return;

        photo.Crop = _crop;
        photo.Crown = _crown;
        photo.Chin = _chin;
        photo.Head = _head;
        photo.AxeVisage = _axeVisage;
        photo.Redressement = _redressement;
        photo.Corrections = _corrections.Clone();
        photo.NoirEtBlanc = GrayscaleCheck.IsChecked == true;
        photo.FondBlanc = WhiteBackgroundCheck.IsChecked == true;
        photo.Copies = _copies;
        photo.Quantite = _quantity;
    }

    /// <summary>
    /// Reprend sur l'écran ce que la photo porte.
    ///
    /// Les deux cases sont posées SANS déclencher leur gestionnaire : cocher « fond
    /// blanc » ici relancerait un détourage de quatre secondes alors qu'on vient de
    /// l'ordonner nous-même juste après.
    /// </summary>
    private void ReprendreDeLaPhoto(StripItem photo)
    {
        _crop = photo.Crop;
        _crown = photo.Crown;
        _chin = photo.Chin;
        _head = photo.Head;
        _axeVisage = photo.AxeVisage;
        _corrections = photo.Corrections.Clone();

        SansGestionnaires(() =>
        {
            GrayscaleCheck.IsChecked = photo.NoirEtBlanc;
            WhiteBackgroundCheck.IsChecked = photo.FondBlanc;
        });

        Redresser(photo.Redressement);
        SetQuantity(photo.Quantite);

        // 0 = jamais réglée : on part de la planche pleine, comme au choix du papier
        SetCopies(photo.Copies > 0 ? photo.Copies : MaxCopiesForSelectedProduct());

        MontrerLesCorrections();
    }

    /// <summary>
    /// Vrai pendant qu'on repose l'état d'une photo : les gestionnaires des deux cases
    /// doivent alors se taire.
    /// </summary>
    private bool _enReprise;

    private void SansGestionnaires(Action action)
    {
        _enReprise = true;
        try
        {
            action();
        }
        finally
        {
            _enReprise = false;
        }
    }

    /// <summary>
    /// Ce que le lot représente, en bas d'écran : le compte des planches à sortir.
    ///
    /// Il ne DÉPOSE rien : appelé depuis <c>SetQuantity</c>, qui est lui-même appelé
    /// pendant la reprise d'une photo, un dépôt écrirait sur la nouvelle photo les
    /// réglages de l'ancienne, à moitié repris.
    /// </summary>
    private void AnnoncerLeLot()
    {
        var planches = _photos.Sum(p => Math.Max(1, p.Quantite));
        LotText.Text = _photos.Count <= 1
            ? ""
            : $"{_photos.Count} photos · {planches} planche{(planches > 1 ? "s" : "")}";
    }

    /// <summary>
    /// Envoie au client la photo qu'il a sous les yeux — cadrage, redressement et
    /// corrections compris.
    ///
    /// C'est une prestation à part, facturée à la photo : elle n'imprime rien, et
    /// imprimer n'envoie rien. Un client peut vouloir les deux, ou l'un des deux, et le
    /// prix n'est pas le même.
    /// </summary>
    private void OnSendByMail(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show("Choisissez d'abord une photo dans la bande du bas.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Navigator.Go(
            new MailSendView([new MailSendView.PhotoAEnvoyer(
                _current.Path, _crop, 0, _redressement, ReglagesRetenus())]),
            "Envoyer les photos par courriel");
    }

    /// <summary>
    /// Tout ce qui sera appliqué aux pixels : les corrections du module, plus les deux
    /// cases de cet écran.
    ///
    /// Un seul endroit, parce qu'il y a trois sorties — la planche, le courriel et
    /// l'aperçu — et qu'une quatrième oubliée tirerait autre chose que ce qu'on montre.
    /// </summary>
    private ImageAdjustments ReglagesRetenus()
    {
        var reglages = _corrections.Clone();
        reglages.Grayscale = GrayscaleCheck.IsChecked == true;
        reglages.WhiteBackground = WhiteBackgroundCheck.IsChecked == true;
        return reglages;
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
            _axeVisage = Math.Clamp(tete.CenterX, 0, 1);
            _crown = new NormPoint(_axeVisage, tete.Y);
            _chin = new NormPoint(_axeVisage, tete.Bottom);
        }
        else
        {
            _axeVisage = 0.5;
            _crown = new NormPoint(_axeVisage, 0.22);
            _chin = new NormPoint(_axeVisage, 0.62);
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

        // seule la hauteur suit la souris : les deux anneaux restent sur l'axe du visage
        // (voir _axeVisage) et c'est le cadre, pas eux, qui se déplace latéralement
        var y = Math.Clamp((positionStage.Y - display.Y) / display.Height, 0, 1);
        var point = new NormPoint(_axeVisage, y);

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

    private void OnGrayscaleChanged(object sender, RoutedEventArgs e)
    {
        if (_enReprise) return;
        if (_current is not null) _current.NoirEtBlanc = GrayscaleCheck.IsChecked == true;
        ApplyGrayscalePreview();
    }

    /// <summary>
    /// Aperçu du fond blanc.
    ///
    /// Le calcul se fait sur l'aperçu (1600 px) et non sur la photo d'origine : il prend
    /// quatre secondes sur un 24 Mpx, ce qui figerait l'écran à chaque clic. Le tirage,
    /// lui, refait la découpe en pleine résolution — c'est le pipeline de rendu qui s'en
    /// charge, par <see cref="ImageAdjustments.WhiteBackground"/>.
    /// </summary>
    private async void OnWhiteBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (_enReprise) return;
        if (_current is null || _displayBitmap is null) return;

        _current.FondBlanc = WhiteBackgroundCheck.IsChecked == true;

        if (WhiteBackgroundCheck.IsChecked != true)
        {
            _detoure = null;
            await RecalculerLApercuCorrigeAsync();  // les corrections repartent de l'original
            return;
        }

        await RefaireLeFondBlancAsync();
    }

    /// <summary>
    /// Calcule (ou recalcule) le détourage de la photo courante.
    ///
    /// Isolé du gestionnaire de la case parce qu'il sert aussi à la REPRISE : une photo
    /// déjà réglée en fond blanc doit retrouver son détourage en revenant dessus, sans que
    /// reposer la case ne déclenche autre chose.
    /// </summary>
    private async Task RefaireLeFondBlancAsync()
    {
        if (_current is null || _displayBitmap is null) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var chemin = _current.Path;
            var octets = await Task.Run(() =>
            {
                var jpeg = App.Services.Thumbnails.GetJpeg(chemin, 1600);
                using var image = new ImageMagick.MagickImage(jpeg);
                return BackgroundRemoval.PoserUnFondBlanc(image)
                    ? image.ToByteArray(ImageMagick.MagickFormat.Png)
                    : null;
            });

            if (octets is null)
            {
                WhiteBackgroundCheck.IsChecked = false;
                MessageBox.Show(
                    "Le fond de cette photo n'est pas assez uni pour être remplacé sans risque " +
                    "d'entamer le sujet.\n\nLa photo est laissée telle quelle.",
                    "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _detoure = ToBitmap(octets);
        }
        catch (Exception ex)
        {
            WhiteBackgroundCheck.IsChecked = false;
            _detoure = null;
            FileLog.Write("Fond blanc impossible", ex);
            MessageBox.Show($"Fond blanc impossible : {ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;

            // le détourage vient de changer la base : l'aperçu corrigé est à refaire
            // par-dessus, sans quoi il montrerait encore l'ancien fond
            await RecalculerLApercuCorrigeAsync();
        }
    }

    /// <summary>
    /// L'aperçu, dans l'ordre du RENDU : fond blanc, puis corrections, puis noir et blanc.
    ///
    /// C'est celui d'<see cref="ImageAdjuster.Apply"/>, et il ne doit pas en différer : le
    /// fond blanc raisonne sur les couleurs d'origine (après une correction, le fond ne
    /// ressemblerait plus à ce que le pourtour a mesuré), et le noir et blanc vient en
    /// dernier, sans quoi les réglages de couleur n'auraient plus de prise.
    /// </summary>
    private void ApplyGrayscalePreview()
    {
        var source = _corrige ?? _detoure ?? _displayBitmap;
        if (source is null) return;

        if (GrayscaleCheck.IsChecked == true)
        {
            var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            Photo.Source = gray;
        }
        else
        {
            Photo.Source = source;
        }
    }

    // ----- corrections -----

    /// <summary>
    /// Ouvre le module de corrections sur la photo courante — le même écran que sur les
    /// tirages, avec les mêmes réglages.
    ///
    /// Le noir et blanc et le fond blanc n'y sont PAS passés : ce sont des cases de cet
    /// écran-ci, et les laisser aussi dans le module donnerait deux commandes pour un même
    /// réglage, dont l'une mentirait dès qu'on toucherait l'autre.
    /// </summary>
    private void OnCorrect(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show("Choisissez d'abord une photo dans la bande du bas.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var photo = _current.Path;
        Navigator.Go(
            new AdjustView([photo], _corrections, reglages =>
            {
                // un exemplaire à nous : l'écran des corrections garde le sien, et un objet
                // partagé se ferait modifier sous nos pieds à la prochaine ouverture
                _corrections = reglages.Clone();

                // les deux cases de cet écran restent maîtresses de leur réglage
                _corrections.Grayscale = false;
                _corrections.WhiteBackground = false;

                // les corrections appartiennent à la photo : sans ce report, revenir
                // dessus après en avoir vu une autre les retrouverait à neutre
                if (_current is not null) _current.Corrections = _corrections.Clone();

                _ = RecalculerLApercuCorrigeAsync();
            }),
            "Corrections");
    }

    /// <summary>
    /// Refait l'aperçu corrigé à partir de ce qui est déjà à l'écran, sur un fil de fond.
    ///
    /// Le calcul porte sur la vignette de 1600 px, pas sur l'original : quelques
    /// millisecondes, mais l'écran ne doit pas se figer pendant qu'un client regarde.
    /// Le TIRAGE, lui, refait tout en pleine résolution — c'est <see cref="ImagePipeline"/>
    /// qui applique les mêmes <see cref="ImageAdjustments"/>.
    /// </summary>
    private async Task RecalculerLApercuCorrigeAsync()
    {
        MontrerLesCorrections();

        var depart = _detoure ?? _displayBitmap;
        if (depart is null) return;

        if (_corrections.IsNeutral)
        {
            _corrige = null;
            ApplyGrayscalePreview();
            return;
        }

        var reglages = _corrections.Clone();
        var png = EnPng(depart);

        try
        {
            var octets = await Task.Run(() =>
            {
                using var image = new ImageMagick.MagickImage(png);
                ImageAdjuster.Apply(image, reglages);
                return image.ToByteArray(ImageMagick.MagickFormat.Png);
            });

            _corrige = ToBitmap(octets);
        }
        catch (Exception ex)
        {
            // l'aperçu n'est pas le tirage : on montre la photo non corrigée plutôt que
            // de bloquer l'écran, et le journal garde de quoi comprendre
            _corrige = null;
            FileLog.Write("Aperçu corrigé impossible (photo d'identité)", ex);
        }

        ApplyGrayscalePreview();
    }

    private static byte[] EnPng(BitmapSource source)
    {
        var encodeur = new PngBitmapEncoder();
        encodeur.Frames.Add(BitmapFrame.Create(source));

        using var flux = new MemoryStream();
        encodeur.Save(flux);
        return flux.ToArray();
    }

    /// <summary>Dit à l'opérateur que des corrections sont posées — sinon rien ne le montre.</summary>
    private void MontrerLesCorrections() =>
        CorrectionsText.Text = _corrections.IsNeutral ? "" : "corrections posées";

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
            // bornes du DOCUMENT visé : le conseil doit désigner le bon sens, et sur un
            // 50 × 50 les millimètres français indiquaient l'inverse de ce qu'il fallait
            issues.Add(c.CrownMarginMm < _document.CrownMarginMinMm
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

    // ----- redressement -----

    private bool _redressementArme;

    /// <summary>
    /// Le mode redressement est-il armé ?
    ///
    /// <b>Pourquoi une bascule et non une touche maintenue.</b> Il fallait tenir T ET
    /// rouler la molette ensemble ; au comptoir, une main est déjà prise. Et surtout,
    /// <c>Keyboard.IsKeyDown</c> lit le clavier tel que le voit l'élément qui a le FOCUS :
    /// sur cet écran, le focus tombe sur la liste des papiers dès qu'on a choisi son
    /// tirage, et la molette se remettait alors à zoomer sans prévenir. C'est ce qui
    /// faisait passer le redressement pour cassé.
    ///
    /// T bascule, Échap sort, et T maintenue continue de marcher : personne ne réapprend.
    /// </summary>
    private bool RedressementArme
    {
        get => _redressementArme;
        set
        {
            if (_redressementArme == value) return;
            _redressementArme = value;
            MontrerLeBandeauRedressement();
        }
    }

    private void OnFenetreKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;

        // dans un champ de saisie, « t » est une lettre
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) return;

        if (e.Key == Key.T)
        {
            RedressementArme = !RedressementArme;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && RedressementArme)
        {
            RedressementArme = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _agrandi)
        {
            // le redressement d'abord : c'est un MODE, et Échap sort du plus imbriqué
            Agrandissement(false);
            e.Handled = true;
        }
    }

    private void MontrerLeBandeauRedressement()
    {
        BandeauRedressement.Visibility = RedressementArme ? Visibility.Visible : Visibility.Collapsed;
        BandeauRedressementTexte.Text =
            $"Redressement {RedressementText.Text} — molette pour régler · T ou Échap pour sortir";
    }

    private void OnRedresserGauche(object sender, RoutedEventArgs e) =>
        Redresser(_redressement - PasDeRedressement);

    private void OnRedresserDroite(object sender, RoutedEventArgs e) =>
        Redresser(_redressement + PasDeRedressement);

    private void OnRedressementRemiseAZero(object sender, RoutedEventArgs e) => Redresser(0);

    /// <summary>
    /// Pose l'angle de redressement et le montre.
    ///
    /// L'aperçu pivote à l'écran plutôt que de refabriquer l'image : sur une photo de
    /// 24 Mpx, refaire le bitmap à chaque cran rendrait le geste inutilisable. Le tirage,
    /// lui, applique la rotation pour de bon (RenderRequest.FineRotationDegrees), et c'est
    /// le rendu qui remplit de blanc les coins libérés.
    /// </summary>
    private void Redresser(double degres)
    {
        _redressement = Math.Clamp(degres, -RedressementMax, RedressementMax);
        if (_current is not null) _current.Redressement = _redressement;

        RedressementText.Text = Math.Abs(_redressement) < 0.01
            ? "0°"
            : $"{_redressement:+0.##;-0.##}°";

        MontrerLeBandeauRedressement();   // l'angle s'affiche dans le bandeau tant qu'il est là
        AppliquerLeRedressementALAffichage();
    }

    private void AppliquerLeRedressementALAffichage()
    {
        Photo.RenderTransformOrigin = new Point(0.5, 0.5);
        Photo.RenderTransform = Math.Abs(_redressement) < 0.01
            ? Transform.Identity
            : new RotateTransform(_redressement);
    }

    // ----- interactions (mêmes gestes que l'éditeur de recadrage) -----

    /// <summary>Proportions du cadre : celles du document visé, pas un 35/45 figé.</summary>
    private double TargetAspect => _document.WidthMm / _document.HeightMm;

    /// <summary>
    /// Le CADRE suit le doigt.
    ///
    /// Cet écran-ci ne bouge pas la photo : <c>Photo</c> reste posée telle quelle et c'est
    /// l'<c>Overlay</c> — le voile et le rectangle de cadrage — qui se redessine. Le geste
    /// était pourtant inversé (<c>-dx</c>), copié des deux autres écrans de recadrage où
    /// c'est bien la PHOTO qui glisse sous un cadre fixe : là-bas, pousser la photo à droite
    /// revient à reculer la fenêtre de cadrage, ici cela l'envoie à l'opposé du curseur.
    ///
    /// Signalé le 04/08/2026 : « lorsqu'on déplace le cadre, les mouvements sont inversés ».
    /// Voir <c>CropSurface.OnMouseMove</c>, qui garde son signe pour la raison inverse.
    /// </summary>
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

    /// <summary>
    /// Un cran de molette = un pixel d'écran sur le cadrage, molette vers l'avant pour
    /// serrer. Même geste que sur les deux autres écrans qui recadrent.
    ///
    /// Le lisseur a disparu avec le pas proportionnel : il rendait le zoom saccadé, et
    /// surtout il continuait de s'appliquer une seconde après le dernier cran — de quoi
    /// défaire par-derrière un recadrage automatique ou une remise à zéro déclenchés
    /// juste après (voir <c>CropEditorView.OnStageWheel</c>).
    /// </summary>
    private void OnStageWheel(object sender, MouseWheelEventArgs e)
    {
        var crans = e.Delta / 120.0;
        if (crans == 0) return;

        // Mode armé (T), ou T maintenue : c'est le redressement qui prend la molette,
        // comme sur les autres écrans de recadrage (voir CropSurface)
        if (RedressementArme || Keyboard.IsKeyDown(Key.T))
        {
            Redresser(_redressement + crans * PasDeRedressement);
            e.Handled = true;
            return;
        }

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
            Zoom(1 / scale);

        e.Handled = true;
    }

    private void OnQuantityMinus(object sender, RoutedEventArgs e) => SetQuantity(_quantity - 1);
    private void OnQuantityPlus(object sender, RoutedEventArgs e) => SetQuantity(_quantity + 1);

    private void SetQuantity(int value)
    {
        _quantity = Math.Clamp(value, 1, 20);
        QuantityText.Text = _quantity.ToString();

        // la vignette porte « ×N » : sans ce report, la bande annoncerait encore l'ancienne
        // quantité et l'opérateur ne verrait son geste que sur le compteur
        if (_current is not null) _current.Quantite = _quantity;
        AnnoncerLeLot();
    }

    private void OnCopiesMinus(object sender, RoutedEventArgs e) => SetCopies(_copies - 1);
    private void OnCopiesPlus(object sender, RoutedEventArgs e) => SetCopies(_copies + 1);

    /// <summary>Nombre de photos sur la planche, borné par ce qui tient réellement sur le tirage.</summary>
    private void SetCopies(int value)
    {
        _copies = Math.Clamp(value, 1, Math.Max(1, MaxCopiesForSelectedProduct()));
        CopiesText.Text = _copies.ToString();

        if (_current is not null) _current.Copies = _copies;
    }

    private int MaxCopiesForSelectedProduct() =>
        ProductCombo.SelectedItem is ProductChoice choice ? choice.Capacite : 1;

    /// <summary>
    /// Changer de produit repart de la planche PLEINE.
    ///
    /// Le nombre de copies inscrit au produit (« planche de 8 ») vaut pour le format
    /// français ; sur un document plus petit, il laisserait des places vides payées au même
    /// prix — la planche est facturée au papier. L'opérateur peut toujours descendre.
    /// </summary>
    private void OnProductChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductCombo.SelectedItem is not ProductChoice choice) return;

        // Le papier vaut pour TOUT le lot — c'est le rouleau chargé, il n'y en a qu'un.
        // Les photos qu'on n'a pas encore rouvertes doivent donc repartir de la planche
        // pleine du NOUVEAU papier : garder « 8 » sur un papier qui en porte 12 laisserait
        // des places vides payées au même prix, sans que rien ne le dise.
        foreach (var photo in _photos)
            if (!ReferenceEquals(photo, _current))
                photo.Copies = 0;

        SetCopies(choice.Capacite);
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

    /// <summary>
    /// Passe au récapitulatif : la planche s'y regarde AVANT d'engager le papier.
    ///
    /// Ce bouton imprimait directement, et c'est ce qui coûtait une feuille à chaque
    /// mauvaise surprise — l'écran de cadrage montre le CADRE sur la photo, pas la
    /// planche : ni la disposition, ni le nombre de vignettes, ni l'horodatage. Voir
    /// <see cref="IdSheetRecapView"/>, qui porte désormais l'impression.
    /// </summary>
    private void OnPrint(object sender, RoutedEventArgs e)
    {
        // ne jamais sortir en silence : sans produit planche activé, le bouton semblait mort
        if (ProductCombo.SelectedItem is not ProductChoice choice)
        {
            MessageBox.Show(
                $"Aucun papier du catalogue ne peut porter une photo de " +
                $"{_document.WidthMm:0.#} × {_document.HeightMm:0.#} mm.\n\n" +
                "Ouvrez Catalogue et activez (ou créez) un produit de type planche assez grand " +
                "pour ce document.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_current is null)
        {
            MessageBox.Show("Choisissez d'abord une photo dans la bande de gauche.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // le travail de la photo affichée n'est pas encore déposé : sans cela, le
        // récapitulatif montrerait le cadrage d'AVANT la dernière retouche
        SauverDansLaPhoto();

        // Avertit sans bloquer : l'opérateur reste juge (visage non détecté, photo
        // médiocre…). Le contrôle porte sur TOUT le lot — n'examiner que la photo à
        // l'écran laisserait passer les autres sans un mot.
        var douteuses = _photos
            .Where(p => p.Head is not null && !IdPhotoFr.Check(p.Crop, p.Head, _document).Compliant)
            .ToList();

        if (douteuses.Count > 0)
        {
            var lesquelles = string.Join(", ", douteuses.Select(p => p.Name));
            var reponse = MessageBox.Show(
                $"Le cadrage ne respecte pas le gabarit {_document.WidthMm:0.#}×{_document.HeightMm:0.#} " +
                $"sur {douteuses.Count} photo(s) : {lesquelles}.\n\nContinuer quand même ?",
                "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (reponse != MessageBoxResult.Yes) return;
        }

        var finition = FinishCombo.SelectedItem as string;

        var planches = _photos
            .Select(p => new IdSheetRecapView.Planche(
                p.Path,
                p.Crop,
                p.Redressement,
                ReglagesDe(p),
                p.Copies > 0 ? p.Copies : choice.Capacite,
                Math.Max(1, p.Quantite),
                choice.Product,
                finition,
                p.Rang))
            .ToList();

        Navigator.Go(
            new IdSheetRecapView(planches, _document,
                surModifier: RevenirSurLaPhoto,
                surRemplacer: RevenirAuChoixDesPhotos,
                attenteId: _attenteId),
            planches.Count == 1 ? "Récapitulatif de la planche" : "Récapitulatif des planches");
    }

    /// <summary>
    /// Tout ce qui sera appliqué aux pixels d'une photo du lot : ses corrections, plus ses
    /// deux cases.
    ///
    /// Jumelle de <see cref="ReglagesRetenus"/>, qui ne sait lire que l'écran. Les deux
    /// existent parce que l'envoi par courriel porte sur la photo AFFICHÉE, tandis que le
    /// récapitulatif porte sur toutes.
    /// </summary>
    private static ImageAdjustments ReglagesDe(StripItem photo)
    {
        var reglages = photo.Corrections.Clone();
        reglages.Grayscale = photo.NoirEtBlanc;
        reglages.WhiteBackground = photo.FondBlanc;
        return reglages;
    }

    /// <summary>Retour du récapitulatif sur une photo précise, pour la recadrer.</summary>
    private void RevenirSurLaPhoto(int rang)
    {
        Navigator.Back();

        var photo = _photos.FirstOrDefault(p => p.Rang == rang);
        if (photo is not null) _ = OuvrirLaPhotoAsync(photo);
    }

    /// <summary>
    /// Retour au choix des photos.
    ///
    /// Deux retours quand on vient de l'écran de sélection, un seul sinon : « Modifier »
    /// depuis les commandes du jour ouvre cet écran directement, et un second retour
    /// remonterait à la liste des commandes — c'est-à-dire ailleurs que là où le bouton
    /// promet d'aller.
    /// </summary>
    private void RevenirAuChoixDesPhotos()
    {
        Navigator.Back();
        if (_cheminsImposes is not null) Navigator.Back();
    }

    // ----- voir la photo en grand -----

    /// <summary>Vrai quand la bande et les réglages sont escamotés au profit de la photo.</summary>
    private bool _agrandi;

    /// <summary>
    /// Escamote la bande de gauche et la barre de réglages pour ne garder que la photo.
    ///
    /// <b>Pourquoi ce bouton existe.</b> Une photo d'identité est en portrait, et la scène
    /// de cadrage est large et basse : c'est la HAUTEUR qui la limite, et elle est prise
    /// par le titre, la barre de réglages et le bandeau des machines. On a resserré tout ce
    /// qui pouvait l'être — bande à 160 px, boutons à 40, marges réduites —, mais le vrai
    /// gain est là : sans la barre, la photo double presque de taille.
    ///
    /// <b>Rien n'est perdu en chemin</b> : les repères, le cadrage et les réglages
    /// continuent de vivre dans la photo courante. Le mode ne fait que cacher des panneaux.
    ///
    /// Échap en sort, comme du mode redressement — c'est le même réflexe.
    /// </summary>
    private void OnBasculerAgrandissement(object sender, RoutedEventArgs e) =>
        Agrandissement(!_agrandi);

    private void Agrandissement(bool actif)
    {
        _agrandi = actif;

        var panneaux = actif ? Visibility.Collapsed : Visibility.Visible;
        BandePhotos.Visibility = panneaux;
        BarreReglages.Visibility = panneaux;
        AideText.Visibility = panneaux;

        // la colonne elle-même doit disparaître : masquer son contenu lui laisserait ses
        // 160 px, c'est-à-dire précisément ce qu'on vient chercher
        BandeColonne.Width = actif ? new GridLength(0) : new GridLength(160);

        // un seul espace : XAML normalise les blancs de son contenu littéral, pas le C#, et
        // le libellé aurait changé d'espacement en basculant
        AgrandirButton.Content = actif ? "⛶ Réduire" : "⛶ Agrandir";

        // la scène change de taille : le cadre et les anneaux se replacent dessus
        Redraw();
    }

    // ----- mise en attente : servir quelqu'un d'autre, puis reprendre -----

    /// <summary>
    /// L'identité de CETTE planche, qu'elle ait déjà été mise de côté ou non. Fixe pour
    /// toute la vie de l'écran : deux mises en attente successives mettent à jour la même
    /// entrée au lieu d'en empiler deux sur l'accueil.
    /// </summary>
    private readonly Guid _attenteId = Guid.NewGuid();

    /// <summary>Le travail repris, s'il y en a un : sert à garder son titre.</summary>
    private readonly TravailEnAttente? _enAttente;

    /// <inheritdoc/>
    public string ResumeDeLAttente
    {
        get
        {
            var prêtes = _photos.Count(p => p.Prete);
            var morceaux = new List<string>
            {
                _document.Country == "France"
                    ? $"identité {_document.WidthMm:0.#}×{_document.HeightMm:0.#}"
                    : $"{_document.Country} — {_document.Document}",
                $"{_photos.Count} photo(s)",
            };

            if (prêtes > 0) morceaux.Add($"{prêtes} cadrée(s)");
            return string.Join(" · ", morceaux);
        }
    }

    /// <inheritdoc/>
    public bool EnregistrerPourReprise()
    {
        if (_photos.Count == 0) return false;

        // ce que l'écran porte appartient à la photo courante : sans ce dépôt, le cadrage
        // et les repères qu'on vient de poser partiraient à la corbeille
        SauverDansLaPhoto();

        try
        {
            var travail = ConstruireLAttente();
            App.Services.CommandesEnAttente.Enregistrer(travail);

            FileLog.Write($"Planche d'identité mise en attente ({travail.Resume}) — " +
                          $"{travail.PhotosDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write("Mise en attente de la planche d'identité impossible", ex);
            return false;
        }
    }

    private TravailEnAttente ConstruireLAttente() => new()
    {
        Id = _attenteId,
        SavedAt = DateTimeOffset.Now,

        // Le dossier sert à VÉRIFIER que les photos sont encore là avant de rouvrir. Avec
        // des photos imposées par la sélection, il peut être vide — on prend alors le
        // dossier de la première, qui est celui que le client a apporté.
        PhotosDirectory = !string.IsNullOrWhiteSpace(_rootPath)
            ? _rootPath
            : System.IO.Path.GetDirectoryName(_photos[0].Path) ?? "",
        AvecSousDossiers = _avecSousDossiers,
        Titre = _enAttente?.Titre is { Length: > 0 } deja ? deja : TitreDeLaPlanche(),
        Resume = ResumeDeLAttente,
        Identite = new IdentiteEnAttente
        {
            Country = _document.Country,
            Document = _document.Document,
            WidthMm = _document.WidthMm,
            HeightMm = _document.HeightMm,
            HeadMinMm = _document.HeadMinMm,
            HeadMaxMm = _document.HeadMaxMm,
            CrownMarginMm = _document.CrownMarginMm,
            TargetHeadOverrideMm = _document.TargetHeadOverrideMm,
            Chemins = _cheminsImposes is null ? [] : [.. _cheminsImposes],
            PhotoCourante = _current?.Name,
            Photos = _photos.Select(p => new PhotoIdentiteEnAttente
            {
                FileName = p.Name,
                Selected = p.Selected,
                Quantity = p.Quantite,
                Copies = p.Copies,
                Prete = p.Prete,
                CropX = p.Crop.X,
                CropY = p.Crop.Y,
                CropWidth = p.Crop.Width,
                CropHeight = p.Crop.Height,
                CrownX = p.Crown?.X,
                CrownY = p.Crown?.Y,
                ChinX = p.Chin?.X,
                ChinY = p.Chin?.Y,
                HeadX = p.Head?.X,
                HeadY = p.Head?.Y,
                HeadWidth = p.Head?.Width,
                HeadHeight = p.Head?.Height,
                AxeVisage = p.AxeVisage,
                Redressement = p.Redressement,
                NoirEtBlanc = p.NoirEtBlanc,
                FondBlanc = p.FondBlanc,
                Corrections = p.Corrections.Clone(),
            }).ToList(),
        },
    };

    private string TitreDeLaPlanche() => _document.Country == "France"
        ? $"Identité {_document.WidthMm:0.#}×{_document.HeightMm:0.#}"
        : $"Identité {_document.Country} — {_document.Document}";

    /// <summary>La norme telle qu'elle a été mise de côté.</summary>
    private static IdDocumentSpec DocumentDe(IdentiteEnAttente identite) => new(
        identite.Country, identite.Document,
        identite.WidthMm, identite.HeightMm,
        identite.HeadMinMm, identite.HeadMaxMm,
        identite.CrownMarginMm, identite.TargetHeadOverrideMm);

    /// <summary>
    /// Repose sur les photos de la bande le travail mis de côté.
    ///
    /// Les photos sont retrouvées par leur NOM DE FICHIER : le rang se décalerait dès
    /// qu'un fichier manque, et on reprendrait le cadrage du voisin sans que rien ne le
    /// dise. Une photo disparue est simplement sautée.
    ///
    /// <b><see cref="StripItem.Prete"/> est reposé en dernier</b>, avec le reste : c'est
    /// lui qui empêche la détection de visage de se relancer et d'écraser les repères
    /// qu'on vient justement de reprendre.
    /// </summary>
    private void AppliquerLAttente(IdentiteEnAttente identite)
    {
        var parNom = identite.Photos.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var photo in _photos)
        {
            if (!parNom.TryGetValue(photo.Name, out var garde)) continue;

            photo.Crop = new CropSpec(garde.CropX, garde.CropY, garde.CropWidth, garde.CropHeight);
            photo.Crown = garde.CrownX is { } cx && garde.CrownY is { } cy ? new NormPoint(cx, cy) : null;
            photo.Chin = garde.ChinX is { } mx && garde.ChinY is { } my ? new NormPoint(mx, my) : null;
            photo.Head = garde.HeadX is { } hx && garde.HeadY is { } hy
                         && garde.HeadWidth is { } hw && garde.HeadHeight is { } hh
                ? new NormRect(hx, hy, hw, hh)
                : null;
            photo.AxeVisage = garde.AxeVisage;
            photo.Redressement = garde.Redressement;
            photo.NoirEtBlanc = garde.NoirEtBlanc;
            photo.FondBlanc = garde.FondBlanc;
            photo.Corrections = garde.Corrections.Clone();
            photo.Quantite = garde.Quantity;
            photo.Copies = garde.Copies;
            photo.Prete = garde.Prete;
        }
    }

    /// <summary>
    /// Une photo de la bande, avec TOUT ce que l'opérateur a posé dessus.
    ///
    /// L'écran ne traitait qu'une photo à la fois et jetait son travail dès qu'on en
    /// choisissait une autre — repères, cadrage, corrections, redressement, tout repartait
    /// à neutre. Pour deux personnes d'une même famille, il fallait imprimer la première
    /// avant de toucher à la seconde, donc deux commandes et deux passages en caisse.
    ///
    /// Le travail vit donc ICI, et l'écran n'en est que la vue : il DÉPOSE l'état courant
    /// avant de changer de photo (<c>SauverDansLaPhoto</c>) et le REPREND ensuite
    /// (<c>ReprendreDeLaPhoto</c>).
    /// </summary>
    private sealed class StripItem : ObservableObject
    {
        private ImageSource? _thumbnail;
        private bool _selected;
        private int _quantite = 1;

        public StripItem(string path) => Path = path;

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);

        /// <summary>Rang dans le lot, à partir de 1 — celui de l'écran de sélection.</summary>
        public int Rang { get; init; }

        /// <summary>
        /// Vrai dès que les repères ont été posés au moins une fois. Sans ce drapeau, une
        /// photo rouverte relancerait la détection de visage et écraserait le placement
        /// manuel que l'opérateur venait justement de corriger.
        /// </summary>
        public bool Prete { get; set; }

        // — le travail de l'opérateur, photo par photo —

        public CropSpec Crop { get; set; } = CropSpec.Full;
        public NormPoint? Crown { get; set; }
        public NormPoint? Chin { get; set; }
        public NormRect? Head { get; set; }
        public double AxeVisage { get; set; } = 0.5;
        public double Redressement { get; set; }
        public ImageAdjustments Corrections { get; set; } = new();
        public bool NoirEtBlanc { get; set; }
        public bool FondBlanc { get; set; }

        /// <summary>Photos par planche. Recalé sur la capacité du papier au premier passage.</summary>
        public int Copies { get; set; }

        /// <summary>
        /// Nombre de planches identiques pour CETTE photo. Deux personnes commandent
        /// rarement la même quantité, et c'est ce que la bande affiche (« ×2 »).
        /// </summary>
        public int Quantite
        {
            get => _quantite;
            set
            {
                if (!Set(ref _quantite, value)) return;
                OnPropertyChanged(nameof(Etiquette));
            }
        }

        /// <summary>Ce qui se lit sur la vignette : le rang et le nombre de planches.</summary>
        public string Etiquette => $"{Rang}  ·  ×{Quantite}";

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
