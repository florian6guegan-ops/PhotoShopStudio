using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Domain;

namespace Studio.App.Views;

/// <summary>Les trois familles de tirages proposées, comme dans DiLand.</summary>
public enum PrintFamily
{
    /// <summary>Tirages courants, sortis sur le minilab.</summary>
    Quick,

    /// <summary>Grands formats, tirés sur l'Epson depuis la boîte grand format.</summary>
    Enlargement,

    /// <summary>Tirages avec la marge blanche de 5 mm.</summary>
    WhiteBorder,
}

/// <summary>
/// Choix de la famille de tirage : impression rapide, agrandissements, ou cadre blanc.
/// C'est le deuxième écran du parcours Tirages de DiLand, que les opérateurs connaissent.
///
/// Les trois familles ne sont pas décoratives : elles correspondent à trois circuits
/// d'impression différents — minilab, Epson via Photoshop, et minilab avec marge.
/// </summary>
public partial class PrintFamilyView : UserControl
{
    public PrintFamilyView()
    {
        InitializeComponent();
        Loaded += (_, _) => Describe();
    }

    /// <summary>Produits d'une famille, dans l'ordre du plus petit au plus grand.</summary>
    public static IReadOnlyList<Product> ProductsOf(PrintFamily family)
    {
        var actifs = App.Services.Catalog.Enabled.Where(p => p.Sheet is null);

        var famille = family switch
        {
            PrintFamily.Enlargement => actifs.Where(p => p.Output == ProductOutput.ManualFile),
            PrintFamily.WhiteBorder => actifs.Where(p => EstCadreBlanc(p)),
            _ => actifs.Where(p => p.Output != ProductOutput.ManualFile && !EstCadreBlanc(p)),
        };

        return famille.OrderBy(p => p.WidthMm * p.HeightMm).ToList();
    }

    private static bool EstCadreBlanc(Product p) =>
        p.Name.StartsWith("Bord blanc", StringComparison.OrdinalIgnoreCase) || p.BorderMm > 0;

    private void Describe()
    {
        RapideDetail.Text = Resume(PrintFamily.Quick, "Tirages courants, sortis sur le minilab");
        AgrandissementDetail.Text = Resume(PrintFamily.Enlargement, "Grands formats, tirés sur l'Epson");
        CadreBlancDetail.Text = Resume(PrintFamily.WhiteBorder, "Marge blanche de 5 mm autour de la photo");
    }

    private static string Resume(PrintFamily family, string phrase)
    {
        var produits = ProductsOf(family);
        if (produits.Count == 0) return phrase + "\n(aucun format disponible)";

        var duPrix = produits.Min(p => p.Price);
        return $"{phrase}\n{produits.Count} formats, à partir de {duPrix:0.00} €";
    }

    private void OnRapide(object sender, RoutedEventArgs e) => Ouvrir(PrintFamily.Quick, "Impression rapide");

    private void OnAgrandissements(object sender, RoutedEventArgs e) =>
        Ouvrir(PrintFamily.Enlargement, "Agrandissements");

    private void OnCadreBlanc(object sender, RoutedEventArgs e) =>
        Ouvrir(PrintFamily.WhiteBorder, "Avec cadre blanc");

    private static void Ouvrir(PrintFamily family, string titre)
    {
        if (ProductsOf(family).Count == 0)
        {
            MessageBox.Show(
                $"Aucun format n'est disponible dans « {titre} ».\n\n" +
                "Vérifiez le catalogue : les produits concernés sont peut-être désactivés.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Navigator.Go(new PrintFormatView(family), titre);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Home(new HomeView(), "Studio Photo");
}
