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

        Loaded += (_, _) =>
        {
            var lignes = PrintFamilyView.ProductsOf(_famille).Select(p => new FormatRow(p)).ToList();

            // « Personnalisé » ferme les deux grilles où il a un sens, et ce n'est PAS le
            // même geste des deux côtés :
            //
            // - impression rapide : des planches composées sur un papier du minilab ;
            // - agrandissements : un tirage unique en A2, A3… sorti en fichier pour l'Epson.
            //
            // Le cadre blanc, lui, n'en a pas : sa marge est imposée par le format.
            if (_famille is PrintFamily.Quick or PrintFamily.Enlargement)
                lignes.Add(FormatRow.Personnalise(_famille));

            FormatsList.ItemsSource = lignes;
        };
    }

    private void OnFormatChoisi(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FormatRow ligne) return;

        if (ligne.Produit is null)
        {
            if (ligne.Famille == PrintFamily.Enlargement)
                Navigator.Go(new CustomEnlargementView(), "Agrandissement personnalisé");
            else
                Navigator.Go(new CustomSizeView(), "Taille personnalisée");
            return;
        }

        // le format est choisi : la sélection des photos démarre déjà sur ce produit
        Navigator.Go(new SourcePickerView((root, profond) =>
            Navigator.Go(new PhotoGridView(root, ligne.Produit.Code, avecSousDossiers: profond),
                $"{ligne.Nom} — choisir les photos")),
            $"{ligne.Nom} — choisir le support");
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Home(new HomeView(), "Studio Photo");

    /// <param name="Produit">
    /// Null pour la tuile « Personnalisé » : elle ne désigne aucun produit du catalogue, le
    /// papier n'étant choisi qu'une fois la taille et la quantité connues.
    /// </param>
    /// <param name="Famille">
    /// N'a d'intérêt que pour la tuile « Personnalisé », dont le geste n'est pas le même
    /// d'une famille à l'autre : des planches sur le minilab, ou un tirage unique sur l'Epson.
    /// </param>
    private sealed record FormatRow(Product? Produit, PrintFamily Famille = PrintFamily.Quick)
    {
        /// <summary>La tuile qui mène à la saisie d'une taille libre.</summary>
        public static FormatRow Personnalise(PrintFamily famille) => new(null, famille);

        public string Nom => Produit?.Name ?? "Personnalisé";

        public string Dimensions => Produit is not null
            ? $"{Produit.WidthMm:0} × {Produit.HeightMm:0} mm · {Destination}"
            : Famille == PrintFamily.Enlargement
                ? "A4, A3, A2… ou la taille de votre choix · Epson"
                : "taille au choix · minilab";

        /// <summary>
        /// Machine qui sortira le tirage. Sans cette mention, deux formats homonymes
        /// deviennent indiscernables : le 10×15 du minilab et celui de la DS620 ne font
        /// pas la même taille et ne sortent pas de la même machine.
        /// </summary>
        private string Destination => Produit!.Output switch
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
                if (Produit is null)
                    return Famille == PrintFamily.Enlargement
                        ? "au prix du format\ndans lequel la taille tient"
                        : "au prix du papier\nutilisé pour la planche";
                if (Produit.Price <= 0) return "prix à définir";

                var lignes = new List<string> { $"{Produit.Price:0.00} €" };
                foreach (var palier in Produit.PriceTiers.Where(t => t.FromQuantity > 1))
                    lignes.Add($"À partir de {palier.FromQuantity} : {palier.UnitPrice:0.00} €");

                return string.Join("\n", lignes);
            }
        }
    }
}
