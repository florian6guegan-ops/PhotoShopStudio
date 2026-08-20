using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Store;

namespace Studio.App.Views;

/// <summary>
/// Les photos d'identité faites ces trente derniers jours — imprimées ou envoyées — et le
/// moyen de les rouvrir telles qu'elles étaient.
///
/// <b>Une tuile touchée rouvre la photo SUR L'ÉCRAN DE TRAVAIL</b>, pas dans une visionneuse.
/// C'est la règle posée avec l'exploitant le 14/08 : tout y est — réimprimer, envoyer par
/// courriel, changer de pays ou de format —, et non un écran séparé aux fonctions limitées.
/// La photo revient avec son cadrage, ses repères de crâne et de menton, son fond blanc et
/// ses corrections : il n'y a rien à remettre.
///
/// L'écran ne DÉCIDE de rien : il liste ce que <see cref="HistoriqueIdentite"/> garde, et la
/// purge des trente jours se fait à cette lecture-là.
/// </summary>
public partial class IdHistoriqueView : UserControl
{
    private readonly List<Tuile> _tuiles = [];
    private CancellationTokenSource? _chargement;

    public IdHistoriqueView()
    {
        InitializeComponent();

        Loaded += async (_, _) => await ChargerAsync();
        Unloaded += (_, _) => _chargement?.Cancel();
    }

    private async Task ChargerAsync()
    {
        _chargement?.Cancel();
        _chargement = new CancellationTokenSource();
        var ct = _chargement.Token;

        if (_tuiles.Count == 0)
        {
            // la lecture purge ce qui a plus de trente jours : elle ne se fait pas sur le
            // fil de l'interface, un dossier de plusieurs centaines d'entrées y figerait
            // l'écran le temps de les relire
            var faites = await Task.Run(() => App.Services.HistoriqueIdentite.Lister(), ct);
            if (ct.IsCancellationRequested) return;

            foreach (var photo in faites) _tuiles.Add(new Tuile(photo));

            PhotosGrid.ItemsSource = _tuiles;
        }

        VideText.Visibility = _tuiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = _tuiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        CountText.Text = _tuiles.Count switch
        {
            0 => "",
            1 => "1 photo",
            _ => $"{_tuiles.Count} photos",
        };

        await ChargerLesVignettesAsync(ct);
    }

    /// <summary>
    /// Remplit la planche par tranches et en parallèle — même mécanique que le choix des
    /// photos, pour la même raison : lues une par une, elles arrivent ligne à ligne sur un
    /// seul cœur.
    /// </summary>
    private async Task ChargerLesVignettesAsync(CancellationToken ct)
    {
        var vignettes = App.Services.Thumbnails;
        var aLire = _tuiles.Where(t => t.Thumbnail is null && !t.Perdue).ToList();
        if (aLire.Count == 0) return;

        var tranche = Math.Max(8, Environment.ProcessorCount * 2);

        for (var debut = 0; debut < aLire.Count; debut += tranche)
        {
            if (ct.IsCancellationRequested) return;

            var lot = aLire.GetRange(debut, Math.Min(tranche, aLire.Count - debut));
            var lues = new byte[lot.Count][];

            try
            {
                await Task.Run(() => Parallel.For(0, lot.Count,
                    new ParallelOptions { CancellationToken = ct }, i =>
                    {
                        try
                        {
                            lues[i] = vignettes.GetJpeg(lot[i].Chemin, 360);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // une vignette illisible laisse la tuile vide ; l'heure et le
                            // format restent, et la photo reste ouvrable
                            FileLog.Write($"Historique identité : vignette illisible — {lot[i].Chemin}", ex);
                        }
                    }), ct);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            for (var i = 0; i < lot.Count; i++)
                if (lues[i] is { } octets) lot[i].Thumbnail = ToBitmap(octets);
        }
    }

    private static BitmapImage ToBitmap(byte[] jpeg)
    {
        using var flux = new MemoryStream(jpeg);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = flux;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Rouvre la photo sur l'écran de travail, telle qu'elle était.
    ///
    /// ⚠ <b>Avec un identifiant NEUF.</b> L'entrée de l'historique ne désigne aucune planche
    /// mise de côté ; rouvrir une photo faite ne doit en effacer ni en modifier aucune. C'est
    /// la règle de <see cref="TravailDepuisCommande"/>, et pour la même raison.
    /// </summary>
    private void OnPhotoClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not Tuile tuile) return;

        if (tuile.Perdue)
        {
            MessageBox.Show(
                $"Le fichier de cette photo n'est plus sur le disque :\n{tuile.Chemin}\n\n" +
                "Les photos des clients sont effacées au bout de trente jours, et la fiche " +
                "part avec elles au prochain passage.",
                "Photos récentes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var travail = tuile.Photo.Travail;
        travail.Id = Guid.NewGuid();

        if (travail.Identite is null)
        {
            // une fiche sans planche ne sait pas s'ouvrir : elle ne devrait pas exister,
            // mais un fichier écrit par une version plus ancienne pourrait
            FileLog.Write($"Historique identité : fiche sans planche — {tuile.Chemin}");
            return;
        }

        Navigator.Go(new IdPhotoView(travail),
            $"{tuile.Photo.NomDuFichier} — {tuile.Photo.Quand}");
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    /// <summary>Une photo faite, telle que la planche l'affiche.</summary>
    private sealed class Tuile : ObservableObject
    {
        private ImageSource? _thumbnail;

        public Tuile(PhotoFaite photo)
        {
            Photo = photo;

            // relu UNE fois, à la construction : le lier à la tuile ferait un accès disque
            // par redessin, sur une planche qui défile
            Perdue = !File.Exists(photo.Chemin);
        }

        public PhotoFaite Photo { get; }

        public string Chemin => Photo.Chemin;
        public string Quand => Photo.Quand;
        public string Resume => Photo.Resume;
        public string Pastille => Photo.Pastille;

        /// <summary>Le fichier n'est plus là : la tuile le dit et ne s'ouvre pas.</summary>
        public bool Perdue { get; }

        public Visibility PerdueVisibility => Perdue ? Visibility.Visible : Visibility.Collapsed;

        public double Opacite => Perdue ? 0.55 : 1.0;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set => Set(ref _thumbnail, value);
        }
    }
}
