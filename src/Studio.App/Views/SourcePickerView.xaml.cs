using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Sources;

namespace Studio.App.Views;

public partial class SourcePickerView : UserControl
{
    private readonly RemovableDriveWatcher _watcher = new();
    private readonly Action<string> _onFolderChosen;

    /// <param name="onFolderChosen">Suite du parcours ; par défaut la grille d'impression.</param>
    public SourcePickerView(Action<string>? onFolderChosen = null)
    {
        _onFolderChosen = onFolderChosen
            ?? (root => Navigator.Go(new PhotoGridView(root), "Choisir les photos"));
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

    private void OnDriveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string root)
            _onFolderChosen(root);
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choisir le dossier des photos" };
        if (dialog.ShowDialog() == true)
            _onFolderChosen(dialog.FolderName);
    }
}
