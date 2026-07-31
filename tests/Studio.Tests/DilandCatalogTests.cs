using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// Vérifie le catalogue repris de DiLand (catalog/products.diland.json), extrait de la
/// base de la borne le 31/07/2026. Les cotes et les prix doivent rester ceux de DiLand :
/// ces tests échouent si quelqu'un les modifie par inadvertance.
/// </summary>
public class DilandCatalogTests
{
    private static readonly Lazy<ProductCatalog> Catalogue = new(() => ProductCatalog.Load(CatalogPath()));

    private static string CatalogPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "catalog", "products.diland.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("catalog/products.diland.json introuvable depuis " + AppContext.BaseDirectory);
    }

    private static Product Get(string code) => Catalogue.Value.Require(code);

    [Fact]
    public void Le_catalogue_se_charge()
    {
        Assert.NotEmpty(Catalogue.Value.All);
        Assert.Equal(41, Catalogue.Value.All.Count);
    }

    [Fact]
    public void Les_codes_sont_uniques()
    {
        var codes = Catalogue.Value.All.Select(p => p.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    // les cotes réelles du DE100, telles que DiLand les envoie au minilab
    [InlineData("10x10", 102, 102)]
    [InlineData("10x15", 102, 152)]
    [InlineData("13x18", 127, 180)]
    [InlineData("15x20", 152, 203)]
    [InlineData("20x30", 203, 307)]
    [InlineData("a4", 210, 297)]
    public void Les_cotes_sont_celles_de_DiLand(string code, double largeur, double hauteur)
    {
        var produit = Get(code);

        Assert.Equal(largeur, produit.WidthMm);
        Assert.Equal(hauteur, produit.HeightMm);
    }

    [Fact]
    public void Le_10x15_DNP_garde_les_cotes_du_DS620()
    {
        // la DS620 tire un 6x4 pouces légèrement plus grand que le 10×15 nominal
        var produit = Get("10x15-dnp");

        Assert.Equal(105, produit.WidthMm);
        Assert.Equal(156.1, produit.HeightMm);
        Assert.Equal("DP-DS620", produit.PrinterName);
    }

    [Fact]
    public void Les_produits_bord_blanc_ont_une_marge_de_cinq_millimetres()
    {
        var bordBlanc = Catalogue.Value.All.Where(p => p.Name.StartsWith("Bord blanc", StringComparison.Ordinal)).ToList();

        Assert.Equal(9, bordBlanc.Count);
        Assert.All(bordBlanc, p =>
        {
            Assert.Equal(5, p.BorderMm);
            Assert.Equal(FitMode.Fit, p.DefaultFit);
        });
    }

    [Fact]
    public void Les_tirages_pleine_page_n_ont_pas_de_marge_imposee()
    {
        var produit = Get("10x15");

        Assert.Equal(0, produit.BorderMm);
        Assert.Equal(FitMode.Fill, produit.DefaultFit);
    }

    [Theory]
    // grille tarifaire boutique de DiLand, prix à l'unité
    [InlineData("10x15", 0.50)]
    [InlineData("13x18", 1.50)]
    [InlineData("15x20", 1.90)]
    [InlineData("20x30", 7.90)]
    [InlineData("bord-blanc-10x15", 0.90)]
    public void Les_prix_sont_ceux_de_DiLand(string code, double prix)
    {
        Assert.Equal((decimal)prix, Get(code).Price);
    }

    [Fact]
    public void Toutes_les_cotes_sont_en_portrait()
    {
        Assert.All(Catalogue.Value.All, p => Assert.True(p.WidthMm <= p.HeightMm,
            $"{p.Code} : {p.WidthMm}×{p.HeightMm} n'est pas en portrait"));
    }

    [Fact]
    public void Toutes_les_cotes_sont_positives()
    {
        Assert.All(Catalogue.Value.All, p =>
        {
            Assert.True(p.WidthMm > 0, $"{p.Code} : largeur nulle");
            Assert.True(p.HeightMm > 0, $"{p.Code} : hauteur nulle");
        });
    }

    [Fact]
    public void Les_produits_actifs_designent_une_imprimante()
    {
        Assert.All(Catalogue.Value.Enabled, p => Assert.False(string.IsNullOrWhiteSpace(p.PrinterName),
            $"{p.Code} est actif sans imprimante"));
    }

    /// <summary>
    /// Les agrandissements sortaient d'un profil DiLand qui ne nommait aucune file Windows :
    /// ils restent désactivés tant que l'imprimante n'est pas confirmée sur place.
    /// </summary>
    [Fact]
    public void Les_agrandissements_restent_desactives()
    {
        var grands = Catalogue.Value.All.Where(p => p.WidthMm >= 300).ToList();

        Assert.NotEmpty(grands);
        Assert.All(grands, p => Assert.False(p.Enabled, $"{p.Code} devrait rester désactivé"));
    }

    [Fact]
    public void Les_produits_actifs_ont_une_resolution_utilisable()
    {
        Assert.All(Catalogue.Value.Enabled, p => Assert.True(p.Dpi >= 150, $"{p.Code} : {p.Dpi} ppp"));
    }

    /// <summary>DiLand n'avait pas de prix pour ces trois-là ; le constat est enregistré ici.</summary>
    [Fact]
    public void Les_produits_sans_prix_dans_DiLand_sont_connus()
    {
        var sansPrix = Catalogue.Value.All.Where(p => p.Price == 0).Select(p => p.Code).OrderBy(c => c).ToList();

        Assert.Equal(["10x15-dnp", "30x40-2", "a4"], sansPrix);
    }
}
