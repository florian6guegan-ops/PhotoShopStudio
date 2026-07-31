using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;

namespace Studio.App.Views;

/// <summary>
/// Choix du type de produit, première étape d'une commande — l'équivalent de l'écran
/// « Sélectionnez un produit » de DiLand.
///
/// Les opérateurs de la boutique connaissent ce parcours : on garde le même point de
/// départ pour ne pas leur faire réapprendre l'outil. Les deux produits que la boutique
/// vend tous les jours ouvrent, les autres sont affichés désactivés plutôt que masqués,
/// pour que leur absence se voie et s'explique.
/// </summary>
public partial class ProductTypeView : UserControl
{
    public ProductTypeView() => InitializeComponent();

    private void OnTirages(object sender, RoutedEventArgs e) =>
        Navigator.Go(new PrintFamilyView(), "Tirages");

    private void OnIdPhoto(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SourcePickerView(root =>
            Navigator.Go(new IdPhotoView(root), "Photos d'identité")),
            "Photos d'identité — choisir le support");

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Back();
}
