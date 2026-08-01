using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Printing.Devices.Fuji;
using Studio.Imaging;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.App.Views;

public partial class PhotoGridView : UserControl
{
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".bmp", ".tif", ".tiff", ".webp" };

    private readonly string _rootPath;
    private readonly List<PhotoItem> _photos = new();
    private CancellationTokenSource? _thumbnailCts;
    private int _quantity = 1;
    private readonly long? _commandeBorne;

    /// <param name="rootPath">Dossier des photos à proposer.</param>
    /// <param name="produitParDefaut">
    /// Format déjà choisi en amont, comme dans le parcours de DiLand. Vide = premier
    /// produit du catalogue, l'opérateur choisira dans la liste.
    /// </param>
    /// <param name="commandeBorne">
    /// OID de la commande de borne à l'origine de ces photos, s'il y en a une. Elle reste
    /// affichée dans « Commandes des bornes » jusqu'à ce que le tirage soit sorti : c'est
    /// l'impression réussie, ici, qui la fait basculer dans l'historique.
    /// </param>
    public PhotoGridView(string rootPath, string? produitParDefaut = null, long? commandeBorne = null)
    {
        _rootPath = rootPath;
        _commandeBorne = commandeBorne;
        InitializeComponent();

        var choix = App.Services.Catalog.Enabled.Select(p => new ProductChoice(p)).ToList();
        ProductCombo.ItemsSource = choix;

        AttachShortcuts();

        var prechoisi = produitParDefaut is null
            ? -1
            : choix.FindIndex(c => c.Product.Code.Equals(produitParDefaut, StringComparison.OrdinalIgnoreCase));
        ProductCombo.SelectedIndex = prechoisi >= 0 ? prechoisi : 0;

        Loaded += async (_, _) =>
        {
            await ScanAndLoadAsync();

            Focus(); // sans le focus, aucune touche ne nous parvient
            await LoadMachinesAsync();
        };
        Unloaded += (_, _) => _thumbnailCts?.Cancel();
    }

    private sealed record ProductChoice(Product Product)
    {
        public string Label => $"{Product.Name} — {Product.Price:0.00} €";
    }

    private Product? DefaultProduct => (ProductCombo.SelectedItem as ProductChoice)?.Product;

    private async Task ScanAndLoadAsync()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        if (_photos.Count == 0)
        {
            var files = await Task.Run(() => FindImages(_rootPath), ct);
            foreach (var file in files)
                _photos.Add(new PhotoItem(file, OnCartChanged));
            PhotosGrid.ItemsSource = _photos;
            UpdateSummary();
        }

        // vignettes en tâche de fond, une par une pour ne pas saturer le support
        var thumbnails = App.Services.Thumbnails;
        foreach (var photo in _photos)
        {
            if (ct.IsCancellationRequested) return;
            if (photo.Thumbnail is not null) continue;
            try
            {
                var bytes = await Task.Run(() => thumbnails.GetJpeg(photo.Path), ct);
                photo.SetSourceThumbnail(ToBitmap(bytes));

                // définition et rapport, affichés sur la vignette comme chez DiLand :
                // l'opérateur voit tout de suite ce qu'il peut tirer sans perte
                var (largeur, hauteur) = await Task.Run(
                    () => ImagePipeline.GetOrientedSize(photo.Path, 0), ct);
                photo.SetSourceSize(largeur, hauteur);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                // fichier illisible : on le laisse sans vignette, il reste sélectionnable par son nom
            }
        }
    }

    /// <summary>Parcours tolérant : dossiers système/inaccessibles ignorés sans interrompre le scan.</summary>
    private static List<string> FindImages(string root)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    pending.Push(sub);
                foreach (var file in Directory.EnumerateFiles(dir))
                    if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        result.Add(file);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
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

    private void OnPhotoClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not PhotoItem photo) return;

        // un clic coche ou décoche, comme le SelectionMode.Multiple de DiLand : ici on
        // compose la commande, il n'y a rien à restreindre. Le tri à la touche Ctrl est
        // dans l'écran Modifier, là où il sert à viser quelques photos d'un réglage.
        Toggle(photo);
    }

    /// <summary>Ajoute une photo à la commande, ou l'en retire.</summary>
    private void Toggle(PhotoItem photo)
    {
        if (!photo.Selected && photo.Product is null)
        {
            // première sélection : la photo prend le produit et la quantité du bandeau
            photo.Product = DefaultProduct;
            photo.Quantity = _quantity;
        }

        photo.Selected = !photo.Selected;

        // la dernière photo touchée est celle que « Recadrer » vise
        if (photo.Selected) _photoCourante = photo;
        else if (ReferenceEquals(_photoCourante, photo)) _photoCourante = null;
    }

    // — barre d'outils de la planche (réduire, agrandir, tout, aucun, trier) —

    private void OnSmaller(object sender, RoutedEventArgs e) => Zoom(1 / 1.15);
    private void OnBigger(object sender, RoutedEventArgs e) => Zoom(1.15);

    private void Zoom(double facteur)
    {
        // bornes larges mais réelles : sous 0,5 on ne distingue plus un visage, au-delà
        // de 2 on ne voit plus assez de photos pour choisir
        var echelle = Math.Clamp(GridZoom.ScaleX * facteur, 0.5, 2.0);
        GridZoom.ScaleX = echelle;
        GridZoom.ScaleY = echelle;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (_photos.All(p => p.Selected)) return; // déjà tout pris : le bouton ne défait pas
        SelectAll();
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var photo in _photos) photo.Selected = false;
        _photoCourante = null;
    }

    private bool _triParDate;

    /// <summary>Bascule entre tri par nom et tri par date de prise de vue, comme « trier » chez DiLand.</summary>
    private void OnSort(object sender, RoutedEventArgs e)
    {
        _triParDate = !_triParDate;

        var triees = _triParDate
            ? _photos.OrderBy(p => File.GetLastWriteTime(p.Path)).ToList()
            : _photos.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        _photos.Clear();
        _photos.AddRange(triees);

        PhotosGrid.ItemsSource = null;
        PhotosGrid.ItemsSource = _photos;
        UpdateSummary();
    }

    /// <summary>Ctrl+A : toute la planche d'un coup, comme chez DiLand.</summary>
    private void SelectAll()
    {
        // tout est déjà pris : on décoche, pour que la même touche défasse
        var toutPris = _photos.Count > 0 && _photos.All(p => p.Selected);

        foreach (var photo in _photos)
        {
            if (toutPris)
            {
                photo.Selected = false;
                continue;
            }

            if (photo.Selected) continue;
            photo.Product ??= DefaultProduct;
            photo.Quantity = _quantity;
            photo.Selected = true;
        }

        if (toutPris) _photoCourante = null;
        else _photoCourante ??= _photos.LastOrDefault();
    }

    private void OnCartChanged()
    {
        UpdateSummary();
    }

    /// <summary>Une machine du minilab, avec le papier qui y est chargé.</summary>
    private sealed record MachineChoice(char Id, string Label);

    /// <summary>
    /// Charge les machines du minilab et le papier de chacune. L'opérateur choisit sur
    /// quelle machine tirer, et voit du même coup ce qui y est chargé : imprimer un 13×18
    /// sur un rouleau de 152 mm ne donne rien de bon.
    /// </summary>
    private async Task LoadMachinesAsync()
    {
        try
        {
            var etats = await App.Services.Minilab.SnapshotAsync();

            var choix = etats
                .Where(e => e.Status != De100PrinterStatus.Offline)
                .Select(e => new MachineChoice(e.MachineId, DecrireMachine(e)))
                .ToList();

            MachineCombo.ItemsSource = choix;
            if (choix.Count > 0) MachineCombo.SelectedIndex = 0;
            MachineCombo.IsEnabled = choix.Count > 1;

            if (choix.Count == 0)
                MachineCombo.ItemsSource = new[] { new MachineChoice(' ', "aucune machine en ligne") };
        }
        catch (Exception ex)
        {
            FileLog.Write("Liste des machines du minilab indisponible", ex);
            MachineCombo.ItemsSource = new[] { new MachineChoice(' ', "minilab injoignable") };
            MachineCombo.SelectedIndex = 0;
            MachineCombo.IsEnabled = false;
        }
    }

    private static string DecrireMachine(De100PrinterInfo info)
    {
        if (info.Media is not { } media) return $"{info.MachineId} — papier inconnu";

        var restant = info.Formats.FirstOrDefault(f => !f.Format.IsVariable);
        var suffixe = restant is null ? "" : $", ~{restant.RemainingPrints} × {restant.Format.Name}";
        return $"{info.MachineId} — {media.PaperWidthMm} mm {media.Surface}{suffixe}";
    }

    private void OnMachineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MachineCombo.SelectedItem is not MachineChoice choix || choix.Id == ' ') return;
        App.Services.Printer.PreferredMinilabMachine = choix.Id.ToString();
    }

    private void UpdateSummary()
    {
        var selected = _photos.Where(p => p.Selected).ToList();
        CountText.Text = selected.Count == 0
            ? $"{_photos.Count} photos trouvées"
            : $"{selected.Count} sélectionnée{(selected.Count > 1 ? "s" : "")} sur {_photos.Count}";
        var total = selected.Sum(p => (p.Product?.Price ?? 0) * p.Quantity);
        TotalText.Text = selected.Count == 0 ? "" : $"{total:0.00} €";
        PrintButton.IsEnabled = selected.Count > 0;

        ModifyButton.IsEnabled = selected.Count > 0;
        ModifyButton.Content = selected.Count > 1 ? $"Modifier ({selected.Count})" : "Modifier";
    }

    /// <summary>
    /// Ouvre l'écran de travail sur la sélection : recadrage et corrections, la planche
    /// restant sous les yeux. L'impression part de là, une fois tout réglé.
    /// </summary>
    private void OnModify(object sender, RoutedEventArgs e)
    {
        var choisies = _photos.Where(p => p.Selected).ToList();
        if (choisies.Count == 0) return;

        // un produit est indispensable pour connaître la forme du cadre
        foreach (var photo in choisies) photo.Product ??= DefaultProduct;

        Navigator.Go(
            new EditSelectionView(choisies, () => OnPrint(this, new RoutedEventArgs())),
            $"Modifier — {choisies.Count} photo(s)");
    }

    // ----- bandeau : s'applique à toutes les photos cochées -----

    private void OnDefaultProductChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultProduct is null) return;
        foreach (var photo in _photos.Where(p => p.Selected))
            photo.Product = DefaultProduct;
        UpdateSummary();
    }

    private void OnQuantityMinus(object sender, RoutedEventArgs e) => SetQuantity(_quantity - 1);
    private void OnQuantityPlus(object sender, RoutedEventArgs e) => SetQuantity(_quantity + 1);

    private void SetQuantity(int value)
    {
        _quantity = Math.Clamp(value, 1, 99);
        QuantityText.Text = _quantity.ToString();
        foreach (var photo in _photos.Where(p => p.Selected))
            photo.Quantity = _quantity;
        UpdateSummary();
    }

    // ----- bandeau de la vignette : produit et quantité de cette photo -----

    private void OnTileMinus(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PhotoItem photo)
            photo.Quantity = Math.Clamp(photo.Quantity - 1, 1, 99);
    }

    private void OnTilePlus(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PhotoItem photo)
            photo.Quantity = Math.Clamp(photo.Quantity + 1, 1, 99);
    }

    /// <summary>
    /// Dernière photo que l'opérateur a cochée. Elle sert de cible au bouton « Recadrer »
    /// de la barre du bas : cocher une photo la désigne, la décocher ne la désigne pas.
    /// </summary>
    private PhotoItem? _photoCourante;

    // — raccourcis clavier, repris de DiLand —

    /// <summary>
    /// Les photos sur lesquelles agit un raccourci : celles qui sont cochées, ou à défaut
    /// la dernière touchée. C'est la règle de DiLand, qui applique ses commandes à
    /// <c>SelectedItems</c> — on règle une planche entière d'un geste.
    /// </summary>
    private List<PhotoItem> Cibles()
    {
        var choisies = _photos.Where(p => p.Selected).ToList();
        if (choisies.Count > 0) return choisies;

        return _photoCourante is null ? [] : [_photoCourante];
    }

    /// <summary>Fixe la quantité des photos visées, en les ajoutant à la commande si besoin.</summary>
    private void SetQuantityOnTargets(int quantite)
    {
        foreach (var photo in Cibles())
        {
            if (quantite <= 0)
            {
                photo.Selected = false;
                continue;
            }

            if (!photo.Selected)
            {
                photo.Product ??= DefaultProduct;
                photo.Selected = true;
            }

            photo.Quantity = quantite;
        }
    }

    private void ChangeQuantityOnTargets(int delta)
    {
        foreach (var photo in Cibles())
        {
            if (!photo.Selected) continue;
            photo.Quantity = Math.Max(1, photo.Quantity + delta);
        }
    }

    /// <summary>
    /// Corrections au clavier, sur les photos visées.
    ///
    /// DiLand règle trois canaux indépendants (R, V, B) ; notre modèle d'image travaille
    /// en température et teinte, qui couvrent le même espace autrement. La correspondance
    /// est donc approchée, et assumée : les touches font ce que l'opérateur attend — plus
    /// rouge, plus vert, plus bleu — sans ajouter un axe au pipeline pour trois raccourcis.
    /// </summary>
    private void Correct(Action<ImageAdjustments> reglage)
    {
        foreach (var photo in Cibles())
        {
            reglage(photo.Adjustments);
            photo.RefreshThumbnail();
        }
    }

    private const double PasLumiere = 0.15;  // en diaphragmes, comme l'exposition
    private const double PasCouleur = 8;     // sur l'échelle −100..100

    private void AttachShortcuts()
    {
        Focusable = true;

        var map = new KeyMap()
            .OnCtrl(Key.A, SelectAll)
            .On(Key.C, () => OnCropCurrent(this, new RoutedEventArgs()))
            .On([Key.Add, Key.OemPlus], () => ChangeQuantityOnTargets(1))
            .On([Key.Subtract, Key.OemMinus], () => ChangeQuantityOnTargets(-1))
            .On([Key.D0, Key.NumPad0], () => SetQuantityOnTargets(0))
            .On(Key.F, () => Correct(r => r.Exposure += PasLumiere))
            .On(Key.V, () => Correct(r => r.Exposure -= PasLumiere))
            .On(Key.G, () => Correct(r => r.Temperature = Borne(r.Temperature + PasCouleur)))
            .On(Key.B, () => Correct(r => r.Temperature = Borne(r.Temperature - PasCouleur)))
            .On(Key.H, () => Correct(r => r.Tint = Borne(r.Tint - PasCouleur)))
            .On(Key.N, () => Correct(r => r.Tint = Borne(r.Tint + PasCouleur)))
            .On(Key.J, () => Correct(r => r.Temperature = Borne(r.Temperature - PasCouleur)))
            .On(Key.M, () => Correct(r => r.Temperature = Borne(r.Temperature + PasCouleur)))
            .OnCtrl(Key.W, () => Correct(r => r.Grayscale = !r.Grayscale))
            .OnCtrl(Key.O, () => Correct(r =>
            {
                var neutre = new ImageAdjustments();
                foreach (var p in typeof(ImageAdjustments).GetProperties().Where(p => p.CanWrite))
                    p.SetValue(r, p.GetValue(neutre));
            }))
            .OnCtrl(Key.Left, () => Rotate(-1))
            .OnCtrl(Key.Right, () => Rotate(1));

        // 1 à 9 : la quantité directement, comme chez DiLand
        for (var chiffre = 1; chiffre <= 9; chiffre++)
        {
            var valeur = chiffre;
            map.On([Key.D0 + chiffre, Key.NumPad0 + chiffre], () => SetQuantityOnTargets(valeur));
        }

        map.Attach(this);
    }

    private static double Borne(double valeur) => Math.Clamp(valeur, -100, 100);

    private void Rotate(int direction)
    {
        foreach (var photo in Cibles())
        {
            photo.RotationQuarterTurns = (photo.RotationQuarterTurns + direction + 4) % 4;
            photo.RefreshThumbnail();
        }
    }

    private void OnCropCurrent(object sender, RoutedEventArgs e)
    {
        if (_photoCourante is { } photo && photo.Selected)
            EditCrop(photo, onClosed: null);
    }

    /// <summary>
    /// Passe en revue toutes les photos cochées, l'une après l'autre : c'est le
    /// « modifier tout » de DiLand. Sur une commande de vingt tirages, régler chaque
    /// cadrage en repassant par la grille serait interminable.
    /// </summary>
    /// <summary>
    /// Applique un même cadrage à toutes les photos cochées, en une fois.
    ///
    /// C'est le « modifier tout » de DiLand. On ne défile PLUS les photos une par une :
    /// le cadre de chacune se lit sur sa vignette, donc l'opérateur voit tout de suite
    /// celles qui demandent une reprise, et n'ouvre que celles-là.
    /// </summary>
    private void OnCropAll(object sender, RoutedEventArgs e)
    {
        var aRegler = _photos.Where(p => p.Selected && p.Product is not null).ToList();
        if (aRegler.Count == 0) return;

        // on règle sur la photo courante, ou à défaut la première cochée
        var modele = _photoCourante is { Product: not null } courante && courante.Selected
            ? courante
            : aRegler[0];

        var produit = modele.Product!;
        var depart = new CropEditorView.State(
            modele.Crop, modele.RotationQuarterTurns, modele.FitOverride ?? produit.DefaultFit);

        Navigator.Go(new CropEditorView(modele.Path, produit, depart, resultat =>
        {
            foreach (var photo in aRegler)
            {
                photo.Crop = resultat.Crop;
                photo.RotationQuarterTurns = resultat.RotationQuarterTurns;
                photo.FitOverride = resultat.Fit == produit.DefaultFit ? null : resultat.Fit;
                photo.RefreshThumbnail();
            }
        }), $"Recadrage appliqué à {aRegler.Count} photo(s)");
    }

    /// <summary>
    /// Corrections appliquées à toutes les photos cochées d'un coup — le geste de DiLand,
    /// avec des réglages qui vont plus loin que la luminosité et le contraste.
    ///
    /// Les réglages de départ sont ceux de la première photo cochée : rouvrir l'écran
    /// après une correction doit montrer où on en était, pas repartir de zéro.
    /// </summary>
    private void OnAdjust(object sender, RoutedEventArgs e)
    {
        var aCorriger = _photos.Where(p => p.Selected).ToList();
        if (aCorriger.Count == 0) return;

        var depart = (_photoCourante is { Selected: true } courante ? courante : aCorriger[0]).Adjustments;

        Navigator.Go(new AdjustView(
                aCorriger.Select(p => p.Path).ToList(),
                depart,
                reglages =>
                {
                    foreach (var photo in aCorriger)
                    {
                        // un exemplaire par photo : un objet partagé ferait qu'un réglage
                        // ultérieur sur l'une déborderait sur toutes les autres
                        photo.Adjustments = reglages.Clone();
                        photo.RefreshThumbnail();
                    }
                }),
            aCorriger.Count > 1 ? $"Corrections — {aCorriger.Count} photos" : "Corrections");
    }

    private void EditCrop(PhotoItem photo, Action? onClosed, string titre = "Recadrage")
    {
        if (photo.Product is not { } product) return;

        var initial = new CropEditorView.State(
            photo.Crop, photo.RotationQuarterTurns, photo.FitOverride ?? product.DefaultFit);

        Navigator.Go(new CropEditorView(photo.Path, product, initial, result =>
        {
            photo.Crop = result.Crop;
            photo.RotationQuarterTurns = result.RotationQuarterTurns;
            photo.FitOverride = result.Fit == product.DefaultFit ? null : result.Fit;
            photo.RefreshThumbnail();
            onClosed?.Invoke();
        }), titre);
    }

    private void OnPickProduct(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not PhotoItem photo) return;

        var menu = new ContextMenu();
        foreach (var product in App.Services.Catalog.Enabled)
        {
            var chosen = product;

            // un produit sans finition déclarée reste une seule entrée ; sinon une par finition
            if (product.Finishes.Count == 0)
            {
                var item = new MenuItem
                {
                    Header = $"{product.Name} — {product.Price:0.00} €",
                    FontSize = 18,
                    IsChecked = photo.Product?.Code == product.Code,
                };
                item.Click += (_, _) =>
                {
                    photo.Product = chosen;
                    photo.Finish = null;
                };
                menu.Items.Add(item);
                continue;
            }

            foreach (var finish in product.Finishes)
            {
                var chosenFinish = finish.Name;
                var item = new MenuItem
                {
                    Header = $"{product.Name} — {chosenFinish} — {product.Price:0.00} €",
                    FontSize = 18,
                    IsChecked = photo.Product?.Code == product.Code && photo.Finish == chosenFinish,
                };
                item.Click += (_, _) =>
                {
                    photo.Product = chosen;
                    photo.Finish = chosenFinish;
                };
                menu.Items.Add(item);
            }
        }
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        var selected = _photos.Where(p => p.Selected && p.Product is not null).ToList();
        if (selected.Count == 0) return;

        var services = App.Services;
        var items = selected
            .Select(p => new DraftItem(p.Path, p.Product!, p.Quantity, p.Crop,
                p.RotationQuarterTurns, p.FineRotationDegrees, p.FitOverride, p.Adjustments, null, p.Finish))
            .ToList();

        PrintButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var order = await Task.Run(() =>
            {
                var created = services.Orders.CreateOrder(
                    _commandeBorne is null ? "Operateur" : DiLandImporter.SourceName, items);

                // le lien est noté AVANT d'imprimer : si Studio est coupé en cours de
                // tirage, la commande de borne reste rattachée à sa commande Studio et
                // se referme toute seule au prochain passage dans la liste
                if (_commandeBorne is { } borne)
                    services.DiLandImport.Journal.AttachStudioOrder(borne, created.Id);

                foreach (var envelope in created.Envelopes)
                    services.Printer.PrintEnvelope(created, envelope);

                // seul un tirage réellement sorti ferme la commande de borne : une
                // enveloppe en attente d'impression manuelle (Epson) ne compte pas
                if (_commandeBorne is { } oid &&
                    created.Envelopes.All(e => e.Status == EnvelopeStatus.Printed))
                    services.DiLandImport.MarkPrinted(oid, created.Id);

                return created;
            });

            Mouse.OverrideCursor = null;
            var prints = selected.Sum(p => p.Quantity);
            MessageBox.Show(
                $"Commande {order.DisplayNumber} envoyée à l'impression.\n" +
                $"{selected.Count} photo(s), {prints} tirage(s) — total {order.Total:0.00} €",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigator.Home(new HomeView(), "Studio Photo");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Échec de l'impression (grille photos)", ex);
            MessageBox.Show($"Échec de l'impression : {ex.Message}\n\n" +
                            "La commande est visible dans « Commandes du jour » pour réimpression.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            PrintButton.IsEnabled = true;
        }
    }

    /// <summary>Une photo de la grille et, si elle est cochée, sa ligne de panier.</summary>
    internal sealed class PhotoItem : ObservableObject
    {
        private readonly Action _cartChanged;
        private ImageSource? _thumbnail;
        private bool _selected;
        private Product? _product;
        private string? _finish;
        private int _quantity = 1;

        public PhotoItem(string path, Action cartChanged)
        {
            Path = path;
            _cartChanged = cartChanged;
        }

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);

        // recadrage et réglages, renseignés par l'éditeur (CropEditorView)
        public CropSpec Crop { get; set; } = CropSpec.Full;
        public int RotationQuarterTurns { get; set; }

        /// <summary>
        /// Redressement fin, en degrés — le « Tilt » de DiLand (touche T), qu'il stocke
        /// sous <c>FineRotationAngle</c>. Distinct des quarts de tour : on redresse un
        /// horizon penché de deux degrés, on ne le fait pas basculer de quatre-vingt-dix.
        /// </summary>
        public double FineRotationDegrees { get; set; }

        /// <summary>Définition du fichier d'origine, une fois orienté.</summary>
        public (int Width, int Height) SourcePixels => (_sourceWidth, _sourceHeight);

        /// <summary>Rapport largeur/hauteur du fichier d'origine, une fois orienté.</summary>
        public double SourceAspect => _sourceHeight == 0 ? 1 : _sourceWidth / (double)_sourceHeight;
        public FitMode? FitOverride { get; set; }
        public ImageAdjustments Adjustments { get; set; } = new();

        private BitmapSource? _sourceThumbnail;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            private set => Set(ref _thumbnail, value);
        }

        public void SetSourceThumbnail(BitmapSource source)
        {
            _sourceThumbnail = source;
            RefreshThumbnail();
        }

        /// <summary>
        /// Vignette affichée = vignette source + rotation utilisateur + recadrage choisi
        /// + corrections, pour que la grille montre la photo telle qu'elle sortira.
        /// </summary>
        public void RefreshThumbnail()
        {
            if (_sourceThumbnail is null) return;

            BitmapSource display = _sourceThumbnail;
            if (RotationQuarterTurns != 0)
                display = new TransformedBitmap(display, new RotateTransform(90 * RotationQuarterTurns));

            Thumbnail = Compose(_sourceThumbnail);
        }

        /// <summary>
        /// Construit l'image affichable à partir d'une source : rotation, corrections,
        /// puis le cadre du tirage par-dessus.
        ///
        /// Isolé pour que la vignette et le grand aperçu suivent EXACTEMENT le même
        /// chemin, à la seule différence de la définition de la source — sinon l'un
        /// montrerait autre chose que l'autre.
        /// </summary>
        public ImageSource Compose(BitmapSource source)
        {
            BitmapSource display = source;
            if (RotationQuarterTurns != 0)
                display = new TransformedBitmap(display, new RotateTransform(90 * RotationQuarterTurns));

            // le redressement fin après les quarts de tour, comme au rendu final
            if (Math.Abs(FineRotationDegrees) > 0.01)
                display = RedresserFin(display, FineRotationDegrees);

            if (display.CanFreeze) display.Freeze();

            // les corrections d'abord : elles s'appliquent à la photo, pas au cadre
            var corrigee = ThumbnailAdjuster.Apply(display, Adjustments);

            // puis le cadre du tirage PAR-DESSUS la photo entière, au lieu de découper.
            // C'est la vue d'ensemble de DiLand : d'un coup d'œil sur la planche, on voit
            // ce que chaque tirage gardera ET ce qu'il coupera. Une vignette déjà rognée
            // ne dit pas ce qu'on perd, et oblige à ouvrir les photos une par une.
            return DessinerCadre(corrigee, Crop);
        }

        /// <summary>
        /// Redressement de quelques degrés, rendu à la main.
        ///
        /// PAS avec <c>TransformedBitmap</c> : WPF n'y accepte que des échelles, des
        /// retournements et des rotations à 90°, et refuse tout autre angle par une
        /// exception — « La transformation doit être une combinaison d'échelles, de
        /// retournements et de rotations à 90 degrés ». Elle remontait à chaque redessin
        /// et faisait tomber d'un coup C, C+clic droit et T+molette (01/08/2026).
        ///
        /// Le canevas est agrandi pour contenir l'image inclinée, sans quoi les coins
        /// seraient coupés au lieu d'être laissés au cadrage.
        /// </summary>
        private static BitmapSource RedresserFin(BitmapSource source, double degres)
        {
            var radians = degres * Math.PI / 180;
            double largeur = source.PixelWidth;
            double hauteur = source.PixelHeight;

            var cos = Math.Abs(Math.Cos(radians));
            var sin = Math.Abs(Math.Sin(radians));
            var largeurRendue = (int)Math.Ceiling(largeur * cos + hauteur * sin);
            var hauteurRendue = (int)Math.Ceiling(largeur * sin + hauteur * cos);

            var visuel = new DrawingVisual();
            using (var dessin = visuel.RenderOpen())
            {
                dessin.PushTransform(new TranslateTransform(largeurRendue / 2.0, hauteurRendue / 2.0));
                dessin.PushTransform(new RotateTransform(degres));
                dessin.DrawImage(source, new Rect(-largeur / 2, -hauteur / 2, largeur, hauteur));
                dessin.Pop();
                dessin.Pop();
            }

            var rendu = new RenderTargetBitmap(largeurRendue, hauteurRendue, 96, 96, PixelFormats.Pbgra32);
            rendu.Render(visuel);
            rendu.Freeze();
            return rendu;
        }

        /// <summary>Dessine le cadre jaune du tirage sur la vignette, comme DiLand.</summary>
        private static ImageSource DessinerCadre(ImageSource photo, CropSpec crop)
        {
            if (photo is not BitmapSource source) return photo;
            if (!crop.IsValid) return photo;

            var largeur = source.PixelWidth;
            var hauteur = source.PixelHeight;
            if (largeur <= 0 || hauteur <= 0) return photo;

            var visuel = new DrawingVisual();
            using (var dessin = visuel.RenderOpen())
            {
                dessin.DrawImage(source, new Rect(0, 0, largeur, hauteur));

                var cadre = new Rect(
                    crop.X * largeur, crop.Y * hauteur,
                    crop.Width * largeur, crop.Height * hauteur);

                // voile sur ce qui sera coupé : le cadre seul se perd sur une photo claire
                var voile = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
                var dehors = new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, largeur, hauteur)),
                    new RectangleGeometry(cadre));
                dessin.DrawGeometry(voile, null, dehors);

                var trait = new Pen(Brushes.Yellow, Math.Max(2, largeur / 90.0));
                trait.Freeze();
                dessin.DrawRectangle(null, trait, cadre);
            }

            var rendu = new RenderTargetBitmap(largeur, hauteur, 96, 96, PixelFormats.Pbgra32);
            rendu.Render(visuel);
            rendu.Freeze();
            return rendu;
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                OnPropertyChanged(nameof(BorderBrush));
                OnPropertyChanged(nameof(TileBrush));
                OnPropertyChanged(nameof(CheckVisibility));
                OnPropertyChanged(nameof(CartVisibility));
                _cartChanged();
            }
        }

        public Product? Product
        {
            get => _product;
            set
            {
                if (_product?.Code == value?.Code) return;
                _product = value;
                OnPropertyChanged(nameof(ProductLabel));
                _cartChanged();
            }
        }

        /// <summary>Finition choisie (voir Product.Finishes) ; null = DEVMODE par défaut du produit.</summary>
        public string? Finish
        {
            get => _finish;
            set
            {
                if (!Set(ref _finish, value)) return;
                OnPropertyChanged(nameof(ProductLabel));
            }
        }

        public int Quantity
        {
            get => _quantity;
            set
            {
                if (!Set(ref _quantity, value)) return;
                OnPropertyChanged(nameof(QuantityLabel));
                _cartChanged();
            }
        }

        public string ProductLabel => _product is null
            ? "Produit…"
            : $"{_product.Name}{(_finish is null ? "" : $" · {_finish}")} · {_product.Price:0.00} €";
        public string QuantityLabel => _quantity.ToString();

        public Brush BorderBrush => Selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;

        /// <summary>
        /// Fond de la vignette : orange dès qu'elle est retenue, comme chez DiLand. Le
        /// choix saute aux yeux d'un bout à l'autre de la planche, ce qu'un liseré fin
        /// ne permet pas — c'est ce qui compte quand on sert un client au comptoir.
        /// </summary>
        public Brush TileBrush => Selected
            ? (Brush)Application.Current.Resources["TitleBrush"]
            : (Brush)Application.Current.Resources["PanelBrush"];

        private int _sourceWidth;
        private int _sourceHeight;

        /// <summary>Définition du fichier d'origine, notée à la lecture de la vignette.</summary>
        public void SetSourceSize(int width, int height)
        {
            _sourceWidth = width;
            _sourceHeight = height;
            OnPropertyChanged(nameof(SizeLabel));
            OnPropertyChanged(nameof(RatioLabel));
            OnPropertyChanged(nameof(BadgeVisibility));
        }

        public string SizeLabel => _sourceWidth == 0 ? "" : $"{_sourceWidth} x {_sourceHeight}";

        /// <summary>Rapport du plus grand côté au plus petit, comme l'affiche DiLand (« 2.3 »).</summary>
        public string RatioLabel
        {
            get
            {
                if (_sourceWidth == 0 || _sourceHeight == 0) return "";
                var grand = Math.Max(_sourceWidth, _sourceHeight);
                var petit = Math.Min(_sourceWidth, _sourceHeight);
                return (grand / (double)petit).ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public Visibility BadgeVisibility => _sourceWidth == 0 ? Visibility.Collapsed : Visibility.Visible;

        public Visibility CheckVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CartVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
    }
}
