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

    /// <summary>
    /// Le document est choisi AVANT les photos, comme dans DiLand : c'est lui qui fixe le
    /// format du tirage et le gabarit du visage, donc tout le cadrage qui suit.
    /// </summary>
    private void OnIdPhoto(object sender, RoutedEventArgs e) =>
        Navigator.Go(new IdDocumentPickerView(document =>
            Navigator.Go(new SourcePickerView((root, profond) =>
                Navigator.Go(new IdPhotoView(root, document, profond),
                    $"{document.Country} — {document.Document}")),
                "Photos d'identité — choisir le support")),
            "Photos d'identité — choisir le document");

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Back();
}
