using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// La planche de la RENTRÉE : quelques photos d'identité, et un portrait sur ce qu'elles
/// laissent.
///
/// Les mesures portent sur la planche que la boutique vend — le 10×15 de la DNP, couché
/// (156,1 × 105 mm), cases françaises de 35 × 45 —, parce que c'est celle dont les cotes
/// se vérifient une règle à la main sur le tirage.
/// </summary>
public class PlancheRentreeTests
{
    private const int Dpi = 300;

    private static int SheetW => MmPx.ToPixels(156.1, Dpi);   // 1843
    private static int SheetH => MmPx.ToPixels(105, Dpi);     // 1240
    private static int CellW => MmPx.ToPixels(35, Dpi);       // 413
    private static int CellH => MmPx.ToPixels(45, Dpi);       // 531

    /// <summary>L'écart d'une planche à fond perdu : un millimètre, où le trait s'inscrit.</summary>
    private static int Gap => MmPx.ToPixels(1, Dpi);          // 12

    private static PlancheRentreeResult Poser(int identites = 4, int reserve = 0) =>
        PlancheRentree.Layout(SheetW, SheetH, CellW, CellH, Gap, identites,
            bottomReserve: reserve,
            largeurMinimaleGrandePx: MmPx.ToPixels(PlancheRentree.LargeurMinimaleGrandeMm, Dpi))
        ?? throw new InvalidOperationException("la planche devrait tenir");

    [Fact]
    public void Quatre_identites_tiennent_en_deux_colonnes_de_deux()
    {
        var planche = Poser();

        Assert.Equal(4, planche.Identites.Count);
        Assert.Equal(2, planche.Colonnes);
        Assert.Equal(2, planche.Rangees);
    }

    /// <summary>
    /// C'est la mesure qui décide de tout le format : ce qui reste au portrait quand quatre
    /// cases françaises sont posées. Plus de huit centimètres de large — un vrai portrait,
    /// pas une bande.
    /// </summary>
    [Fact]
    public void Le_portrait_prend_plus_de_huit_centimetres_de_large()
    {
        var planche = Poser();

        var largeurMm = MmPx.ToMm(planche.Grande.Width, Dpi);
        var hauteurMm = MmPx.ToMm(planche.Grande.Height, Dpi);

        Assert.InRange(largeurMm, 83, 86);
        Assert.InRange(hauteurMm, 104, 105);
    }

    [Fact]
    public void Le_portrait_est_a_droite_du_bloc_didentites()
    {
        var planche = Poser();

        Assert.All(planche.Identites, c => Assert.True(c.Right <= planche.Grande.X,
            $"une case d'identité déborde sur le portrait : {c.Right} > {planche.Grande.X}"));
    }

    /// <summary>
    /// Deux photos qui se recouvrent, c'est une planche à refaire et un client qui attend :
    /// on le vérifie case par case plutôt que de s'en remettre au calcul.
    /// </summary>
    [Fact]
    public void Aucune_case_nen_recouvre_une_autre()
    {
        var cases = Poser().Toutes;

        for (var i = 0; i < cases.Count; i++)
        for (var j = i + 1; j < cases.Count; j++)
        {
            var a = cases[i];
            var b = cases[j];
            var seChevauchent = a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
            Assert.False(seChevauchent, $"les cases {i} et {j} se recouvrent");
        }
    }

    [Fact]
    public void Tout_tient_dans_la_feuille()
    {
        foreach (var c in Poser().Toutes)
        {
            Assert.InRange(c.X, 0, SheetW - c.Width);
            Assert.InRange(c.Y, 0, SheetH - c.Height);
        }
    }

    /// <summary>
    /// LA BANDE EST PRISE SUR LE BLOC D'IDENTITÉS SEUL, et le portrait descend jusqu'au
    /// bord de la feuille.
    ///
    /// ⚠ <b>C'est l'inverse de ce que cet essai exigeait avant le 20/08/2026</b>, et le
    /// renversement est voulu. La réserve courait sous toute la planche ; le portrait,
    /// taillé pour occuper exactement ce qui restait, ramenait donc la bande à son MINIMUM
    /// — 4,5 mm utiles, sous le plancher des 6 mm en dessous duquel <c>SheetFooterLayout</c>
    /// ne garde que la date. La planche de rentrée ne pouvait pas porter « PHOTOS
    /// CONFORMES » ni le nom de la boutique : pas par oubli, par géométrie.
    ///
    /// Sous le seul bloc d'identités, la bande trouve une dizaine de millimètres, et le
    /// portrait récupère au passage la hauteur qu'il cédait.
    /// </summary>
    [Fact]
    public void La_bande_basse_est_prise_sur_le_bloc_didentites_seul()
    {
        var reserve = MmPx.ToPixels(6, Dpi);

        var sans = Poser();
        var avec = Poser(reserve: reserve);

        // le portrait ne cède rien : il va au bord, avec ou sans bande
        Assert.Equal(SheetH, avec.Grande.Bottom);
        Assert.Equal(sans.Grande.Bottom, avec.Grande.Bottom);

        // ce sont les CASES qui remontent
        var basDesCases = avec.Identites.Max(c => c.Bottom);
        Assert.True(basDesCases <= SheetH - reserve,
            $"les cases descendent à {basDesCases}, la bande commence à {SheetH - reserve}");

        // et la bande est sous elles, sur LEUR largeur — pas sous le portrait
        Assert.Equal(basDesCases, avec.BandeBasse.Y);
        Assert.Equal(SheetH, avec.BandeBasse.Bottom);
        Assert.True(avec.BandeBasse.Right <= avec.Grande.X,
            "la bande mord sur le portrait");
    }

    /// <summary>
    /// Les cases s'empilent EN HAUTEUR d'abord : c'est ce qui laisse au portrait un morceau
    /// large d'un seul tenant. Trois cases font donc deux rangées et deux colonnes, jamais
    /// trois colonnes d'une seule case.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    public void Les_cases_sempilent_en_hauteur_dabord(int identites, int colonnes, int rangees)
    {
        var planche = Poser(identites);

        Assert.Equal(colonnes, planche.Colonnes);
        Assert.Equal(rangees, planche.Rangees);
        Assert.Equal(identites, planche.Identites.Count);
    }

    /// <summary>
    /// Au-delà de ce que le papier peut porter avec un portrait, on refuse. Rendre une
    /// planche dont la grande photo se réduit à une lisière serait pire qu'un refus :
    /// l'opérateur ne le verrait qu'au tirage.
    /// </summary>
    /// <summary>
    /// <b>Quatre est le maximum sur la planche française</b>, et ce n'est pas un choix
    /// d'affichage : une troisième colonne de cases ne laisse que 48 mm au portrait, sous
    /// le minimum. C'est ce qui borne le compteur de l'écran de cadrage.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void Trop_didentites_ne_laissent_pas_de_portrait(int identites)
    {
        var mini = MmPx.ToPixels(PlancheRentree.LargeurMinimaleGrandeMm, Dpi);

        Assert.Null(PlancheRentree.Layout(SheetW, SheetH, CellW, CellH, Gap, identites,
            largeurMinimaleGrandePx: mini));
    }

    /// <summary>
    /// Sans minimum imposé, la disposition continue de se poser tant qu'il reste un pixel :
    /// c'est le minimum qui juge, pas la géométrie — elle, elle mesure.
    /// </summary>
    [Fact]
    public void Sans_minimum_six_cases_se_posent_encore()
    {
        var planche = PlancheRentree.Layout(SheetW, SheetH, CellW, CellH, Gap, 6);

        Assert.NotNull(planche);
        Assert.Equal(3, planche.Colonnes);
        Assert.Equal(2, planche.Rangees);
    }

    [Fact]
    public void Une_case_plus_grande_que_le_papier_ne_tient_pas()
    {
        Assert.Null(PlancheRentree.Layout(SheetW, SheetH, SheetW + 1, CellH, Gap, 4));
        Assert.Null(PlancheRentree.Layout(SheetW, SheetH, CellW, SheetH + 1, Gap, 4));
    }

    [Fact]
    public void Zero_identite_na_pas_de_sens()
    {
        Assert.Null(PlancheRentree.Layout(SheetW, SheetH, CellW, CellH, Gap, 0));
    }

    /// <summary>
    /// Les cases gardent LEUR cote, au pixel près : une photo d'identité sous-cotée se fait
    /// refuser au guichet, et c'est le premier travers qu'une planche doit éviter.
    /// </summary>
    [Fact]
    public void Les_cases_gardent_la_cote_de_la_norme()
    {
        Assert.All(Poser().Identites, c =>
        {
            Assert.Equal(CellW, c.Width);
            Assert.Equal(CellH, c.Height);
        });
    }
}
