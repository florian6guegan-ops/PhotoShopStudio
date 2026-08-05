using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Sources;

namespace Studio.App.Views;

public partial class SourcePickerView : UserControl
{
    private readonly RemovableDriveWatcher _watcher = new();

    /// <summary>
    /// Suite du parcours. Le second paramètre dit s'il faut descendre dans les
    /// sous-dossiers : un support entier, oui ; un dossier désigné dans l'explorateur,
    /// seulement si l'opérateur l'a demandé — c'est là qu'un dossier parent ramenait des
    /// dizaines de milliers de fichiers.
    /// </summary>
    private readonly Action<string, bool> _onFolderChosen;

    /// <summary>Un dossier de l'ordinateur proposé en tuile, à côté des supports amovibles.</summary>
    /// <param name="Libelle">Ce que l'opérateur lit sur la tuile.</param>
    /// <param name="Chemin">Le dossier ouvert au clic.</param>
    public sealed record DossierRaccourci(string Libelle, string Chemin);

    /// <param name="onFolderChosen">Suite du parcours ; par défaut la grille d'impression.</param>
    /// <param name="raccourcis">
    /// Dossiers de l'ordinateur à proposer en plus des supports. Null = les FAVORIS du poste
    /// (voir <see cref="DossiersFavoris"/>).
    ///
    /// Ils n'étaient proposés qu'au parcours E-Photo, au motif qu'un tirage ordinaire vient
    /// d'une carte. C'est de moins en moins vrai : les photos arrivent par WeTransfer, par
    /// courriel ou par téléphone, et l'opérateur devait alors passer par « Parcourir » puis
    /// redescendre l'arborescence devant le client. Ils sont donc partout, et la liste se
    /// règle dans Paramètres.
    /// </param>
    public SourcePickerView(
        Action<string, bool>? onFolderChosen = null,
        IReadOnlyList<DossierRaccourci>? raccourcis = null)
    {
        raccourcis ??= DossiersFavoris.Raccourcis();

        _onFolderChosen = onFolderChosen
            ?? ((root, profond) => Navigator.Go(
                new PhotoGridView(root, avecSousDossiers: profond), "Choisir les photos"));
        InitializeComponent();
        _watcher.DrivesChanged += drives => Dispatcher.Invoke(() => Refresh(drives));
        Loaded += (_, _) =>
        {
            RaccourcisList.ItemsSource = raccourcis;
            Refresh(RemovableDriveWatcher.GetDrives());
            _watcher.Start();
        };
        Unloaded += (_, _) => _watcher.Dispose();
    }

    /// <summary>
    /// Le raccourci « Téléchargements » seul.
    ///
    /// Il n'a plus lieu d'être appelé : les favoris du poste le contiennent, et ils sont
    /// désormais proposés partout. Conservé pour un parcours qui voudrait n'afficher que
    /// celui-là, sans les autres.
    /// </summary>
    public static IReadOnlyList<DossierRaccourci> RaccourciTelechargements()
    {
        var chemin = DossiersUtilisateur.Telechargement();
        return chemin is null ? [] : [new DossierRaccourci("⬇  Téléchargements", chemin)];
    }

    /// <summary>
    /// Un dossier de l'ordinateur : sans sous-dossiers, comme un dossier désigné à la
    /// main. Téléchargements contient tout ce que le navigateur a rapporté, et descendre
    /// dedans ramènerait des dizaines de milliers de fichiers.
    /// </summary>
    private void OnRaccourciClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string chemin)
            _onFolderChosen(chemin, false);
    }

    private void Refresh(IReadOnlyList<RemovableDrive> drives)
    {
        DrivesList.ItemsSource = drives;
        NoDrivesText.Visibility = drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Un support entier : DCIM d'abord, sous-dossiers compris.</summary>
    private void OnDriveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string root)
            _onFolderChosen(root, true);
    }

    /// <summary>
    /// La boîte Windows désigne le POINT DE DÉPART, l'écran de parcours fait le reste.
    ///
    /// Les deux ont chacun ce qui manque à l'autre : la boîte Windows connaît les
    /// favoris, l'historique et la frappe d'un chemin — elle amène vite au bon endroit —
    /// mais elle rend un chemin sans jamais dire ce qu'il contient. C'est ainsi qu'on
    /// repartait avec un dossier vide, ou avec un dossier parent dont la lecture ramenait
    /// tout un disque. L'écran de parcours prend le relais là où elle s'arrête : il
    /// montre les photos de chaque sous-dossier avant qu'on l'ouvre.
    /// </summary>
    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Où commencer la recherche des photos ?",
        };
        DossiersFavoris.Epingler(dialog);

        if (dialog.ShowDialog() != true) return;

        Navigator.Go(
            new FolderBrowserView(
                dialog.FolderName,
                choix => _onFolderChosen(choix.Path, choix.AvecSousDossiers)),
            "Parcourir les dossiers");
    }
}
