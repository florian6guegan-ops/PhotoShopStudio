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

    /// <summary>Une photo reçue, telle que la bande d'aperçu la montre.</summary>
    private sealed record Recue(string Chemin, string Nom, BitmapImage Vignette);

    /// <summary>
    /// Ce qui est déjà dans la bande, par chemin — pour ne relire que les NOUVEAUX fichiers.
    /// Sans cela, chaque battement du minuteur redécoderait toute la série, et une session
    /// d'une cinquantaine de photos passerait son temps à refabriquer des vignettes.
    /// </summary>
    private readonly HashSet<string> _connues = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Recue> _recues = [];

    /// <summary>Un aperçu ne demande pas la pleine définition — et le fichier vient du réseau.</summary>
    private const int VignettePx = 240;

    private bool _lectureEnCours;

    private async void RefreshCount()
    {
        if (_session is null) return;

        List<string> fichiers;
        try
        {
            fichiers = Directory.EnumerateFiles(_session.Folder).OrderBy(f => f).ToList();
        }
        catch (IOException)
        {
            return;
        }

        var count = fichiers.Count;
        CountText.Text = count == 0
            ? "En attente de photos…"
            : $"{count} photo{(count > 1 ? "s" : "")} reçue{(count > 1 ? "s" : "")} ✓";

        OpenButton.IsEnabled = count > 0;
        SaveButton.IsEnabled = count > 0;

        await AjouterLesNouvellesAsync(fichiers);
    }

    /// <summary>
    /// Fabrique les vignettes des fichiers qui viennent d'arriver.
    ///
    /// <b>Un seul passage à la fois</b> : le minuteur bat toutes les 1,5 s, et un téléphone
    /// qui envoie vingt photos d'un coup ferait démarrer la lecture suivante avant la fin
    /// de la précédente — les mêmes fichiers seraient décodés deux fois et la bande aurait
    /// des doublons.
    ///
    /// <b>Le décodage est hors du fil d'interface</b> : une photo de téléphone fait douze
    /// mégapixels, et l'écran doit rester vivant pendant que le client envoie la suite.
    /// </summary>
    private async Task AjouterLesNouvellesAsync(IReadOnlyList<string> fichiers)
    {
        if (_lectureEnCours) return;

        var nouvelles = fichiers.Where(f => !_connues.Contains(f)).ToList();
        if (nouvelles.Count == 0) return;

        _lectureEnCours = true;
        try
        {
            foreach (var chemin in nouvelles)
            {
                // marqué AVANT la lecture : un fichier illisible ne doit pas être repris à
                // chaque battement — le téléphone peut l'avoir laissé à moitié écrit
                _connues.Add(chemin);

                var vignette = await Task.Run(() => LireLaVignette(chemin));
                if (vignette is null) continue;

                _recues.Add(new Recue(chemin, Path.GetFileName(chemin), vignette));
            }

            ApercuList.ItemsSource = null;
            ApercuList.ItemsSource = _recues;
            ApercuScroll.Visibility = _recues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _lectureEnCours = false;
        }
    }

    /// <summary>La vignette d'un fichier, ou null s'il n'est pas (encore) lisible.</summary>
    private static BitmapImage? LireLaVignette(string chemin)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // le fichier est relâché aussitôt
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = VignettePx;
            image.UriSource = new Uri(chemin);
            image.EndInit();
            image.Freeze();                                 // passe le fil d'interface
            return image;
        }
        catch (Exception)
        {
            // envoi encore en cours, format que WPF ne décode pas (HEIC brut) : la photo
            // reste comptée et ouvrable, elle n'a simplement pas d'aperçu.
            return null;
        }
    }

    /// <summary>
    /// Copie les photos reçues dans un dossier choisi par l'opérateur — la clef USB du
    /// client, le plus souvent.
    ///
    /// <b>Rien n'est déplacé ni effacé</b> : la session garde les siennes, et l'opérateur
    /// peut enregistrer PUIS imprimer. Un nom déjà pris est suffixé plutôt qu'écrasé — deux
    /// téléphones sortent volontiers un « IMG_0001.JPG » chacun.
    /// </summary>
    private async void OnSavePhotos(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        List<string> fichiers;
        try
        {
            fichiers = Directory.EnumerateFiles(_session.Folder).OrderBy(f => f).ToList();
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Les photos reçues sont illisibles : {ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (fichiers.Count == 0) return;

        var boite = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Où enregistrer les {fichiers.Count} photo(s) ?",
        };

        if (boite.ShowDialog() != true) return;

        var destination = boite.FolderName;
        SaveButton.IsEnabled = false;

        try
        {
            var copiees = await Task.Run(() => Copier(fichiers, destination));

            FileLog.Write($"Photos du téléphone enregistrées : {copiees} fichier(s) vers {destination}");
            MessageBox.Show($"{copiees} photo(s) enregistrée(s) dans :\n{destination}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            FileLog.Write("Enregistrement des photos du téléphone impossible", ex);
            MessageBox.Show(
                $"Les photos n'ont pas pu être enregistrées : {ex.Message}\n\n" +
                "Elles restent disponibles depuis cet écran.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private static int Copier(IReadOnlyList<string> fichiers, string destination)
    {
        Directory.CreateDirectory(destination);

        var copiees = 0;
        foreach (var source in fichiers)
        {
            File.Copy(source, CheminLibre(destination, Path.GetFileName(source)));
            copiees++;
        }

        return copiees;
    }

    /// <summary>Le chemin demandé, ou le même suffixé « (2) », « (3) »… s'il est déjà pris.</summary>
    private static string CheminLibre(string dossier, string nom)
    {
        var candidat = Path.Combine(dossier, nom);
        if (!File.Exists(candidat)) return candidat;

        var racine = Path.GetFileNameWithoutExtension(nom);
        var extension = Path.GetExtension(nom);

        for (var n = 2; ; n++)
        {
            candidat = Path.Combine(dossier, $"{racine} ({n}){extension}");
            if (!File.Exists(candidat)) return candidat;
        }
    }

    private void OnOpenPhotos(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        _poll.Stop();
        Navigator.Go(new PhotoGridView(_session.Folder), "Photos du téléphone");
    }
}
