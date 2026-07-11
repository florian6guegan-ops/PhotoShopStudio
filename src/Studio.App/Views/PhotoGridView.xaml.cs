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

    private async Task ScanAndLoadAsync()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        if (_photos.Count == 0)
        {
            var files = await Task.Run(() => FindImages(_rootPath), ct);
            foreach (var file in files)
                _photos.Add(new PhotoItem(file, OnSelectionChanged));
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
                photo.Thumbnail = ToBitmap(bytes);
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
        if ((sender as Border)?.Tag is PhotoItem photo)
            photo.Selected = !photo.Selected;
    }

    private void OnSelectionChanged()
    {
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = _photos.Count(p => p.Selected);
        CountText.Text = count == 0
            ? $"{_photos.Count} photos trouvées"
            : $"{count} sélectionnée{(count > 1 ? "s" : "")} sur {_photos.Count}";
        PrintButton.IsEnabled = count > 0;
    }

    private void OnQuantityMinus(object sender, RoutedEventArgs e) => SetQuantity(_quantity - 1);
    private void OnQuantityPlus(object sender, RoutedEventArgs e) => SetQuantity(_quantity + 1);

    private void SetQuantity(int value)
    {
        _quantity = Math.Clamp(value, 1, 99);
        QuantityText.Text = _quantity.ToString();
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        var selected = _photos.Where(p => p.Selected).ToList();
        if (selected.Count == 0 || ProductCombo.SelectedItem is not ProductChoice choice) return;

        var services = App.Services;
        var quantity = _quantity;
        var items = selected
            .Select(p => new DraftItem(p.Path, choice.Product, quantity, CropSpec.Full, 0, null, new ImageAdjustments()))
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
            MessageBox.Show(
                $"Commande {order.DisplayNumber} envoyée à l'impression.\n" +
                $"{selected.Count} photo(s) × {quantity} — total {order.Total:0.00} €",
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

    private sealed class PhotoItem : ObservableObject
    {
        private readonly Action _selectionChanged;
        private ImageSource? _thumbnail;
        private bool _selected;

        public PhotoItem(string path, Action selectionChanged)
        {
            Path = path;
            _selectionChanged = selectionChanged;
        }

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
                OnPropertyChanged(nameof(CheckVisibility));
                _selectionChanged();
            }
        }

        public Brush BorderBrush => Selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;

        public Visibility CheckVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
    }
}
