using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Sources;

namespace Studio.App.Views;

/// <summary>Sélection borne : cocher des photos et leur nombre d'exemplaires, rien d'autre.</summary>
public partial class KioskGridView : UserControl
{
    private readonly List<KioskPhoto> _photos = new();
    private readonly string _rootPath;
    private CancellationTokenSource? _thumbnailCts;

    public KioskGridView(string rootPath)
    {
        _rootPath = rootPath;
        InitializeComponent();
        Loaded += async (_, _) => await ScanAndLoadAsync();
        Unloaded += (_, _) => _thumbnailCts?.Cancel();
    }

    private async Task ScanAndLoadAsync()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        if (_photos.Count == 0)
        {
            var files = await Task.Run(() => PhotoScanner.Scan(_rootPath), ct);
            foreach (var file in files)
                _photos.Add(new KioskPhoto(file, UpdateSummary));
            PhotosGrid.ItemsSource = _photos;
            UpdateSummary();
        }

        var thumbnails = App.Services.Thumbnails;
        foreach (var photo in _photos)
        {
            if (ct.IsCancellationRequested) return;
            if (photo.Thumbnail is not null) continue;
            try
            {
                var bytes = await Task.Run(() => thumbnails.GetJpeg(photo.Path), ct);
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                photo.Thumbnail = bitmap;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
        }
    }

    private void OnPhotoClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is KioskPhoto photo)
            photo.Selected = !photo.Selected;
    }

    private void OnTileMinus(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is KioskPhoto photo)
            photo.Quantity = Math.Clamp(photo.Quantity - 1, 1, 99);
    }

    private void OnTilePlus(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is KioskPhoto photo)
            photo.Quantity = Math.Clamp(photo.Quantity + 1, 1, 99);
    }

    private void UpdateSummary()
    {
        var selected = _photos.Where(p => p.Selected).ToList();
        var prints = selected.Sum(p => p.Quantity);
        CountText.Text = selected.Count == 0
            ? $"{_photos.Count} photos trouvées"
            : $"{selected.Count} photo(s), {prints} tirage(s)";
        ContinueButton.IsEnabled = selected.Count > 0;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        var selection = _photos.Where(p => p.Selected)
            .Select(p => new KioskSelection(p.Path, p.Quantity))
            .ToList();
        Navigator.Go(new KioskCheckoutView(selection), "Choisissez le format");
    }

    internal sealed class KioskPhoto : ObservableObject
    {
        private readonly Action _changed;
        private ImageSource? _thumbnail;
        private bool _selected;
        private int _quantity = 1;

        public KioskPhoto(string path, Action changed)
        {
            Path = path;
            _changed = changed;
        }

        public string Path { get; }

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
                _changed();
            }
        }

        public int Quantity
        {
            get => _quantity;
            set
            {
                if (!Set(ref _quantity, value)) return;
                OnPropertyChanged(nameof(QuantityLabel));
                _changed();
            }
        }

        public string QuantityLabel => _quantity.ToString();

        public Brush BorderBrush => Selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;

        public Visibility CheckVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Une photo retenue par le client, avec son nombre d'exemplaires.</summary>
public sealed record KioskSelection(string Path, int Quantity);
