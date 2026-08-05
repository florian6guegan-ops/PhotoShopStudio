using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Sources;

namespace Studio.App.Views;

/// <summary>
/// Navigation dans l'arborescence, comme DiLand : on descend de dossier en dossier, on
/// voit combien de photos chacun contient et à quoi elles ressemblent, puis on ouvre.
///
/// Ce que la boîte de dialogue Windows ne permettait pas : elle rend un chemin et rien
/// d'autre. L'opérateur choisissait à l'aveugle, tombait sur un dossier sans photos — ou
/// désignait un dossier parent dont le scan ramenait tout un disque et faisait tomber
/// l'application par manque de mémoire (01/08/2026).
///
/// L'écran ne quitte jamais l'application : tant qu'on n'a pas trouvé, on reste ici.
/// </summary>
public partial class FolderBrowserView : UserControl
{
    /// <summary>Ce que l'opérateur a fini par désigner : un dossier, et jusqu'où le lire.</summary>
    public sealed record Choix(string Path, bool AvecSousDossiers);

    private readonly Action<Choix> _onChosen;
    private string _dossier;
    private CancellationTokenSource? _cts;
    private ObservableCollection<DossierRow> _lignes = [];
    private bool _tronque;
    private int _masques;

    /// <summary>Plafond du comptage : au-delà on affiche « 5000+ », inutile d'aller plus loin.</summary>
    private const int PlafondComptage = 5000;

    /// <summary>Nombre de sous-dossiers illustrés d'une vignette : au-delà, l'icône suffit.</summary>
    private const int MaxVignettes = 60;

    /// <param name="depart">Dossier ouvert à l'arrivée ; null = le dernier visité, sinon Images.</param>
    /// <param name="onChosen">Ce qu'on fait du dossier retenu.</param>
    public FolderBrowserView(string? depart, Action<Choix> onChosen)
    {
        _onChosen = onChosen;
        _dossier = PremierDossierValide(depart);
        InitializeComponent();

        // Les FAVORIS d'abord, les disques ensuite.
        //
        // Le volet ne montrait que les disques et les dossiers connus de Windows : le
        // dossier WeTransfer de la boutique n'y était pas, et le Bureau se retrouvait après
        // quatre lecteurs. Ce sont pourtant les trois endroits d'où les photos arrivent
        // (voir DossiersFavoris), et l'écran des favoris sert précisément à les nommer.
        ShortcutsList.ItemsSource = DossiersFavoris.Actifs()
            .Select(f => new RaccourciRow(new FolderShortcut(f.Chemin, f.Libelle, "★")))
            .Concat(FolderTree.Shortcuts().Select(r => new RaccourciRow(r)))
            .ToList();

        Loaded += (_, _) => Naviguer(_dossier);
        Unloaded += (_, _) => _cts?.Cancel();
    }

    /// <summary>
    /// Dernier dossier ouvert, retenu d'un passage à l'autre. Les photos d'une boutique
    /// arrivent presque toujours du même endroit : redemander le chemin complet à chaque
    /// commande serait une perte de temps à chaque client.
    /// </summary>
    private static string? _dernierDossier;

    private static string PremierDossierValide(string? depart)
    {
        foreach (var candidat in new[]
                 {
                     depart,
                     _dernierDossier,
                     Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidat) && Directory.Exists(candidat))
                return candidat!;
        }

        return FolderTree.Shortcuts().FirstOrDefault()?.Path
               ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ----- navigation -----

    private void Naviguer(string dossier)
    {
        if (!Directory.Exists(dossier))
        {
            MessageBox.Show($"Ce dossier n'est plus accessible :\n{dossier}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // tout travail de fond entamé pour le dossier précédent devient sans objet
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _dossier = dossier;
        _dernierDossier = dossier;

        BreadcrumbList.ItemsSource = FolderTree.Breadcrumb(dossier);
        ParentButton.IsEnabled = FolderTree.Parent(dossier) is not null;

        var sousDossiers = FolderTree.SubFolders(dossier);
        _tronque = sousDossiers.Count > 300;
        _masques = 0;

        // ObservableCollection et non List : un dossier dont on découvre qu'il n'a aucune
        // photo disparaît de la liste sans qu'il faille tout reconstruire
        _lignes = new ObservableCollection<DossierRow>(
            sousDossiers.Take(300).Select(n => new DossierRow(n)));
        FoldersList.ItemsSource = _lignes;

        MajListeVide();
        FoldersScroll.ScrollToTop();

        SummaryText.Text = "Lecture du dossier…";
        OpenButton.IsEnabled = false;
        OpenDeepButton.IsEnabled = false;

        _ = CompterAsync(dossier, _lignes.ToList(), ct);
    }

    /// <summary>
    /// Le message qui remplace la liste quand il n'y a plus rien à montrer, et la mention
    /// des dossiers écartés — un dossier qui disparaît sans explication inquiète.
    /// </summary>
    private void MajListeVide()
    {
        NoFoldersText.Visibility = _lignes.Count == 0 || _tronque
            ? Visibility.Visible
            : Visibility.Collapsed;

        NoFoldersText.Text = _tronque
            ? "Plus de 300 sous-dossiers : seuls les 300 premiers sont affichés."
            : _masques > 0
                ? $"Aucun sous-dossier avec des photos ici ({_masques} sans photo, masqué" +
                  $"{(_masques > 1 ? "s" : "")})."
                : "Aucun sous-dossier ici.";

        HiddenText.Visibility = _masques > 0 && _lignes.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        HiddenText.Text = $"{_masques} dossier{(_masques > 1 ? "s" : "")} sans photo " +
                          $"masqué{(_masques > 1 ? "s" : "")}.";
    }

    /// <summary>
    /// Compte et illustre en tâche de fond : le dossier courant d'abord — c'est lui qui
    /// commande les deux boutons du bas — puis chaque sous-dossier l'un après l'autre.
    ///
    /// Rien de tout cela ne bloque l'écran : sur un disque lent, compter les photos d'une
    /// arborescence prend des secondes, et l'opérateur doit pouvoir descendre pendant ce
    /// temps-là. Descendre annule ce qui reste.
    /// </summary>
    private async Task CompterAsync(string dossier, List<DossierRow> lignes, CancellationToken ct)
    {
        try
        {
            var ici = await Task.Run(
                () => PhotoScanner.Count(dossier, recursive: false, PlafondComptage, ct), ct);
            if (ct.IsCancellationRequested) return;

            AfficherResume(ici, sousArbre: null);
            OpenButton.IsEnabled = ici > 0;

            var total = await Task.Run(
                () => PhotoScanner.Count(dossier, recursive: true, PlafondComptage, ct), ct);
            if (ct.IsCancellationRequested) return;

            AfficherResume(ici, total);
            OpenDeepButton.IsEnabled = total > ici;

            var thumbnails = App.Services.Thumbnails;
            for (var i = 0; i < lignes.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                var ligne = lignes[i];

                var compte = await Task.Run(
                    () => PhotoScanner.Count(ligne.Path, recursive: true, PlafondComptage, ct), ct);

                // un dossier dont rien ne sortira à l'impression n'a pas à encombrer la
                // liste : on l'écarte, et on dit seulement combien on en a écarté
                if (compte == 0)
                {
                    _lignes.Remove(ligne);
                    _masques++;
                    MajListeVide();
                    continue;
                }

                ligne.SetCompte(compte, PlafondComptage);

                if (i >= MaxVignettes) continue;

                var apercu = await Task.Run(() =>
                {
                    var photo = PhotoScanner.FirstPhoto(ligne.Path, ct);
                    return photo is null ? null : thumbnails.GetJpeg(photo, 160);
                }, ct);

                if (apercu is not null) ligne.SetPreview(ToBitmap(apercu));
            }
        }
        catch (OperationCanceledException)
        {
            // on a changé de dossier : ce comptage-là n'intéresse plus personne
        }
        catch (Exception ex)
        {
            FileLog.Write($"Lecture du dossier « {dossier} » interrompue", ex);
            if (!ct.IsCancellationRequested)
                SummaryText.Text = "Ce dossier n'a pas pu être lu entièrement.";
        }
    }

    private void AfficherResume(int ici, int? sousArbre)
    {
        var texte = ici == 0 ? "Aucune photo dans ce dossier" : $"{ici} photo{S(ici)} dans ce dossier";

        if (sousArbre is { } total && total > ici)
            texte += $"  ·  {Plafonne(total)} avec les sous-dossiers";

        if (ici == 0 && (sousArbre is null or 0))
            texte += " — descendez dans un sous-dossier, ou changez de support.";

        SummaryText.Text = texte;
    }

    private static string S(int n) => n > 1 ? "s" : "";

    private string Plafonne(int compte) =>
        compte >= PlafondComptage ? $"{PlafondComptage}+" : compte.ToString();

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

    // ----- gestes -----

    private void OnEnterFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string chemin) Naviguer(chemin);
    }

    private void OnBreadcrumb(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string chemin) Naviguer(chemin);
    }

    private void OnShortcut(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string chemin) Naviguer(chemin);
    }

    private void OnParent(object sender, RoutedEventArgs e)
    {
        if (FolderTree.Parent(_dossier) is { } parent) Naviguer(parent);
    }

    private void OnOpen(object sender, RoutedEventArgs e) =>
        _onChosen(new Choix(_dossier, AvecSousDossiers: false));

    private void OnOpenDeep(object sender, RoutedEventArgs e) =>
        _onChosen(new Choix(_dossier, AvecSousDossiers: true));

    // ----- lignes affichées -----

    private sealed record RaccourciRow(FolderShortcut Raccourci)
    {
        public string Path => Raccourci.Path;
        public string Display => $"{Raccourci.Icon}  {Raccourci.Label}";
    }

    private sealed class DossierRow : ObservableObject
    {
        private string _countLabel = "…";
        private ImageSource? _preview;

        public DossierRow(FolderNode node)
        {
            Path = node.Path;
            Name = node.Name;
        }

        public string Path { get; }
        public string Name { get; }

        public string CountLabel
        {
            get => _countLabel;
            private set => Set(ref _countLabel, value);
        }

        public ImageSource? Preview
        {
            get => _preview;
            private set
            {
                if (!Set(ref _preview, value)) return;
                OnPropertyChanged(nameof(IconVisibility));
            }
        }

        /// <summary>L'icône ne reste que tant qu'aucune photo du dossier ne la remplace.</summary>
        public Visibility IconVisibility =>
            _preview is null ? Visibility.Visible : Visibility.Collapsed;

        public void SetCompte(int compte, int plafond) =>
            CountLabel = compte switch
            {
                0 => "aucune photo",
                1 => "1 photo",
                _ when compte >= plafond => $"{plafond}+ photos",
                _ => $"{compte} photos",
            };

        public void SetPreview(ImageSource image) => Preview = image;
    }
}
