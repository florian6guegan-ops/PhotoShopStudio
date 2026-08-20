using Studio.Imaging.Geometry;
using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// Le papier de la planche de rentrée, dérivé de la planche d'identité du poste.
///
/// <b>Ce qui se vérifie ici est un problème de DÉPLOIEMENT.</b> Les quatre boutiques ont
/// leur propre catalogue, qu'une mise à jour ne touche jamais : le nouveau format ne peut
/// donc pas être livré, il doit se fabriquer sur place. Et ce qu'il reprend — machine,
/// profil ICC, DEVMODE — est justement ce qu'on ne peut pas recréer sans l'imprimante sous
/// la main.
/// </summary>
public class PlancheRentreeProduitTests
{
    private static Product PlancheDuPoste() => new()
    {
        Code = "ID-FR-6",
        Name = "Photos d'identité — planche 10×15",
        WidthMm = 156.1,
        HeightMm = 105,
        PrinterName = "DP-DS620",
        Output = ProductOutput.Printer,
        Dpi = 300,
        Price = 6m,
        IccProfile = "DS620(MA)_Vivid.icm",
        DevmodeFile = "devmode-ID-FR-6.bin",
        PriceTiers = [new PriceTier { FromQuantity = 1, UnitPrice = 6m }],
        Sheet = new SheetSpec
        {
            Copies = 8,
            CellWidthMm = 35,
            CellHeightMm = 45,
            FullBleed = true,
            DateStamp = true,
        },
    };

    /// <summary>
    /// Le papier, la machine et les réglages pilote sont repris TELS QUELS : c'est tout
    /// l'intérêt de dériver plutôt que de créer.
    /// </summary>
    [Fact]
    public void La_derivation_garde_la_machine_et_les_reglages_pilote()
    {
        var source = PlancheDuPoste();
        var rentree = PlancheRentreeProduit.Deriver(source);

        Assert.Equal(source.WidthMm, rentree.WidthMm);
        Assert.Equal(source.HeightMm, rentree.HeightMm);
        Assert.Equal(source.PrinterName, rentree.PrinterName);
        Assert.Equal(source.Dpi, rentree.Dpi);
        Assert.Equal(source.IccProfile, rentree.IccProfile);
        Assert.Equal(source.DevmodeFile, rentree.DevmodeFile);
        Assert.Equal(source.Sheet!.CellWidthMm, rentree.Sheet!.CellWidthMm);
        Assert.Equal(source.Sheet.FullBleed, rentree.Sheet.FullBleed);
        Assert.Equal(source.Sheet.DateStamp, rentree.Sheet.DateStamp);
    }

    [Fact]
    public void La_derivation_pose_le_format_de_rentree()
    {
        var rentree = PlancheRentreeProduit.Deriver(PlancheDuPoste());

        Assert.Equal(PlancheRentreeProduit.Code, rentree.Code);
        Assert.True(rentree.Sheet!.GrandePhoto);
        Assert.Equal(PlancheDeRentree.IdentitesParDefaut, rentree.Sheet.Copies);
        Assert.Equal(11m, rentree.Price);
        Assert.True(rentree.Enabled);
    }

    /// <summary>
    /// Les paliers dégressifs de la planche ne suivent pas : on ne vend pas trente planches
    /// de rentrée au même client, et un tarif de gros sur un produit de saison se verrait
    /// en caisse.
    /// </summary>
    [Fact]
    public void Les_paliers_ne_suivent_pas()
    {
        Assert.Empty(PlancheRentreeProduit.Deriver(PlancheDuPoste()).PriceTiers);
    }

    /// <summary>
    /// Dériver ne doit RIEN changer à la planche d'origine — c'est le papier que la
    /// boutique tire toute la journée.
    /// </summary>
    [Fact]
    public void La_planche_dorigine_nest_pas_touchee()
    {
        var source = PlancheDuPoste();

        PlancheRentreeProduit.Deriver(source);

        Assert.Equal("ID-FR-6", source.Code);
        Assert.Equal(8, source.Sheet!.Copies);
        Assert.False(source.Sheet.GrandePhoto);
        Assert.Single(source.PriceTiers);
    }

    [Fact]
    public void Le_produit_existant_nest_jamais_retarife()
    {
        var deja = PlancheRentreeProduit.Deriver(PlancheDuPoste());
        deja.Price = 14m;   // l'exploitant a changé son prix au Catalogue

        var catalogue = new ProductCatalog([PlancheDuPoste(), deja]);
        var ajouts = 0;

        var trouve = PlancheRentreeProduit.Obtenir(catalogue, PlancheDuPoste(), _ => ajouts++);

        Assert.Equal(14m, trouve.Price);
        Assert.Equal(0, ajouts);
    }

    [Fact]
    public void Le_produit_absent_est_cree_une_fois()
    {
        var catalogue = new ProductCatalog([PlancheDuPoste()]);
        var ajoutes = new List<Product>();

        var cree = PlancheRentreeProduit.Obtenir(catalogue, PlancheDuPoste(), ajoutes.Add);

        Assert.Single(ajoutes);
        Assert.Equal(PlancheRentreeProduit.Code, cree.Code);
        Assert.True(cree.Sheet!.GrandePhoto);
    }

    /// <summary>
    /// Le produit dérivé porte VRAIMENT le format vendu : quatre cases françaises et un
    /// portrait tiennent sur son papier, et une cinquième n'y tient plus.
    ///
    /// C'est le seul essai qui relie le catalogue à la géométrie : un produit qui annonce
    /// quatre cases sur un papier qui n'en porte que trois échouerait à l'impression, après
    /// l'annonce du prix.
    /// </summary>
    [Fact]
    public void Le_papier_derive_porte_bien_quatre_cases_et_un_portrait()
    {
        var rentree = PlancheRentreeProduit.Deriver(PlancheDuPoste());
        var sheet = rentree.Sheet!;

        PlancheRentreeResult? Poser(int identites) => PlancheRentree.Layout(
            MmPx.ToPixels(rentree.WidthMm, rentree.Dpi),
            MmPx.ToPixels(rentree.HeightMm, rentree.Dpi),
            MmPx.ToPixels(35, rentree.Dpi),
            MmPx.ToPixels(45, rentree.Dpi),
            MmPx.ToPixels(sheet.LayoutGapMm, rentree.Dpi),
            identites,
            largeurMinimaleGrandePx:
                MmPx.ToPixels(PlancheRentree.LargeurMinimaleGrandeMm, rentree.Dpi));

        var pose = Poser(sheet.Copies);

        Assert.NotNull(pose);
        Assert.Equal(4, pose!.Identites.Count);
        Assert.InRange(MmPx.ToMm(pose.Grande.Width, rentree.Dpi), 80, 90);

        Assert.Null(Poser(sheet.Copies + 1));
    }

    /// <summary>
    /// Le catalogue est un fichier que quelqu'un peut avoir édité à la main : une « planche »
    /// sans réglages de planche ne doit pas faire tomber le comptoir.
    /// </summary>
    [Fact]
    public void Une_planche_sans_reglages_ne_fait_pas_echouer()
    {
        var bancal = PlancheDuPoste();
        bancal.Sheet = null;

        var rentree = PlancheRentreeProduit.Deriver(bancal);

        Assert.NotNull(rentree.Sheet);
        Assert.True(rentree.Sheet!.GrandePhoto);
        Assert.Equal(PlancheDeRentree.IdentitesParDefaut, rentree.Sheet.Copies);
    }
}
