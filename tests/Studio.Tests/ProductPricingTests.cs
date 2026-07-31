using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>Tarif dégressif : choix du palier applicable à une quantité.</summary>
public class ProductPricingTests
{
    private static Product AvecPaliers() => new()
    {
        Code = "10x15",
        Price = 0.60m,
        PriceTiers =
        {
            new PriceTier { FromQuantity = 1, UnitPrice = 0.60m },
            new PriceTier { FromQuantity = 31, UnitPrice = 0.55m },
            new PriceTier { FromQuantity = 50, UnitPrice = 0.50m },
            new PriceTier { FromQuantity = 100, UnitPrice = 0.45m },
        },
    };

    [Fact]
    public void Sans_palier_le_prix_de_base_s_applique()
    {
        var produit = new Product { Price = 1.90m };

        Assert.Equal(1.90m, produit.UnitPriceFor(1));
        Assert.Equal(1.90m, produit.UnitPriceFor(1000));
    }

    [Theory]
    [InlineData(1, 0.60)]
    [InlineData(30, 0.60)]
    [InlineData(31, 0.55)]
    [InlineData(50, 0.50)]
    [InlineData(100, 0.45)]
    [InlineData(5000, 0.45)]
    public void Le_palier_atteint_le_plus_avantageux_gagne(int quantite, double attendu)
    {
        Assert.Equal((decimal)attendu, AvecPaliers().UnitPriceFor(quantite));
    }

    [Fact]
    public void Des_paliers_en_desordre_donnent_quand_meme_le_bon_prix()
    {
        var produit = new Product
        {
            Price = 0.60m,
            PriceTiers =
            {
                new PriceTier { FromQuantity = 50, UnitPrice = 0.50m },
                new PriceTier { FromQuantity = 1, UnitPrice = 0.60m },
                new PriceTier { FromQuantity = 31, UnitPrice = 0.55m },
            },
        };

        Assert.Equal(0.55m, produit.UnitPriceFor(40));
    }

    /// <summary>Si aucun palier ne couvre la quantité, on retombe sur le prix de base.</summary>
    [Fact]
    public void Un_premier_palier_au_dela_de_la_quantite_laisse_le_prix_de_base()
    {
        var produit = new Product
        {
            Price = 0.90m,
            PriceTiers = { new PriceTier { FromQuantity = 5, UnitPrice = 0.60m } },
        };

        Assert.Equal(0.90m, produit.UnitPriceFor(1));
        Assert.Equal(0.60m, produit.UnitPriceFor(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Une_quantite_invalide_est_refusee(int quantite)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AvecPaliers().UnitPriceFor(quantite));
    }
}
