using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Printing.Devices.Fuji;
using Studio.Store;

namespace Studio.App.Views;

public partial class PhotoGridView : UserControl
{
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".bmp", ".tif", ".tiff", ".webp" };

    private readonly string _rootPath;
    private readonly List<PhotoItem> _photos = new();
    private CancellationTokenSource? _thumbnailCts;
    private int _quantity = 1;

    /// <param name="rootPath">Dossier des photos à proposer.</param>
    /// <param name="produitParDefaut">
    /// Format déjà choisi en amont, comme dans le parcours de DiLand. Vide = premier
    /// produit du catalogue, l'opérateur choisira dans la liste.
    /// </param>
    public PhotoGridView(string rootPath, string? produitParDefaut = null)
    {
        _rootPath = rootPath;
        InitializeComponent();

        var choix = App.Services.Catalog.Enabled.Select(p => new ProductChoice(p)).ToList();
        ProductCombo.ItemsSource = choix;

        var prechoisi = produitParDefaut is null
            ? -1
            : choix.FindIndex(c => c.Product.Code.Equals(produitParDefaut, StringComparison.OrdinalIgnoreCase));
        ProductCombo.SelectedIndex = prechoisi >= 0 ? prechoisi : 0;

        Loaded += async (_, _) =>
        {
            await ScanAndLoadAsync();
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
        if (!photo.Selected && photo.Product is null)
        {
            // première sélection : la photo prend le produit et la quantité du bandeau
            photo.Product = DefaultProduct;
            photo.Quantity = _quantity;
        }
        photo.Selected = !photo.Selected;

        // cocher désigne la photo pour le bouton « Recadrer » ; décocher ne la désigne pas
        if (photo.Selected) _photoCourante = photo;
        else if (ReferenceEquals(_photoCourante, photo)) _photoCourante = null;
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

        var courante = _photoCourante is { Selected: true, Product: not null };
        CropButton.IsEnabled = courante;
        CropButton.Content = courante ? $"Recadrer {_photoCourante!.Name}" : "Recadrer";
        CropAllButton.IsEnabled = selected.Count(p => p.Product is not null) > 0;
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
    private void OnCropAll(object sender, RoutedEventArgs e)
    {
        var aRegler = _photos.Where(p => p.Selected && p.Product is not null).ToList();
        if (aRegler.Count == 0) return;

        EditSequence(aRegler, 0);
    }

    private void EditSequence(List<PhotoItem> photos, int index)
    {
        if (index >= photos.Count) return;

        EditCrop(photos[index],
            titre: $"Recadrage {index + 1} sur {photos.Count}",
            // l'éditeur se referme juste après son rappel : on enchaîne au tour d'après
            onClosed: () => Dispatcher.BeginInvoke(() => EditSequence(photos, index + 1)));
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
                p.RotationQuarterTurns, p.FitOverride, p.Adjustments, null, p.Finish))
            .ToList();

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
    private sealed class PhotoItem : ObservableObject
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

        /// <summary>Vignette affichée = vignette source + rotation utilisateur + recadrage choisi.</summary>
        public void RefreshThumbnail()
        {
            if (_sourceThumbnail is null) return;

            BitmapSource display = _sourceThumbnail;
            if (RotationQuarterTurns != 0)
                display = new TransformedBitmap(display, new RotateTransform(90 * RotationQuarterTurns));

            if (!Crop.IsFull && Crop.IsValid)
            {
                var x = (int)Math.Round(Crop.X * display.PixelWidth);
                var y = (int)Math.Round(Crop.Y * display.PixelHeight);
                var w = Math.Clamp((int)Math.Round(Crop.Width * display.PixelWidth), 1, display.PixelWidth - x);
                var h = Math.Clamp((int)Math.Round(Crop.Height * display.PixelHeight), 1, display.PixelHeight - y);
                display = new CroppedBitmap(display, new Int32Rect(x, y, w, h));
            }

            if (display.CanFreeze) display.Freeze();
            Thumbnail = display;
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                OnPropertyChanged(nameof(BorderBrush));
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

        public Visibility CheckVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CartVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
    }
}
