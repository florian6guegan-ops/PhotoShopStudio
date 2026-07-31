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
        Assert.Equal(40, Catalogue.Value.All.Count);
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
    // prix affichés en boutique (affiches « TIRAGE NUMÉRIQUE » et « TIRAGE AGRANDISSEMENT »)
    [InlineData("10x15", 0.60)]
    [InlineData("13x18", 1.50)]
    [InlineData("15x20", 1.90)]
    [InlineData("18x24", 6.90)]
    // 20×20 et 20×25 ne figurent pas sur l'affiche : alignés sur le 20×30 à la demande
    // de l'exploitant (31/07/2026)
    [InlineData("20x20", 7.50)]
    [InlineData("20x25", 7.50)]
    [InlineData("20x30", 7.50)]
    [InlineData("30x40", 12.90)]
    [InlineData("40x50", 19.90)]
    [InlineData("50x70", 24.90)]
    [InlineData("60x80", 29.90)]
    public void Les_prix_sont_ceux_affiches_en_boutique(string code, double prix)
    {
        Assert.Equal((decimal)prix, Get(code).Price);
    }

    /// <summary>
    /// Le bord blanc ne se facture pas plus cher que le tirage plein (décision de
    /// l'exploitant, 31/07/2026) : c'est le même papier, seul le cadrage change.
    /// </summary>
    [Fact]
    public void Le_bord_blanc_coute_le_meme_prix_que_le_tirage_plein()
    {
        // deux produits peuvent porter le même nom sur des machines différentes
        // (un 10×15 sur le DE100, un autre sur la DS620) : la clé inclut l'imprimante
        var parNom = Catalogue.Value.All.ToDictionary(p => $"{p.Name}|{p.PrinterName}", StringComparer.Ordinal);
        var bordBlanc = Catalogue.Value.All.Where(p => p.Name.StartsWith("Bord blanc ", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(bordBlanc);
        foreach (var produit in bordBlanc)
        {
            var cle = $"{produit.Name["Bord blanc ".Length..]}|{produit.PrinterName}";
            Assert.True(parNom.TryGetValue(cle, out var reference),
                $"Aucun tirage plein en face de « {produit.Name} » sur {produit.PrinterName}");

            Assert.Equal(reference.Price, produit.Price);
            Assert.Equal(reference.PriceTiers.Count, produit.PriceTiers.Count);
        }
    }

    [Theory]
    // affiche « De 1 à 30 : 0,60 € / De 31 à 49 : 0,55 € / De 50 à 99 : 0,50 € / De 100 à 200 : 0,45 € »
    [InlineData(1, 0.60)]
    [InlineData(30, 0.60)]
    [InlineData(31, 0.55)]
    [InlineData(49, 0.55)]
    [InlineData(50, 0.50)]
    [InlineData(99, 0.50)]
    [InlineData(100, 0.45)]
    [InlineData(200, 0.45)]
    public void Le_10x15_applique_le_tarif_degressif_affiche(int quantite, double attendu)
    {
        Assert.Equal((decimal)attendu, Get("10x15").UnitPriceFor(quantite));
    }

    [Fact]
    public void Un_produit_a_prix_unique_ignore_la_quantite()
    {
        var produit = Get("13x18");

        Assert.Empty(produit.PriceTiers);
        Assert.Equal(1.50m, produit.UnitPriceFor(1));
        Assert.Equal(1.50m, produit.UnitPriceFor(500));
    }

    [Fact]
    public void Le_premier_palier_correspond_toujours_au_prix_affiche()
    {
        Assert.All(Catalogue.Value.All.Where(p => p.PriceTiers.Count > 0), p =>
        {
            Assert.Equal(1, p.PriceTiers[0].FromQuantity);
            Assert.Equal(p.Price, p.PriceTiers[0].UnitPrice);
        });
    }

    [Fact]
    public void Les_paliers_sont_ordonnes_et_decroissants()
    {
        Assert.All(Catalogue.Value.All.Where(p => p.PriceTiers.Count > 1), p =>
        {
            for (var i = 1; i < p.PriceTiers.Count; i++)
            {
                Assert.True(p.PriceTiers[i].FromQuantity > p.PriceTiers[i - 1].FromQuantity,
                    $"{p.Code} : paliers non ordonnés");
                Assert.True(p.PriceTiers[i].UnitPrice <= p.PriceTiers[i - 1].UnitPrice,
                    $"{p.Code} : le palier {p.PriceTiers[i].FromQuantity} n'est pas plus avantageux");
            }
        });
    }

    /// <summary>Un produit vendable à 0,00 € distribuerait des tirages gratuits.</summary>
    [Fact]
    public void Aucun_produit_actif_n_est_gratuit()
    {
        Assert.All(Catalogue.Value.Enabled, p => Assert.True(p.Price > 0, $"{p.Code} est actif à 0,00 €"));
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
    public void Les_produits_envoyes_au_spouleur_designent_une_imprimante()
    {
        var automatiques = Catalogue.Value.Enabled.Where(p => p.Output == ProductOutput.Printer);

        Assert.All(automatiques, p => Assert.False(string.IsNullOrWhiteSpace(p.PrinterName),
            $"{p.Code} est actif sans imprimante"));
    }

    /// <summary>
    /// Au-delà du 21×29,7 la boutique tire sur l'Epson SC-P800, mais depuis l'outil
    /// d'impression de Photoshop. Ces produits sortent donc en fichiers et ne doivent
    /// jamais partir au spouleur Windows.
    /// </summary>
    [Fact]
    public void Les_grands_formats_passent_par_Photoshop()
    {
        var grands = Catalogue.Value.All.Where(p => p.WidthMm >= 300).ToList();

        Assert.NotEmpty(grands);
        Assert.All(grands, p =>
        {
            Assert.Equal(ProductOutput.ManualFile, p.Output);
            Assert.True(string.IsNullOrEmpty(p.PrinterName), $"{p.Code} ne doit désigner aucune file Windows");
        });
    }

    /// <summary>
    /// Une enveloppe ne peut pas mélanger circuit automatique et circuit manuel :
    /// les produits repris dans Photoshop doivent donc avoir leur propre canal.
    /// </summary>
    [Fact]
    public void Le_circuit_manuel_a_son_propre_canal()
    {
        var manuels = Catalogue.Value.All.Where(p => p.Output == ProductOutput.ManualFile).ToList();
        var automatiques = Catalogue.Value.All.Where(p => p.Output == ProductOutput.Printer).ToList();

        Assert.NotEmpty(manuels);
        Assert.All(manuels, p => Assert.Equal("Agrandissements (Photoshop)", p.Channel));
        Assert.DoesNotContain(automatiques, p => p.Channel == "Agrandissements (Photoshop)");
    }

    [Fact]
    public void Les_formats_jusqu_au_A4_restent_sur_le_minilab()
    {
        // le 21×29,7 est la limite : lui passe encore par le DE100
        var produit = Get("21x29-7");

        Assert.Equal(ProductOutput.Printer, produit.Output);
        Assert.Equal("FUJIFILM DE100", produit.PrinterName);
    }

    [Fact]
    public void Les_produits_actifs_ont_une_resolution_utilisable()
    {
        Assert.All(Catalogue.Value.Enabled, p => Assert.True(p.Dpi >= 150, $"{p.Code} : {p.Dpi} ppp"));
    }

    /// <summary>
    /// Ni DiLand ni les affiches ne donnent de prix pour ces deux-là (le 70×100 relève
    /// du « sur devis »). Ils restent désactivés ; le constat est enregistré ici.
    /// </summary>
    [Fact]
    public void Les_produits_sans_prix_connu_sont_recenses()
    {
        var sansPrix = Catalogue.Value.All.Where(p => p.Price == 0).Select(p => p.Code).OrderBy(c => c).ToList();

        Assert.Equal(["70x100", "a4"], sansPrix);
    }
}
