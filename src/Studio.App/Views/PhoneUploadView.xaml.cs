using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Studio.App.Infrastructure;
using Studio.Web;

namespace Studio.App.Views;

/// <summary>
/// Session « téléphone » : QR à scanner, compteur en direct des photos reçues,
/// puis ouverture de la grille d'impression sur le dossier de la session.
/// </summary>
public partial class PhoneUploadView : UserControl
{
    private UploadSession? _session;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public PhoneUploadView()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => RefreshCount();
        Loaded += async (_, _) => await StartSessionAsync();
        Unloaded += (_, _) => _poll.Stop();
    }

    private async Task StartSessionAsync()
    {
        if (_session is not null)
        {
            _poll.Start();
            return;
        }

        try
        {
            await App.Services.EnsureUploadServerAsync();
            var (session, url) = App.Services.Upload.CreateSession();
            _session = session;

            using var stream = new MemoryStream(QrPng.For(url));
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            QrImage.Source = bitmap;
            UrlText.Text = url;

            _poll.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible de démarrer le serveur d'envoi : {ex.Message}\n\n" +
                "Vérifiez qu'aucune autre application n'utilise le port 8123.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            Navigator.Back();
        }
    }

    private void RefreshCount()
    {
        if (_session is null) return;
        int count;
        try
        {
            count = Directory.EnumerateFiles(_session.Folder).Count();
        }
        catch (IOException)
        {
            return;
        }
        CountText.Text = count == 0
            ? "En attente de photos…"
            : $"{count} photo{(count > 1 ? "s" : "")} reçue{(count > 1 ? "s" : "")} ✓";
        OpenButton.IsEnabled = count > 0;
    }

    private void OnOpenPhotos(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        _poll.Stop();
        Navigator.Go(new PhotoGridView(_session.Folder), "Photos du téléphone");
    }
}
