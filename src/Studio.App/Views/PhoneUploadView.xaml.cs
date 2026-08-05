using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Studio.App.Infrastructure;
using Studio.Core;
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

            QrImage.Source = EnImage(QrPng.For(url));
            UrlText.Text = url;

            _poll.Start();

            // le WiFi vient après : il interroge netsh, l'écran ne doit pas l'attendre
            await AfficherLeWifiAsync();
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

    /// <summary>
    /// Le code de connexion au réseau, quand ce poste en connaît un.
    ///
    /// <b>Sans profil lisible, il n'y a pas de message d'erreur</b> : la colonne disparaît et
    /// le code d'envoi reprend son titre sans numéro. Un poste en Ethernet est un cas normal,
    /// pas une panne — et l'opérateur ne peut rien y faire depuis cet écran.
    /// </summary>
    private async Task AfficherLeWifiAsync()
    {
        // config/wifi.json d'abord : le poste de l'atelier n'a pas de carte sans fil, donc
        // aucun profil que Windows saurait rendre. La lecture automatique ne sert que sur
        // un portable, et netsh démarre deux processus — hors du fil d'interface.
        var reseau = App.Services.Wifi.Network() ?? await Task.Run(WifiQr.Current);
        if (reseau is null) return;

        try
        {
            WifiQrImage.Source = EnImage(WifiQr.Png(reseau));
        }
        catch (Exception ex)
        {
            FileLog.Write($"Code QR WiFi impossible à produire pour « {reseau.Ssid} »", ex);
            return;
        }

        WifiSsidText.Text = reseau.Ssid;
        WifiPanel.Visibility = Visibility.Visible;
        UploadStepText.Text = "2.  Envoyer les photos";
    }

    /// <summary>Un PNG en mémoire, gelé : ces codes ne changent plus une fois affichés.</summary>
    private static BitmapImage EnImage(byte[] png)
    {
        using var flux = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = flux;
        image.EndInit();
        image.Freeze();
        return image;
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
