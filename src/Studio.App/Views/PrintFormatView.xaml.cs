using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

            // « Personnalisé » ferme les TROIS grilles, et ce n'est pas le même geste :
            //
            // - impression rapide : des planches composées sur un papier du minilab ;
            // - agrandissements : un tirage unique en A2, A3… sorti en fichier pour l'Epson ;
            // - cadre blanc : une planche elle aussi, mais chaque tirage porte sa marge.
            //
            // Le cadre blanc en était privé — « sa marge est imposée par le format » —, ce
            // qui enfermait l'opérateur dans les seuls formats bordés du catalogue. La
            // marge n'a pourtant pas besoin du format : elle se pose à l'intérieur de la
            // case, quelle que soit sa taille. Demandé depuis la boutique le 13/08/2026.
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
            else if (ligne.Famille == PrintFamily.WhiteBorder)
                Navigator.Go(new CustomSizeView(bordMm: MargeDuCadreBlanc()),
                    "Taille personnalisée à bord blanc");
            else
                Navigator.Go(new CustomSizeView(), "Taille personnalisée");
            return;
        }

        var produit = ligne.Produit;

        // Un agrandissement passe d'abord par le choix de la feuille : plusieurs tirages du
        // même format tiennent souvent sur une seule, et la moitié du papier partait à la
        // chute. L'écran ne s'affiche que s'il y a un montage possible — sinon on enchaîne
        // comme avant, et le parcours ne change pas d'un écran.
        if (_famille == PrintFamily.Enlargement)
        {
            MontageFeuilleView.Proposer(produit, feuille => VersLesPhotos(produit, ligne.Nom, feuille));
            return;
        }

        VersLesPhotos(produit, ligne.Nom, null);
    }

    /// <param name="montageFeuille">
    /// Feuille de montage retenue, ou null pour un fichier par tirage.
    /// </param>
    private static void VersLesPhotos(Product produit, string nom, string? montageFeuille)
    {
        // le format est choisi : la sélection des photos démarre déjà sur ce produit
        Navigator.Go(new SourcePickerView((root, profond) =>
            Navigator.Go(
                new PhotoGridView(root, produit.Code, avecSousDossiers: profond,
                    montageFeuille: montageFeuille),
                $"{nom} — choisir les photos")),
            $"{nom} — choisir le support");
    }

    /// <summary>
    /// La marge du cadre blanc, telle que la boutique la pratique.
    ///
    /// Lue sur les produits bordés du catalogue plutôt qu'écrite en dur : c'est la valeur
    /// que l'opérateur voit déjà sur ses formats, et une boutique qui borde à 4 mm ne doit
    /// pas se retrouver avec 5 mm sur sa seule taille libre. La médiane, pour qu'un produit
    /// mal saisi ne l'emporte pas sur les autres. Cinq millimètres à défaut — c'est la
    /// marge dont parle <see cref="PrintFamily.WhiteBorder"/>.
    /// </summary>
    private static double MargeDuCadreBlanc()
    {
        var marges = PrintFamilyView.ProductsOf(PrintFamily.WhiteBorder)
            .Select(p => p.BorderMm)
            .Where(b => b > 0)
            .OrderBy(b => b)
            .ToList();

        return marges.Count == 0 ? 5 : marges[marges.Count / 2];
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnCancel(object sender, RoutedEventArgs e) => AccueilStudio.Rentrer();

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
            ? $"{Produit.WidthMm:0} × {Produit.HeightMm:0} mm"
            : Famille == PrintFamily.Enlargement
                ? "A4, A3, A2… ou la taille de votre choix"
                : "taille au choix";

        /// <summary>
        /// Machine qui sortira le tirage. Sans cette mention, deux formats homonymes
        /// deviennent indiscernables : le 10×15 du minilab et celui de la DS620 ne font
        /// pas la même taille et ne sortent pas de la même machine.
        /// </summary>
        public string Destination => Produit is null
            ? Famille == PrintFamily.Enlargement ? "Epson" : "Minilab DE100"
            : Produit.Output switch
            {
                ProductOutput.FujiMinilab => "Minilab DE100",
                ProductOutput.ManualFile => "Epson",
                _ => string.IsNullOrWhiteSpace(Produit.PrinterName) ? "à définir" : Produit.PrinterName,
            };

        /// <summary>
        /// Une couleur PAR MACHINE : c'est ce qui se repère sans lire, et c'est tout
        /// l'intérêt de la pastille. Le minilab en bleu, la sublimation en violet, l'Epson
        /// en vert — les mêmes teintes que le bandeau des machines.
        /// </summary>
        public Brush DestinationBrush
        {
            get
            {
                var sortie = Produit?.Output
                             ?? (Famille == PrintFamily.Enlargement
                                 ? ProductOutput.ManualFile
                                 : ProductOutput.FujiMinilab);

                return sortie switch
                {
                    ProductOutput.FujiMinilab => (Brush)Application.Current.Resources["AccentDarkBrush"],
                    ProductOutput.ManualFile => new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0x33)),
                    _ => new SolidColorBrush(Color.FromRgb(0x6A, 0x4C, 0x93)),
                };
            }
        }

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
