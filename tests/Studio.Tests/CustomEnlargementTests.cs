using Studio.Core.Catalog;
using Studio.Core.Domain;
using Xunit;

namespace Studio.Tests;

/// <summary>
/// Agrandissements à taille libre : quel format du catalogue les porte, et donc leur prix.
///
/// Règle posée par l'exploitant le 02/08/2026 : « si ça tient dans un 30×40, le prix d'un
/// 30×40 ; si c'est dans un 40×50, le prix d'un 40×50. » Ni tarif au dm² à tenir à jour, ni
/// prix tapé à la main devant le client.
///
/// Les formats ci-dessous sont ceux du catalogue de la boutique, avec leurs prix réels.
/// </summary>
public class CustomEnlargementTests
{
    private static Product Format(string code, double w, double h, decimal prix) => new()
    {
        Code = code, Name = code, WidthMm = w, HeightMm = h, Price = prix,
        Output = ProductOutput.ManualFile, Dpi = 300, Enabled = true,
    };

    private static readonly List<Product> Catalogue =
    [
        Format("30x30", 300, 300, 19.90m),
        Format("30x40", 300, 400, 12.90m),
        Format("30x45", 300, 450, 19.90m),
        Format("40x50", 400, 500, 19.90m),
        Format("40x60", 400, 600, 19.90m),
        Format("50x60", 500, 600, 24.90m),
        Format("50x70", 500, 700, 24.90m),
        Format("60x90", 600, 900, 35.90m),
        Format("70x100", 700, 1000, 0m),   // non tarifé, désactivé en boutique
    ];

    [Fact]
    public void Un_A4_tient_dans_le_30x40_et_en_prend_le_prix()
    {
        // A4 = 210 × 297 : le 30×40 le contient et c'est le moins cher (12,90 €)
        var papier = EnlargementSizes.PaperFor(210, 297, Catalogue);
        Assert.Equal("30x40", papier!.Code);
        Assert.Equal(12.90m, papier.Price);
    }

    [Fact]
    public void Un_A3_ne_tient_PAS_dans_le_30x40()
    {
        // A3 = 297 × 420 : la largeur passe (297 ≤ 300) mais pas la hauteur (420 > 400).
        // C'est le 30×45 qui le porte — quatre centimètres et demi font tout le prix.
        var papier = EnlargementSizes.PaperFor(297, 420, Catalogue);
        Assert.Equal("30x45", papier!.Code);
        Assert.Equal(19.90m, papier.Price);
    }

    [Fact]
    public void Un_A2_tient_dans_le_50x60()
    {
        // A2 = 420 × 594. Le 40×60 (400 × 600) est trop étroit de 2 cm.
        var papier = EnlargementSizes.PaperFor(420, 594, Catalogue);
        Assert.Equal("50x60", papier!.Code);
        Assert.Equal(24.90m, papier.Price);
    }

    [Fact]
    public void Un_A1_tient_dans_le_60x90()
    {
        var papier = EnlargementSizes.PaperFor(594, 841, Catalogue);
        Assert.Equal("60x90", papier!.Code);
    }

    [Fact]
    public void Le_sens_du_tirage_ne_change_rien()
    {
        // un tirage se pose comme on veut : un A3 couché doit trouver le même papier
        Assert.Equal(
            EnlargementSizes.PaperFor(297, 420, Catalogue)!.Code,
            EnlargementSizes.PaperFor(420, 297, Catalogue)!.Code);
    }

    [Fact]
    public void Le_moins_cher_lemporte_sur_le_plus_petit()
    {
        // 28 × 28 cm tient dans le 30×30 (19,90 €) comme dans le 30×40 (12,90 €).
        // C'est le PRIX qu'on annonce au client, donc c'est lui qui départage.
        var papier = EnlargementSizes.PaperFor(280, 280, Catalogue);
        Assert.Equal("30x40", papier!.Code);
    }

    [Fact]
    public void Un_format_non_tarife_ne_gagne_jamais()
    {
        // le 70×100 est à 0,00 € : sans ce garde-fou, il raflerait tout au « moins cher »
        // et la boutique travaillerait gratuitement
        Assert.NotEqual("70x100", EnlargementSizes.PaperFor(210, 297, Catalogue)!.Code);
    }

    [Fact]
    public void Au_dela_du_plus_grand_format_rien_nest_rendu()
    {
        // A0 = 841 × 1189 : plus grand que tout ce que le catalogue tarife. L'écran doit le
        // dire avant que l'opérateur n'annonce un prix.
        Assert.Null(EnlargementSizes.PaperFor(841, 1189, Catalogue));
    }

    [Fact]
    public void Une_taille_nulle_est_refusee()
    {
        Assert.Null(EnlargementSizes.PaperFor(0, 297, Catalogue));
    }

    // ----- le produit engendré -----

    [Fact]
    public void Le_code_est_stable_et_ne_depend_pas_du_sens()
    {
        // redemander deux fois le même format doit retomber sur le MÊME produit : sinon le
        // catalogue se remplirait d'un doublon par commande
        Assert.Equal("agr-297x420", EnlargementSizes.CodeFor(297, 420));
        Assert.Equal("agr-297x420", EnlargementSizes.CodeFor(420, 297));
    }

    [Fact]
    public void Le_produit_engendre_est_un_agrandissement_au_prix_du_papier()
    {
        var papier = Format("30x45", 300, 450, 19.90m);
        papier.PriceTiers = [new PriceTier { FromQuantity = 1, UnitPrice = 19.90m },
                             new PriceTier { FromQuantity = 5, UnitPrice = 17.90m }];

        var produit = EnlargementSizes.Create(297, 420, papier, "A3 (29,7 × 42 cm)");

        Assert.Equal("agr-297x420", produit.Code);
        Assert.Equal("A3 (29,7 × 42 cm)", produit.Name);
        Assert.Equal(297, produit.WidthMm);
        Assert.Equal(420, produit.HeightMm);

        // il sort par le circuit des agrandissements : fichier repris à la main, aucune file
        Assert.Equal(ProductOutput.ManualFile, produit.Output);
        Assert.Equal("", produit.PrinterName);

        // le tarif dégressif du papier suit, RECOPIÉ : le produit garde le tarif du jour
        Assert.Equal(19.90m, produit.Price);
        Assert.Equal(17.90m, produit.UnitPriceFor(5));

        papier.PriceTiers[1].UnitPrice = 1m;
        Assert.Equal(17.90m, produit.UnitPriceFor(5));
    }

    [Fact]
    public void Sans_nom_donne_le_produit_prend_ses_centimetres()
    {
        var produit = EnlargementSizes.Create(250, 380, Format("30x40", 300, 400, 12.90m));
        Assert.Equal("25 × 38 cm", produit.Name);
    }

    [Fact]
    public void Les_formats_normalises_couvrent_du_A4_au_A0()
    {
        var noms = EnlargementSizes.Standards.Select(s => s.Name).ToList();
        Assert.Contains("A4", noms);
        Assert.Contains("A3", noms);
        Assert.Contains("A2", noms);
        Assert.Contains("A1", noms);

        // les cotes ISO, au millimètre : c'est tout l'intérêt de la tuile
        var a2 = EnlargementSizes.Standards.Single(s => s.Name == "A2");
        Assert.Equal(420, a2.WidthMm);
        Assert.Equal(594, a2.HeightMm);
    }
}
