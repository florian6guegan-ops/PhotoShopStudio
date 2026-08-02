using System.Reflection;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Xunit;

namespace Studio.Tests;

/// <summary>
/// Ce que l'écran Catalogue fait subir à un produit.
///
/// Défaut trouvé le 02/08/2026 : « Modifier » et « Dupliquer » passaient par une copie
/// manuelle qui oubliait huit champs — <c>Output</c>, <c>MinilabMachineId</c>,
/// <c>MinilabPrintSizeName</c>, <c>PriceTiers</c>, et quatre de <c>SheetSpec</c>. Modifier
/// un tirage du minilab le transformait donc en produit imprimante, et effaçait ses
/// paliers de tarif. Rien ne le signalait : le produit s'enregistrait sans erreur.
/// </summary>
public class CatalogEditTests
{
    private static Product Complet() => new()
    {
        Code = "10x15",
        Name = "10×15",
        WidthMm = 102,
        HeightMm = 152,
        PrinterName = "",
        Output = ProductOutput.FujiMinilab,
        MinilabMachineId = "B",
        MinilabPrintSizeName = "152x102",
        PrinterChannel = "Minilab DE100",
        Dpi = 300,
        Price = 0.60m,
        DefaultFit = FitMode.Fit,
        BorderMm = 5,
        IccProfile = "DE100-Lustre.icc",
        DevmodeFile = "devmode-10x15.bin",
        Finishes = [new FinishOption { Name = "Lustré", DevmodeFile = "d.bin", IccProfile = "i.icc" }],
        PriceTiers =
        [
            new PriceTier { FromQuantity = 1, UnitPrice = 0.60m },
            new PriceTier { FromQuantity = 31, UnitPrice = 0.55m },
        ],
        Sheet = new SheetSpec
        {
            Copies = 8, CellWidthMm = 35, CellHeightMm = 45,
            GapMm = 3, CutMarks = false, CutBorder = false, DateStamp = false,
        },
        Enabled = false,
    };

    [Fact]
    public void La_copie_ne_perd_aucun_champ_simple()
    {
        var original = Complet();
        var copie = original.Copy();

        // toutes les propriétés à valeur simple, comparées une à une SANS les nommer :
        // une propriété ajoutée demain à Product sera couverte sans toucher à cet essai
        var aPart = new[] { nameof(Product.Finishes), nameof(Product.PriceTiers), nameof(Product.Sheet) };

        foreach (var propriete in typeof(Product).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!propriete.CanWrite || aPart.Contains(propriete.Name)) continue;

            Assert.True(
                Equals(propriete.GetValue(original), propriete.GetValue(copie)),
                $"Product.Copy() a perdu « {propriete.Name} » : " +
                $"{propriete.GetValue(original)} devenu {propriete.GetValue(copie)}");
        }
    }

    [Fact]
    public void La_copie_garde_la_sortie_et_les_paliers()
    {
        var copie = Complet().Copy();

        // les deux pertes qui coûtaient le plus cher : un tirage minilab devenu imprimante,
        // et un tarif dégressif effacé
        Assert.Equal(ProductOutput.FujiMinilab, copie.Output);
        Assert.Equal(2, copie.PriceTiers.Count);
        Assert.Equal(0.55m, copie.UnitPriceFor(31));
    }

    [Fact]
    public void La_copie_garde_les_sept_reglages_de_planche()
    {
        var copie = Complet().Copy();
        var planche = Assert.IsType<SheetSpec>(copie.Sheet);

        Assert.Equal(8, planche.Copies);
        Assert.Equal(35, planche.CellWidthMm);
        Assert.Equal(45, planche.CellHeightMm);
        Assert.Equal(3, planche.GapMm);
        Assert.False(planche.CutMarks);
        Assert.False(planche.CutBorder);
        Assert.False(planche.DateStamp);
    }

    [Fact]
    public void La_copie_est_independante_de_loriginal()
    {
        var original = Complet();
        var copie = original.Copy();

        // « Modifier » édite la copie : annuler ne doit RIEN laisser dans le catalogue
        copie.Name = "autre";
        copie.PriceTiers.Clear();
        copie.Finishes.Clear();
        copie.Sheet!.Copies = 1;

        Assert.Equal("10×15", original.Name);
        Assert.Equal(2, original.PriceTiers.Count);
        Assert.Single(original.Finishes);
        Assert.Equal(8, original.Sheet!.Copies);
    }

    // ----- suppression : ce qui est cité ne se supprime pas -----

    private static Order CommandeAvec(params string[] codes)
    {
        var commande = new Order();
        var enveloppe = new Envelope { Number = 1 };
        foreach (var code in codes)
            enveloppe.Lines.Add(new OrderLine { ProductCode = code });
        commande.Envelopes.Add(enveloppe);
        return commande;
    }

    [Fact]
    public void Un_produit_sans_commande_nest_cite_nulle_part()
    {
        Assert.Equal(0, ProductCatalog.CountReferences("pola", [CommandeAvec("10x15")]));
    }

    [Fact]
    public void On_compte_les_commandes_pas_les_lignes()
    {
        // deux lignes du même produit dans une seule commande : c'est UNE commande qui le
        // cite, et c'est ce nombre qu'on annonce à l'opérateur
        var commandes = new[] { CommandeAvec("10x15", "10x15"), CommandeAvec("13x18") };
        Assert.Equal(1, ProductCatalog.CountReferences("10x15", commandes));
    }

    [Fact]
    public void La_casse_du_code_ne_change_rien()
    {
        // le catalogue est indexé sans tenir compte de la casse : le comptage doit suivre,
        // sinon un « ID-FR-6 » serait supprimé alors qu'une commande porte « id-fr-6 »
        Assert.Equal(1, ProductCatalog.CountReferences("id-fr-6", [CommandeAvec("ID-FR-6")]));
    }
}
