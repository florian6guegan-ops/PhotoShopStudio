using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;

namespace Studio.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();

        // le compte est affiché sur le bouton : un agrandissement oublié dans la file
        // ne se voit nulle part ailleurs, puisque rien ne part tout seul à l'impression
        Loaded += (_, _) =>
        {
            var pending = LargeFormatQueueView.PendingCount();
            LargeFormatButton.Content = pending > 0
                ? $"🖼  Agrandissements à tirer ({pending})"
                : "🖼  Agrandissements à tirer";
        };
    }

    private void OnMachineStatus(object sender, RoutedEventArgs e) =>
        Navigator.Go(new MachineStatusView(), "État des machines");

    private void OnLargeFormatQueue(object sender, RoutedEventArgs e) =>
        Navigator.Go(new LargeFormatQueueView(), "Agrandissements à tirer");

    private void OnNewOrder(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SourcePickerView(), "Nouvelle commande — choisir le support");

    private void OnPhoneUpload(object sender, RoutedEventArgs e) =>
        Navigator.Go(new PhoneUploadView(), "Photos depuis un téléphone");

    private void OnIdPhoto(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SourcePickerView(root =>
            Navigator.Go(new IdPhotoView(root), "Photos d'identité")),
            "Photos d'identité — choisir le support");

    private void OnOrders(object sender, RoutedEventArgs e) =>
        Navigator.Go(new OrdersView(), "Commandes du jour");

    private void OnCatalog(object sender, RoutedEventArgs e) =>
        Navigator.Go(new CatalogView(), "Catalogue et imprimantes");

    private void OnStats(object sender, RoutedEventArgs e) =>
        Navigator.Go(new StatsView(), "Statistiques");
}
