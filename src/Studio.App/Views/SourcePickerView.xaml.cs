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

    /// <param name="onFolderChosen">Suite du parcours ; par défaut la grille d'impression.</param>
    public SourcePickerView(Action<string, bool>? onFolderChosen = null)
    {
        _onFolderChosen = onFolderChosen
            ?? ((root, profond) => Navigator.Go(
                new PhotoGridView(root, avecSousDossiers: profond), "Choisir les photos"));
        InitializeComponent();
        _watcher.DrivesChanged += drives => Dispatcher.Invoke(() => Refresh(drives));
        Loaded += (_, _) =>
        {
            Refresh(RemovableDriveWatcher.GetDrives());
            _watcher.Start();
        };
        Unloaded += (_, _) => _watcher.Dispose();
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

        if (dialog.ShowDialog() != true) return;

        Navigator.Go(
            new FolderBrowserView(
                dialog.FolderName,
                choix => _onFolderChosen(choix.Path, choix.AvecSousDossiers)),
            "Parcourir les dossiers");
    }
}
