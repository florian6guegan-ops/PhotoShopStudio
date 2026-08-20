using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le cadre du PORTRAIT, déduit de celui de l'identité : c'est ce qui permet de vendre une
/// planche de rentrée sans demander deux cadrages à l'opérateur.
///
/// La photo de référence est un portrait d'appareil : 3000 × 4000 px, cadre d'identité
/// serré sur le visage dans le haut de l'image — exactement ce que la détection pose.
/// </summary>
public class CadrageElargiTests
{
    private const double LargeurPx = 3000;
    private const double HauteurPx = 4000;

    /// <summary>La case du portrait sur la planche française : 84 × 105 mm.</summary>
    private const double GrandeL = 84;
    private const double GrandeH = 105;

    /// <summary>Un cadre d'identité plausible : 35 × 45 de rapport, visage en haut.</summary>
    private static CropSpec Identite => new(0.35, 0.12, 0.24, 0.2333);

    private static CropSpec Elargir(double facteur = CadrageElargi.FacteurParDefaut) =>
        CadrageElargi.Depuis(Identite, LargeurPx, HauteurPx, GrandeL, GrandeH, facteur);

    [Fact]
    public void Le_cadre_large_reste_dans_la_photo()
    {
        var cadre = Elargir();

        Assert.True(cadre.IsValid, $"cadre hors de la photo : {cadre}");
    }

    /// <summary>
    /// <b>Le rapport se juge en PIXELS, jamais en fractions.</b> Sur une photo 3:4, un cadre
    /// carré s'écrit 0,33 × 0,25 : comparer les fractions ferait croire à un portrait étiré,
    /// et c'est le piège que ce calcul doit éviter.
    /// </summary>
    [Fact]
    public void Le_cadre_large_a_le_rapport_de_la_case()
    {
        var cadre = Elargir();

        var rapport = cadre.Width * LargeurPx / (cadre.Height * HauteurPx);

        Assert.Equal(GrandeL / GrandeH, rapport, 2);
    }

    [Fact]
    public void Le_cadre_large_est_plus_grand_que_celui_de_lidentite()
    {
        var cadre = Elargir();

        Assert.True(cadre.Height > Identite.Height,
            "le portrait doit montrer plus que la case d'identité");
        Assert.True(cadre.Width > Identite.Width);
    }

    /// <summary>
    /// Ce qu'on gagne va d'abord vers le BAS : c'est là que sont les épaules. Un cadre qui
    /// s'ouvrirait autour du centre gagnerait surtout du plafond, et l'enfant se retrouverait
    /// petit au milieu d'un mur.
    /// </summary>
    [Fact]
    public void Le_portrait_gagne_surtout_les_epaules()
    {
        var cadre = Elargir();

        var gagneEnHaut = Identite.Y - cadre.Y;
        var gagneEnBas = (cadre.Y + cadre.Height) - (Identite.Y + Identite.Height);

        Assert.True(gagneEnBas > gagneEnHaut,
            $"le cadre s'ouvre trop vers le haut : {gagneEnHaut:0.000} contre {gagneEnBas:0.000}");
    }

    [Fact]
    public void Le_visage_reste_au_milieu_en_largeur()
    {
        var cadre = Elargir();

        var centreIdentite = Identite.X + Identite.Width / 2;
        var centreLarge = cadre.X + cadre.Width / 2;

        Assert.Equal(centreIdentite, centreLarge, 3);
    }

    /// <summary>
    /// Un visage cadré tout en haut de la photo : le cadre large ne peut pas s'ouvrir
    /// au-dessus, il doit donc se contenter du bord — jamais déborder.
    /// </summary>
    [Fact]
    public void Un_visage_au_bord_ne_fait_pas_deborder_le_cadre()
    {
        var colle = new CropSpec(0, 0, 0.24, 0.2333);

        var cadre = CadrageElargi.Depuis(colle, LargeurPx, HauteurPx, GrandeL, GrandeH);

        Assert.True(cadre.IsValid, $"cadre hors de la photo : {cadre}");
        Assert.True(cadre.X >= 0 && cadre.Y >= 0);
    }

    /// <summary>
    /// Une ouverture démesurée ne peut pas dépasser la photo : elle est ramenée AU RAPPORT,
    /// sans quoi le portrait sortirait étiré.
    /// </summary>
    [Fact]
    public void Une_ouverture_trop_grande_est_ramenee_au_rapport()
    {
        var cadre = Elargir(facteur: 20);

        Assert.True(cadre.IsValid);

        var rapport = cadre.Width * LargeurPx / (cadre.Height * HauteurPx);
        Assert.Equal(GrandeL / GrandeH, rapport, 2);
    }

    /// <summary>
    /// Rien à calculer, rien à inventer : sans cotes ni dimensions, on rend la photo
    /// entière, que le rendu saura toujours poser dans sa case.
    /// </summary>
    [Theory]
    [InlineData(0, 4000, 84, 105)]
    [InlineData(3000, 0, 84, 105)]
    [InlineData(3000, 4000, 0, 105)]
    [InlineData(3000, 4000, 84, 0)]
    public void Sans_cotes_utilisables_on_rend_la_photo_entiere(
        double largeur, double hauteur, double grandeL, double grandeH)
    {
        var cadre = CadrageElargi.Depuis(Identite, largeur, hauteur, grandeL, grandeH);

        Assert.Equal(CropSpec.Full, cadre);
    }
}
