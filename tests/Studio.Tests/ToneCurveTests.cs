using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// La courbe de tons est le cœur des corrections : exposition, noirs, blancs, ombres,
/// hautes lumières et contraste y sont réunis. Étant une fonction pure, elle se vérifie
/// au point près sans rien rendre.
/// </summary>
public class ToneCurveTests
{
    private static double Appliquer(double v, Action<ImageAdjustments> reglage)
    {
        var a = new ImageAdjustments();
        reglage(a);
        return ToneCurve.Apply(v, a);
    }

    [Fact]
    public void Sans_reglage_la_courbe_ne_change_rien()
    {
        var neutre = new ImageAdjustments();

        foreach (var v in new[] { 0, 0.25, 0.5, 0.75, 1.0 })
            Assert.Equal(v, ToneCurve.Apply(v, neutre), 6);

        Assert.True(ToneCurve.IsIdentity(neutre));
    }

    /// <summary>Un diaphragme de plus, c'est deux fois plus de lumière.</summary>
    [Fact]
    public void Un_diaphragme_double_la_lumiere()
    {
        Assert.Equal(0.5, Appliquer(0.25, a => a.Exposure = 1), 6);
        Assert.Equal(0.25, Appliquer(0.5, a => a.Exposure = -1), 6);
    }

    [Fact]
    public void La_courbe_reste_toujours_dans_les_bornes()
    {
        foreach (var v in new[] { 0, 0.1, 0.5, 0.9, 1.0 })
        {
            var haut = Appliquer(v, a => { a.Exposure = 3; a.Shadows = 100; a.Whites = 100; });
            var bas = Appliquer(v, a => { a.Exposure = -3; a.Highlights = -100; a.Blacks = -100; });

            Assert.InRange(haut, 0, 1);
            Assert.InRange(bas, 0, 1);
        }
    }

    /// <summary>
    /// Une courbe qui redescend inverserait des tons entre eux : un dégradé y perdrait son
    /// sens de lecture. Elle doit rester croissante quels que soient les réglages.
    /// </summary>
    [Fact]
    public void La_courbe_reste_croissante_meme_aux_reglages_extremes()
    {
        var extreme = new ImageAdjustments
        {
            Exposure = 1.5, Brightness = 60, Contrast = 80,
            Highlights = -100, Shadows = 100, Whites = 50, Blacks = -50,
        };

        var lut = ToneCurve.BuildLut(extreme);

        for (var i = 1; i < lut.Length; i++)
            Assert.True(lut[i] >= lut[i - 1] - 1e-9,
                $"la courbe redescend en {i} : {lut[i - 1]:F6} → {lut[i]:F6}");
    }

    /// <summary>
    /// Remonter les ombres doit déboucher un contre-jour sans délaver le reste : l'effet
    /// est fort dans les tons sombres et négligeable dans les clairs.
    /// </summary>
    [Fact]
    public void Les_ombres_agissent_sur_les_tons_sombres_pas_sur_les_clairs()
    {
        var sombre = Appliquer(0.1, a => a.Shadows = 100) - 0.1;
        var clair = Appliquer(0.9, a => a.Shadows = 100) - 0.9;

        Assert.True(sombre > 0.2, $"les ombres doivent remonter nettement (obtenu {sombre:F3})");
        Assert.True(clair < 0.01, $"les clairs doivent être épargnés (obtenu {clair:F3})");
    }

    /// <summary>Récupérer un ciel brûlé : l'effet porte sur les hautes lumières seules.</summary>
    [Fact]
    public void Les_hautes_lumieres_agissent_sur_les_tons_clairs_pas_sur_les_sombres()
    {
        var clair = Appliquer(0.9, a => a.Highlights = -100) - 0.9;
        var sombre = Appliquer(0.1, a => a.Highlights = -100) - 0.1;

        Assert.True(clair < -0.2, $"les hautes lumières doivent redescendre (obtenu {clair:F3})");
        Assert.True(Math.Abs(sombre) < 0.01, $"les sombres doivent être épargnés (obtenu {sombre:F3})");
    }

    /// <summary>Le contraste écarte les tons de part et d'autre du gris moyen, qui ne bouge pas.</summary>
    [Fact]
    public void Le_contraste_ecarte_les_tons_autour_du_gris_moyen()
    {
        Assert.Equal(0.5, Appliquer(0.5, a => a.Contrast = 100), 6);
        Assert.True(Appliquer(0.25, a => a.Contrast = 100) < 0.25);
        Assert.True(Appliquer(0.75, a => a.Contrast = 100) > 0.75);
    }

    [Fact]
    public void Un_contraste_negatif_rapproche_les_tons_du_gris_moyen()
    {
        Assert.True(Appliquer(0.25, a => a.Contrast = -100) > 0.25);
        Assert.True(Appliquer(0.75, a => a.Contrast = -100) < 0.75);
    }

    /// <summary>
    /// La luminosité agit en gamma : elle éclaircit sans brûler, donc le blanc pur reste
    /// blanc et le noir pur reste noir. C'est ce qui la distingue de l'exposition.
    /// </summary>
    [Fact]
    public void La_luminosite_preserve_le_noir_et_le_blanc()
    {
        Assert.Equal(0, Appliquer(0, a => a.Brightness = 100), 6);
        Assert.Equal(1, Appliquer(1, a => a.Brightness = 100), 6);
        Assert.True(Appliquer(0.5, a => a.Brightness = 100) > 0.5);
    }

    [Fact]
    public void Les_noirs_et_les_blancs_deplacent_les_points_d_ancrage()
    {
        Assert.True(Appliquer(0.1, a => a.Blacks = -100) < 0.1, "des noirs négatifs les creusent");
        Assert.True(Appliquer(0.1, a => a.Blacks = 100) > 0.1, "des noirs positifs les remontent");
        Assert.True(Appliquer(0.9, a => a.Whites = 100) > 0.9, "des blancs positifs les poussent");
    }

    /// <summary>Sans réglage tonal, on saute l'étape : inutile de faire traverser la table à l'image.</summary>
    [Fact]
    public void Un_reglage_purement_colore_ne_declenche_pas_la_courbe()
    {
        Assert.True(ToneCurve.IsIdentity(new ImageAdjustments { Saturation = 50, Temperature = 30 }));
        Assert.False(ToneCurve.IsIdentity(new ImageAdjustments { Exposure = 0.5 }));
    }

    [Fact]
    public void La_table_couvre_toute_l_echelle()
    {
        var lut = ToneCurve.BuildLut(new ImageAdjustments(), taille: 256);

        Assert.Equal(256, lut.Length);
        Assert.Equal(0, lut[0], 6);
        Assert.Equal(1, lut[^1], 6);
    }
}
