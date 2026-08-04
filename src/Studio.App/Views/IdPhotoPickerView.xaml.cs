using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Imaging.Geometry;
using Studio.Sources;

namespace Studio.App.Views;

/// <summary>
/// Premier écran du parcours identité : le choix des photos, PLUSIEURS à la fois.
///
/// L'écran de cadrage montrait toutes les photos du dossier dans une bande latérale et
/// n'en traitait qu'une. Une carte mémoire en porte quatre-vingts : l'opérateur cherchait
/// la bonne dans une bande de 240 pixels de large, et pour deux personnes de la même
/// famille il fallait tout recommencer, commande comprise. C'est le parcours de DiLand,
/// que les opérateurs connaissent : on choisit, on cadre, on récapitule.
///
/// Les photos retenues gardent leur ORDRE DE CLIC — c'est l'ordre dans lequel elles
/// seront cadrées, puis imprimées, et l'opérateur les désigne par leur rang.
/// </summary>
public partial class IdPhotoPickerView : UserControl
{
    private readonly string _rootPath;
    private readonly bool _avecSousDossiers;
    private readonly IdDocumentSpec _document;
    private readonly List<Vignette> _photos = [];
    private CancellationTokenSource? _chargement;

    /// <summary>Faux = classement par date (le défaut), vrai = par nom.</summary>
    private bool _parNom;

    /// <param name="rootPath">Dossier des photos.</param>
    /// <param name="document">Norme visée ; null = norme française.</param>
    /// <param name="avecSousDossiers">Descendre ou non sous <paramref name="rootPath"/>.</param>
    public IdPhotoPickerView(string rootPath, IdDocumentSpec? document = null,
        bool avecSousDossiers = true)
    {
        _rootPath = rootPath;
        _avecSousDossiers = avecSousDossiers;
        _document = document ?? IdDocumentSpec.France;

        InitializeComponent();

        TitleText.Text = _document.Country == "France"
            ? $"Sélectionnez les photos — identité {_document.WidthMm:0.#}×{_document.HeightMm:0.#}"
            : $"Sélectionnez les photos — {_document.Country}, {_document.Document}";

        Loaded += async (_, _) => await ChargerAsync();
        Unloaded += (_, _) => _chargement?.Cancel();
    }

    private async Task ChargerAsync()
    {
        _chargement?.Cancel();
        _chargement = new CancellationTokenSource();
        var ct = _chargement.Token;

        if (_photos.Count == 0)
        {
            // La plus récente en premier : la photo d'identité qu'on vient de prendre est
            // en bout de carte. Les PDF sont écartés — on ne fait pas une photo d'identité
            // depuis un document, et la détection de visage n'aurait rien à y chercher.
            var fichiers = await Task.Run(
                () => PhotoScanner.TrierParDateDecroissante(
                    PhotoScanner.Scan(_rootPath, _avecSousDossiers, PhotoScanner.MaxAffichable, ct)
                        .Where(f => !PhotoScanner.IsPdf(f))),
                ct);

            foreach (var fichier in fichiers)
                _photos.Add(new Vignette(fichier));

            PhotosGrid.ItemsSource = _photos;

            if (_photos.Count == 0)
                HintText.Text = "Aucune photo dans ce dossier — revenez en arrière pour en choisir un autre.";
        }

        MettreAJourLeCompte();

        var vignettes = App.Services.Thumbnails;
        foreach (var photo in _photos)
        {
            if (ct.IsCancellationRequested) return;
            if (photo.Thumbnail is not null) continue;

            try
            {
                var octets = await Task.Run(() => vignettes.GetJpeg(photo.Path, 360), ct);
                photo.Thumbnail = ToBitmap(octets);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* une vignette illisible laisse la tuile vide, le nom reste */ }
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

    private void OnPhotoClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not Vignette photo) return;

        photo.Choisie = !photo.Choisie;
        MettreAJourLeCompte();
    }

    private void OnTout(object sender, RoutedEventArgs e)
    {
        foreach (var photo in _photos) photo.Choisie = true;
        MettreAJourLeCompte();
    }

    private void OnAucun(object sender, RoutedEventArgs e)
    {
        foreach (var photo in _photos) photo.Choisie = false;
        MettreAJourLeCompte();
    }

    /// <summary>
    /// Bascule entre le classement par date et par nom, comme la grille des tirages.
    /// La SÉLECTION survit au tri : elle porte sur des photos, pas sur des rangs.
    /// </summary>
    private void OnTrier(object sender, RoutedEventArgs e)
    {
        _parNom = !_parNom;

        var ordonnees = _parNom
            ? _photos.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : PhotoScanner.TrierParDateDecroissante(_photos.Select(p => p.Path))
                .Select(chemin => _photos.First(p => p.Path == chemin))
                .ToList();

        _photos.Clear();
        _photos.AddRange(ordonnees);

        // liste ordinaire et non ObservableCollection : sans cette relance, rien ne bouge
        PhotosGrid.ItemsSource = null;
        PhotosGrid.ItemsSource = _photos;

        MettreAJourLeCompte();
    }

    /// <summary>
    /// Renumérote les pastilles et met à jour le compte. Les rangs suivent l'ORDRE DE
    /// CLIC : décocher la deuxième de trois doit ramener la troisième au rang 2, sinon
    /// « la troisième » ne désigne plus rien.
    /// </summary>
    private void MettreAJourLeCompte()
    {
        var choisies = _photos.Where(p => p.Choisie).ToList();

        for (var i = 0; i < choisies.Count; i++)
            choisies[i].Rang = i + 1;

        CountText.Text = choisies.Count switch
        {
            0 => "aucune photo sélectionnée",
            1 => "1 sélectionnée",
            _ => $"{choisies.Count} sélectionnées",
        };

        SuivantButton.IsEnabled = choisies.Count > 0;
    }

    /// <summary>
    /// Les photos retenues, dans l'ordre où l'opérateur les a cochées.
    ///
    /// C'est cet ordre qu'on garde jusqu'au bout : celui du cadrage, celui des planches, et
    /// celui des pastilles. Reprendre l'ordre de la grille ferait changer les numéros entre
    /// deux écrans.
    /// </summary>
    private List<string> CheminsRetenus() =>
        _photos.Where(p => p.Choisie)
            .OrderBy(p => p.Rang)
            .Select(p => p.Path)
            .ToList();

    private void OnSuivant(object sender, RoutedEventArgs e)
    {
        var chemins = CheminsRetenus();
        if (chemins.Count == 0) return;

        Navigator.Go(new IdPhotoView(chemins, _document),
            $"{_document.Country} — cadrer {chemins.Count} photo(s)");
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnAnnuler(object sender, RoutedEventArgs e) =>
        Navigator.Home(new HomeView(), "Studio Photo");

    private sealed class Vignette : ObservableObject
    {
        private ImageSource? _thumbnail;
        private bool _choisie;
        private int _rang;

        public Vignette(string path) => Path = path;

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set => Set(ref _thumbnail, value);
        }

        public bool Choisie
        {
            get => _choisie;
            set
            {
                if (!Set(ref _choisie, value)) return;
                OnPropertyChanged(nameof(BorderBrush));
                OnPropertyChanged(nameof(FondBrush));
                OnPropertyChanged(nameof(RangVisibility));
            }
        }

        /// <summary>Rang dans la sélection, à partir de 1. 0 = non retenue.</summary>
        public int Rang
        {
            get => _rang;
            set
            {
                if (!Set(ref _rang, value)) return;
                OnPropertyChanged(nameof(RangVisibility));
            }
        }

        public Visibility RangVisibility =>
            Choisie && Rang > 0 ? Visibility.Visible : Visibility.Collapsed;

        public Brush BorderBrush => Choisie
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;

        public Brush FondBrush => Choisie
            ? (Brush)Application.Current.Resources["PanelBrush"]
            : (Brush)Application.Current.Resources["CardBrush"];
    }
}
