using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// La planche d'index : les vignettes de N photos différentes, numérotées, sur un tirage.
///
/// Ce qui est vérifié ici est ce que DiLand rate (planches de la boutique examinées le
/// 01/08/2026) : le REMPLISSAGE. Sa grille est fixe à vingt-quatre vignettes, si bien que
/// vingt-sept photos sortaient sur deux tirages 10×15 dont le second portait trois
/// vignettes. Le papier perdu ne se voit pas dans un test de rendu — il se voit ici.
/// </summary>
public class IndexSheetTests
{
    /// <summary>Un 10×15 à 300 dpi, le tirage courant de la boutique.</summary>
    private const int Largeur = 1795;   // 152 mm
    private const int Hauteur = 1205;   // 102 mm
    private const int Dpi = 300;

    private static int Px(double mm) => MmPx.ToPixels(mm, Dpi);

    private static IndexSheetLayout.Plan Planche(int photos, double vignetteMinimaleMm = 15) =>
        IndexSheetLayout.Compute(
            Largeur, Hauteur, photos,
            margin: Px(4), gap: Px(2), headerHeight: Px(8), footerHeight: Px(5),
            labelHeight: Px(3.5), minThumbWidth: Px(vignetteMinimaleMm));

    /// <summary>
    /// Le cas exact de la boutique : vingt-sept photos doivent tenir sur UNE planche.
    /// C'est la raison d'être de ce module.
    /// </summary>
    [Fact]
    public void Vingt_sept_photos_tiennent_sur_une_seule_planche()
    {
        var plan = Planche(27);

        Assert.Equal(1, plan.Pages);
        Assert.True(plan.PerPage >= 27, $"capacité {plan.PerPage}, attendu au moins 27");
    }

    /// <summary>
    /// Les cellules ne doivent ni se chevaucher ni sortir du tirage — sans quoi une
    /// vignette mordrait sur sa voisine, ce qui ne se verrait qu'une fois le papier sorti.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(27)]
    [InlineData(36)]
    public void Les_cellules_restent_dans_le_tirage_et_ne_se_croisent_pas(int photos)
    {
        var plan = Planche(photos);

        foreach (var cellule in plan.Cells)
        {
            Assert.True(cellule.X >= 0 && cellule.Y >= 0, "cellule hors du tirage (haut/gauche)");
            Assert.True(cellule.Right <= Largeur, $"cellule débordante à droite ({cellule.Right})");
            Assert.True(cellule.Bottom <= Hauteur, $"cellule débordante en bas ({cellule.Bottom})");
        }

        for (var i = 0; i < plan.Cells.Count; i++)
            for (var j = i + 1; j < plan.Cells.Count; j++)
                Assert.False(SeCroisent(plan.Cells[i], plan.Cells[j]),
                    $"les cellules {i} et {j} se chevauchent");
    }

    private static bool SeCroisent(PixelRect a, PixelRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    /// <summary>
    /// Toutes les photos doivent trouver une place : autant de cellules que la planche en
    /// annonce, et assez de planches pour tout porter.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    [InlineData(60)]
    [InlineData(200)]
    public void Aucune_photo_n_est_laissee_de_cote(int photos)
    {
        var plan = Planche(photos);

        Assert.Equal(plan.PerPage, plan.Cells.Count);
        Assert.True(plan.PerPage * plan.Pages >= photos,
            $"{plan.Pages} planches de {plan.PerPage} ne portent pas {photos} photos");
    }

    /// <summary>
    /// Quand il faut plusieurs planches, elles se partagent le travail à parts égales.
    ///
    /// C'est le reproche fait à DiLand : une planche pleine suivie d'une planche à trois
    /// vignettes, c'est un tirage 10×15 payé pour du blanc.
    /// </summary>
    [Fact]
    public void Deux_planches_se_partagent_les_photos_a_parts_egales()
    {
        // une vignette minimale exigeante force le passage à deux planches
        var plan = Planche(40, vignetteMinimaleMm: 22);

        Assert.True(plan.Pages >= 2, "ce cas doit demander plusieurs planches");

        var surLaDerniere = 40 - plan.PerPage * (plan.Pages - 1);
        Assert.True(surLaDerniere >= plan.PerPage - plan.Pages,
            $"dernière planche trop vide : {surLaDerniere} vignettes contre {plan.PerPage}");
    }

    /// <summary>
    /// Plus il y a de photos, plus les vignettes rapetissent — mais jamais en dessous du
    /// seuil : c'est lui qui décide d'ouvrir une seconde planche plutôt que de rendre la
    /// première illisible.
    /// </summary>
    [Fact]
    public void Une_vignette_ne_descend_jamais_sous_le_seuil()
    {
        var seuil = Px(15);

        foreach (var photos in new[] { 1, 12, 27, 48, 120 })
        {
            var plan = Planche(photos);
            var cellule = plan.Cells[0];
            var vignette = IndexSheetLayout.PlaceVignette(cellule, Px(3.5), 3.0 / 2.0);

            Assert.True(vignette.Width >= seuil,
                $"{photos} photos : vignette de {vignette.Width}px, seuil {seuil}px");
        }
    }

    /// <summary>
    /// La grille doit tirer parti de la forme du tirage : sur un 10×15 couché, une planche
    /// de six vignettes doit s'étaler en largeur, pas en hauteur.
    /// </summary>
    [Fact]
    public void La_grille_suit_la_forme_du_tirage()
    {
        var plan = Planche(6);

        Assert.True(plan.Columns > plan.Rows,
            $"grille {plan.Columns}×{plan.Rows} : elle devrait être plus large que haute");
    }

    /// <summary>
    /// La vignette est posée entière, centrée, et au-dessus de la place du numéro — quel
    /// que soit le sens de la photo.
    /// </summary>
    [Theory]
    [InlineData(3.0 / 2.0)]
    [InlineData(2.0 / 3.0)]
    [InlineData(1.0)]
    public void La_vignette_tient_dans_sa_cellule_sans_manger_le_numero(double rapport)
    {
        var plan = Planche(27);
        var cellule = plan.Cells[5];
        var numero = Px(3.5);

        var vignette = IndexSheetLayout.PlaceVignette(cellule, numero, rapport);

        Assert.True(vignette.X >= cellule.X && vignette.Right <= cellule.Right, "déborde en largeur");
        Assert.True(vignette.Y >= cellule.Y, "déborde en haut");
        Assert.True(vignette.Bottom <= cellule.Bottom - numero + 1,
            "la vignette mord sur la place du numéro");

        // les proportions de la photo sont gardées : une planche qui étire les visages
        // ne sert à rien pour choisir
        Assert.Equal(rapport, vignette.Width / (double)vignette.Height, 1);
    }
}
