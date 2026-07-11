using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;

namespace Studio.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OnNewOrder(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SourcePickerView(), "Nouvelle commande — choisir le support");

    private void OnIdPhoto(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SourcePickerView(root =>
            Navigator.Go(new IdPhotoView(root), "Photos d'identité")),
            "Photos d'identité — choisir le support");

    private void OnOrders(object sender, RoutedEventArgs e) =>
        Navigator.Go(new OrdersView(), "Commandes du jour");

    private void OnCatalog(object sender, RoutedEventArgs e) =>
        Navigator.Go(new CatalogView(), "Catalogue et imprimantes");
}
