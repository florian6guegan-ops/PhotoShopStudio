using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// La conformité doit se juger avec les cotes DU DOCUMENT VISÉ.
///
/// Elle se jugeait avec les millimètres français (marge de crâne 2 à 7 mm, centrage 2 mm)
/// quel que soit le document. Sur 121 des 274 normes du référentiel, le cadrage
/// mathématiquement idéal était alors déclaré NON CONFORME : le gabarit restait orange et
/// l'écran conseillait de « monter le cadre », ce qui le rendait vraiment faux.
/// </summary>
public class IdComplianceFormatsTests
{
    /// <summary>Le cadrage idéal d'un document est conforme à ce même document.</summary>
    [Theory]
    // France : la norme de la boutique, celle qui marchait déjà
    [InlineData(35, 45, 32, 36)]
    // les formats carrés qui échouaient : marge idéale 9 mm, hors des 7 mm français
    [InlineData(50, 50, 35, 40)]
    [InlineData(51, 51, 35, 40)]
    // très haut : marge idéale ~15 mm
    [InlineData(38, 63, 25, 35)]
    // petit format : marge idéale ~2,5 mm
    [InlineData(26, 32, 25, 29)]
    public void CadrageIdeal_EstConforme(double largeur, double hauteur, double visageMin, double visageMax)
    {
        var spec = new IdDocumentSpec("Essai", "Essai", largeur, hauteur, visageMin, visageMax);

        // une tête posée telle que le cadrage idéal tienne entièrement dans l'image
        const int imageW = 2000, imageH = 3000;
        var tete = new NormRect(0.40, 0.25, 0.20, 0.22);

        var crop = IdPhotoFr.ComputeCrop(tete, imageW, imageH, spec);
        var verdict = IdPhotoFr.Check(crop, tete, spec);

        Assert.True(verdict.HeadHeightOk,
            $"hauteur de tête {verdict.HeadHeightMm:0.0} mm hors de {spec.HeadMinMm}–{spec.HeadMaxMm}");
        Assert.True(verdict.CrownOk,
            $"marge de crâne {verdict.CrownMarginMm:0.0} mm hors de " +
            $"{spec.CrownMarginMinMm:0.0}–{spec.CrownMarginMaxMm:0.0}");
        Assert.True(verdict.CenteredOk, $"centrage {verdict.CenterOffsetMm:0.0} mm");
        Assert.True(verdict.Compliant);
    }

    /// <summary>
    /// Les bornes suivent la cible : le cadrage visé pour la France doit rester conforme
    /// après le calage sur DiLand (marge de crâne ramenée à 1,75 mm le 03/08/2026).
    ///
    /// C'est la garantie qui compte : quelle que soit la cible retenue, le cadrage idéal
    /// ne doit jamais être déclaré non conforme, sinon l'écran conseille de le défaire.
    /// </summary>
    [Fact]
    public void France_LaCibleResteDansSesBornes()
    {
        var france = IdDocumentSpec.France;

        Assert.InRange(france.TargetCrownMarginMm, france.CrownMarginMinMm, france.CrownMarginMaxMm);
        Assert.Equal(2.0, france.CenterToleranceMm, 6);

        // il reste du battement de part et d'autre : un cadrage manuel un peu haut ou un
        // peu bas ne doit pas basculer à l'orange au moindre millimètre
        Assert.True(france.CrownMarginMaxMm - france.CrownMarginMinMm >= 4.0,
            $"battement trop étroit : {france.CrownMarginMinMm:0.##}–{france.CrownMarginMaxMm:0.##} mm");
    }

    /// <summary>Un cadrage franchement décalé reste refusé — les bornes ne sont pas devenues laxistes.</summary>
    [Fact]
    public void CadrageFranchementFaux_EstRefuse()
    {
        var spec = new IdDocumentSpec("Essai", "Essai", 50, 50, 35, 40);
        var tete = new NormRect(0.40, 0.25, 0.20, 0.22);

        var ideal = IdPhotoFr.ComputeCrop(tete, 2000, 3000, spec);

        // le cadre descendu d'un cinquième de sa hauteur : le crâne se retrouve trop haut
        var decale = ideal with { Y = ideal.Y + ideal.Height * 0.2 };
        Assert.False(IdPhotoFr.Check(decale, tete, spec).CrownOk);

        // et nettement décalé sur le côté
        var deporte = ideal with { X = ideal.X + ideal.Width * 0.25 };
        Assert.False(IdPhotoFr.Check(deporte, tete, spec).CenteredOk);
    }
}
