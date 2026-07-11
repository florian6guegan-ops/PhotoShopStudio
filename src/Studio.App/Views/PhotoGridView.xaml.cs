using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
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

    public PhotoGridView(string rootPath)
    {
        _rootPath = rootPath;
        InitializeComponent();

        ProductCombo.ItemsSource = App.Services.Catalog.Enabled
            .Select(p => new ProductChoice(p))
            .ToList();
        ProductCombo.SelectedIndex = 0;

        Loaded += async (_, _) => await ScanAndLoadAsync();
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
    }

    private void OnCartChanged()
    {
        UpdateSummary();
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

    private void OnEditCrop(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PhotoItem photo || photo.Product is null) return;

        var product = photo.Product;
        var initial = new CropEditorView.State(
            photo.Crop, photo.RotationQuarterTurns, photo.FitOverride ?? product.DefaultFit);

        Navigator.Go(new CropEditorView(photo.Path, product, initial, result =>
        {
            photo.Crop = result.Crop;
            photo.RotationQuarterTurns = result.RotationQuarterTurns;
            photo.FitOverride = result.Fit == product.DefaultFit ? null : result.Fit;
            photo.RefreshThumbnail();
        }), "Recadrage");
    }

    private void OnPickProduct(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not PhotoItem photo) return;

        var menu = new ContextMenu();
        foreach (var product in App.Services.Catalog.Enabled)
        {
            var item = new MenuItem
            {
                Header = $"{product.Name} — {product.Price:0.00} €",
                FontSize = 18,
                IsChecked = photo.Product?.Code == product.Code,
            };
            var chosen = product;
            item.Click += (_, _) => photo.Product = chosen;
            menu.Items.Add(item);
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
                p.RotationQuarterTurns, p.FitOverride, p.Adjustments))
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

        public string ProductLabel => _product is null ? "Produit…" : $"{_product.Name} · {_product.Price:0.00} €";
        public string QuantityLabel => _quantity.ToString();

        public Brush BorderBrush => Selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;

        public Visibility CheckVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CartVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
    }
}
