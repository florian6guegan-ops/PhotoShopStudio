using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Domain;

namespace Studio.App.Views;

/// <summary>
/// Grille des formats d'une famille, avec prix et paliers dégressifs affichés sur chaque
/// tuile — comme la grille de DiLand.
///
/// Montrer le tarif dès le choix du format évite à l'opérateur d'annoncer un prix de
/// mémoire, et rend visible la remise à partir de 31 ou 50 tirages.
/// </summary>
public partial class PrintFormatView : UserControl
{
    private readonly PrintFamily _famille;

    public PrintFormatView(PrintFamily famille)
    {
        InitializeComponent();
        _famille = famille;

        Loaded += (_, _) => FormatsList.ItemsSource =
            PrintFamilyView.ProductsOf(_famille).Select(p => new FormatRow(p)).ToList();
    }

    private void OnFormatChoisi(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FormatRow ligne) return;

        // le format est choisi : la sélection des photos démarre déjà sur ce produit
        Navigator.Go(new SourcePickerView(root =>
            Navigator.Go(new PhotoGridView(root, ligne.Produit.Code),
                $"{ligne.Nom} — choisir les photos")),
            $"{ligne.Nom} — choisir le support");
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Home(new HomeView(), "Studio Photo");

    private sealed record FormatRow(Product Produit)
    {
        public string Nom => Produit.Name;

        public string Dimensions => $"{Produit.WidthMm:0} × {Produit.HeightMm:0} mm · {Destination}";

        /// <summary>
        /// Machine qui sortira le tirage. Sans cette mention, deux formats homonymes
        /// deviennent indiscernables : le 10×15 du minilab et celui de la DS620 ne font
        /// pas la même taille et ne sortent pas de la même machine.
        /// </summary>
        private string Destination => Produit.Output switch
        {
            ProductOutput.FujiMinilab => "minilab",
            ProductOutput.ManualFile => "Epson",
            _ => string.IsNullOrWhiteSpace(Produit.PrinterName) ? "à définir" : Produit.PrinterName,
        };

        /// <summary>
        /// Prix à l'unité, suivi des paliers dégressifs s'il y en a. DiLand écrit
        /// « À partir de 31 : 0,55 € » ; on reprend la même formulation.
        /// </summary>
        public string Tarif
        {
            get
            {
                if (Produit.Price <= 0) return "prix à définir";

                var lignes = new List<string> { $"{Produit.Price:0.00} €" };
                foreach (var palier in Produit.PriceTiers.Where(t => t.FromQuantity > 1))
                    lignes.Add($"À partir de {palier.FromQuantity} : {palier.UnitPrice:0.00} €");

                return string.Join("\n", lignes);
            }
        }
    }
}
