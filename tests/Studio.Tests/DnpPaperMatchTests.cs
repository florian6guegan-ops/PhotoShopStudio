using Studio.Core.Domain;
using Studio.Imaging.Geometry;
using Studio.Printing;
using Xunit;

namespace Studio.Tests;

/// <summary>
/// Choix du format de papier sur une imprimante à sublimation.
///
/// Ce que ces essais protègent : les planches d'identité des 01 et 02/08/2026 sont parties
/// au spouleur et ne sont jamais sorties. Le pilote DP-DS620 ne déclare QUE ses onze formes
/// privées (RawKind 119 à 129) ; le produit était enregistré à 152 × 102 mm quand la forme
/// « (6x4) » en fait 156,2 × 104,9. L'ancienne recherche n'admettait que 1,5 mm d'écart,
/// ne trouvait rien, et fabriquait un format personnalisé (RawKind 0, DMPAPER_USER) que la
/// machine jette en silence.
///
/// Les cotes ci-dessous sont celles relevées sur la DS620 de la boutique, en centièmes de
/// pouce — l'unité de System.Drawing.Printing.
/// </summary>
public class DnpPaperMatchTests
{
    /// <summary>Les onze formes déclarées par le pilote DP-DS620, dans son ordre.</summary>
    private static readonly (int Width, int Height)[] Ds620 =
    [
        (516, 363),  // (5x3.5)
        (516, 513),  // (5x5)
        (615, 413),  // (6x4)
        (615, 612),  // (6x6)
        (516, 713),  // (5x7)
        (615, 812),  // (6x8)
        (363, 516),  // PR (3.5x5)
        (413, 615),  // PR (4x6)
        (615, 913),  // (6x9)
        (615, 462),  // (6x4.5)
        (462, 615),  // PR (4.5x6)
    ];

    private const int Le6x4 = 2;
    private const int LePr4x6 = 7;

    private static int Choisir(double largeurMm, double hauteurMm) =>
        BitmapPrinter.ChoisirFormat(Ds620,
            (int)Math.Round(largeurMm / 25.4 * 100),
            (int)Math.Round(hauteurMm / 25.4 * 100));

    [Fact]
    public void La_planche_didentite_corrigee_tombe_sur_le_6x4()
    {
        // 156,2 × 104,9 mm : les cotes exactes de la forme (6x4)
        Assert.Equal(Le6x4, Choisir(156.2, 104.9));
    }

    [Fact]
    public void Le_meme_format_en_portrait_donne_la_meme_forme_retournee()
    {
        // le 10×15 de la DS620 est déclaré 105 × 156,1 mm : c'est « PR (4x6) »
        Assert.Equal(LePr4x6, Choisir(105, 156.1));
    }

    [Fact]
    public void Les_anciennes_cotes_ne_trouvent_aucune_forme()
    {
        // 152 × 102 : 4,2 mm de moins que le (6x4). L'étirer d'autant donnerait des photos
        // d'identité de 36 mm de large au lieu de 35 — refusées au guichet. Mieux vaut le
        // dire à l'opérateur que sortir une planche fausse (ou, comme avant, rien du tout).
        Assert.Equal(-1, Choisir(152, 102));
    }

    [Fact]
    public void Une_forme_plus_petite_que_le_tirage_nest_jamais_retenue()
    {
        // 160 × 110 mm ne tient dans aucune forme : le (6x4) est plus court, le (6x6)
        // beaucoup trop long. Retenir le (6x4) rognerait 4 mm de photo en silence.
        Assert.Equal(-1, Choisir(160, 110));
    }

    [Fact]
    public void Entre_deux_formes_possibles_la_plus_proche_gagne()
    {
        // 156,2 × 116 mm : le (6x4.5) fait 156,2 × 117,3 — le (6x6), 156,2 × 155,4, est
        // largement plus grand et ne doit pas l'emporter
        Assert.Equal(9, Choisir(156.2, 117.3));
    }

    [Fact]
    public void Un_ecart_dun_demi_millimetre_reste_admis()
    {
        // les cotes du catalogue sont arrondies au dixième : elles ne tomberont jamais au
        // centième de pouce près, et un demi-millimètre ne se voit pas sur un tirage
        Assert.Equal(Le6x4, Choisir(156.5, 105.2));
    }

    // ----- la cellule suit le document, pas le produit -----

    /// <summary>
    /// Le papier de la boutique : la forme (6x4) de la DS620, à 300 ppp.
    /// </summary>
    private static (int Largeur, int Hauteur) Planche =>
        (MmPx.ToPixels(156.2, 300), MmPx.ToPixels(104.9, 300));

    private static int Capacite(double celluleLargeurMm, double celluleHauteurMm) =>
        IdSheetLayout.MaxCopies(
            Planche.Largeur, Planche.Hauteur,
            MmPx.ToPixels(celluleLargeurMm, 300),
            MmPx.ToPixels(celluleHauteurMm, 300),
            MmPx.ToPixels(SheetSpec.DefaultGapMm, 300));

    [Fact]
    public void Le_format_francais_donne_bien_huit_photos()
    {
        Assert.Equal(8, Capacite(35, 45));
    }

    [Fact]
    public void Un_document_plus_petit_donne_plus_de_photos()
    {
        // passeport espagnol : 26 × 32 mm. Avec la cellule figée du produit, la planche
        // sortait en 35 × 45 quel que soit le pays choisi à l'écran précédent.
        Assert.True(Capacite(26, 32) > Capacite(35, 45),
            "un document plus petit doit tenir en plus grand nombre sur le même papier");
    }

    [Fact]
    public void Un_document_trop_grand_pour_le_papier_donne_zero()
    {
        // 110 mm de haut sur une planche de 104,9 : aucune case ne tient. L'écran doit
        // écarter ce papier de la liste plutôt que le proposer puis échouer à l'impression.
        Assert.Equal(0, Capacite(80, 110));
    }

    [Fact]
    public void La_cellule_de_larticle_lemporte_sur_celle_du_produit()
    {
        var produit = new Product
        {
            Code = "ID-FR-6",
            WidthMm = 156.2,
            HeightMm = 104.9,
            Dpi = 300,
            Sheet = new SheetSpec { Copies = 8, CellWidthMm = 35, CellHeightMm = 45 },
        };

        var article = new OrderItem { SheetCellWidthMm = 26, SheetCellHeightMm = 32 };

        // c'est le calcul que fait PrintOrchestrator avant d'appeler RenderIdSheetToFile
        var largeur = article.SheetCellWidthMm ?? produit.Sheet!.CellWidthMm;
        var hauteur = article.SheetCellHeightMm ?? produit.Sheet!.CellHeightMm;

        Assert.Equal(26, largeur);
        Assert.Equal(32, hauteur);

        // et sans valeur sur l'article, on retombe sur le produit — les commandes déjà
        // enregistrées ne portent pas ces champs
        var ancien = new OrderItem();
        Assert.Equal(35, ancien.SheetCellWidthMm ?? produit.Sheet!.CellWidthMm);
        Assert.Equal(45, ancien.SheetCellHeightMm ?? produit.Sheet!.CellHeightMm);
    }
}
