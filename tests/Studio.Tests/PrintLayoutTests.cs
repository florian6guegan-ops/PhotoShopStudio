using Studio.Printing.LargeFormat;

namespace Studio.Tests;

/// <summary>
/// Géométrie de la boîte d'impression grand format. Les valeurs de référence viennent
/// du format A3+ de l'Epson SC-P800 (329 × 483 mm) utilisé à l'atelier.
/// </summary>
public class PrintLayoutTests
{
    private const double A3PlusWidthMm = 329;
    private const double A3PlusHeightMm = 483;

    // une image de 4000 × 6000 px à 300 ppp fait 338,67 × 508 mm à 100 %
    private const int WidthPx = 4000;
    private const int HeightPx = 6000;
    private const double SourceDpi = 300;

    [Fact]
    public void La_taille_a_cent_pour_cent_decoule_de_la_resolution()
    {
        var (largeur, hauteur) = PrintLayout.NaturalSizeMm(WidthPx, HeightPx, SourceDpi);

        Assert.Equal(338.67, largeur, 2);
        Assert.Equal(508.00, hauteur, 2);
    }

    [Fact]
    public void A_cent_pour_cent_l_image_garde_sa_resolution()
    {
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm);

        Assert.Equal(100, p.ScalePercent, 3);
        Assert.Equal(300, p.EffectiveDpi, 3);
    }

    [Fact]
    public void Reduire_l_echelle_augmente_la_resolution_obtenue()
    {
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, scalePercent: 50);

        Assert.Equal(169.33, p.WidthMm, 2);
        Assert.Equal(254.00, p.HeightMm, 2);
        Assert.Equal(600, p.EffectiveDpi, 3);
    }

    [Fact]
    public void Agrandir_l_echelle_diminue_la_resolution_obtenue()
    {
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, scalePercent: 200);

        Assert.Equal(150, p.EffectiveDpi, 3);
    }

    [Fact]
    public void Le_centrage_repartit_la_marge_de_part_et_d_autre()
    {
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm,
            scalePercent: 50, center: true);

        Assert.Equal((A3PlusWidthMm - p.WidthMm) / 2, p.LeftMm, 3);
        Assert.Equal((A3PlusHeightMm - p.HeightMm) / 2, p.TopMm, 3);
    }

    [Fact]
    public void Sans_centrage_les_decalages_saisis_sont_respectes()
    {
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm,
            scalePercent: 50, center: false, topMm: 12, leftMm: 7);

        Assert.Equal(12, p.TopMm, 3);
        Assert.Equal(7, p.LeftMm, 3);
    }

    /// <summary>« Ajuster au support » : l'image touche le bord le plus contraignant, sans déborder.</summary>
    [Fact]
    public void Ajuster_au_support_fait_tenir_l_image_dans_la_feuille()
    {
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, fitToMedia: true);

        Assert.False(p.OverflowsPaper(A3PlusWidthMm, A3PlusHeightMm));
        // l'image est plus haute que large par rapport à la feuille : c'est la hauteur qui limite
        Assert.Equal(A3PlusHeightMm, p.HeightMm, 2);
        Assert.True(p.WidthMm <= A3PlusWidthMm);
        Assert.True(p.ScalePercent < 100, "l'image devait être réduite pour tenir");
    }

    [Fact]
    public void Ajuster_au_support_agrandit_une_image_trop_petite()
    {
        // 1000 × 1500 px à 300 ppp = 84,7 × 127 mm, bien plus petit que l'A3+
        var p = PrintLayout.Compute(1000, 1500, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, fitToMedia: true);

        Assert.True(p.ScalePercent > 100, "l'image devait être agrandie pour remplir le support");
        Assert.False(p.OverflowsPaper(A3PlusWidthMm, A3PlusHeightMm));
    }

    [Fact]
    public void Ajuster_au_support_ignore_l_echelle_saisie()
    {
        var ajuste = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm,
            scalePercent: 500, fitToMedia: true);
        var reference = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm,
            scalePercent: 10, fitToMedia: true);

        Assert.Equal(reference.ScalePercent, ajuste.ScalePercent, 6);
    }

    /// <summary>Un tirage trop grand doit être signalé, pas imprimé en silence à moitié.</summary>
    [Fact]
    public void Un_tirage_plus_grand_que_la_feuille_est_signale()
    {
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, scalePercent: 100);

        Assert.True(p.OverflowsPaper(A3PlusWidthMm, A3PlusHeightMm));
    }

    [Fact]
    public void Un_tirage_qui_tient_n_est_pas_signale()
    {
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, scalePercent: 60);

        Assert.False(p.OverflowsPaper(A3PlusWidthMm, A3PlusHeightMm));
    }

    [Fact]
    public void L_echelle_pour_une_largeur_visee_est_exacte()
    {
        var echelle = PrintLayout.ScaleForWidth(WidthPx, SourceDpi, targetWidthMm: 200);
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, echelle);

        Assert.Equal(200, p.WidthMm, 3);
    }

    [Fact]
    public void L_echelle_pour_une_hauteur_visee_est_exacte()
    {
        var echelle = PrintLayout.ScaleForHeight(HeightPx, SourceDpi, targetHeightMm: 400);
        var p = PrintLayout.Compute(WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, echelle);

        Assert.Equal(400, p.HeightMm, 3);
    }

    [Theory]
    [InlineData(PrintUnits.Millimeters, 100, 100)]
    [InlineData(PrintUnits.Centimeters, 100, 10)]
    [InlineData(PrintUnits.Inches, 25.4, 1)]
    public void Les_conversions_d_unites_sont_reversibles(PrintUnits unite, double mm, double attendu)
    {
        var affiche = PrintLayout.FromMm(mm, unite);

        Assert.Equal(attendu, affiche, 6);
        Assert.Equal(mm, PrintLayout.ToMm(affiche, unite), 6);
    }

    [Fact]
    public void Une_image_sans_pixel_est_refusee()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintLayout.NaturalSizeMm(0, 100, SourceDpi));
    }

    [Fact]
    public void Une_resolution_nulle_est_refusee()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintLayout.NaturalSizeMm(100, 100, 0));
    }

    [Fact]
    public void Une_echelle_nulle_est_refusee()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintLayout.Compute(
            WidthPx, HeightPx, SourceDpi, A3PlusWidthMm, A3PlusHeightMm, scalePercent: 0));
    }

    [Fact]
    public void Un_format_papier_nul_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintLayout.Compute(
            WidthPx, HeightPx, SourceDpi, 0, A3PlusHeightMm));
    }
}

/// <summary>Contrôles de cohérence des réglages avant impression.</summary>
public class LargeFormatPrintSettingsTests
{
    private static LargeFormatPrintSettings Valides() => new()
    {
        PrinterName = "EPSONFECE59 (SC-P800 Series)",
        Copies = 1,
    };

    [Fact]
    public void Des_reglages_corrects_ne_signalent_rien()
    {
        Assert.Empty(Valides().Validate());
    }

    [Fact]
    public void Une_imprimante_manquante_est_signalee()
    {
        var reglages = Valides();
        reglages.PrinterName = "";

        Assert.Contains(reglages.Validate(), m => m.Contains("imprimante", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Un_nombre_de_copies_invalide_est_signale()
    {
        var reglages = Valides();
        reglages.Copies = 0;

        Assert.Contains(reglages.Validate(), m => m.Contains("copies", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Le piège de la double conversion : si Studio convertit sans profil, ou si le pilote
    /// convertit aussi, les couleurs sortent fausses. On refuse plutôt que d'imprimer.
    /// </summary>
    [Fact]
    public void Gerer_les_couleurs_sans_profil_est_signale()
    {
        var reglages = Valides();
        reglages.ColorHandling = ColorHandling.ApplicationManagesColor;
        reglages.PrinterProfile = null;

        Assert.Contains(reglages.Validate(), m => m.Contains("profil ICC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gerer_les_couleurs_avec_un_profil_est_accepte()
    {
        var reglages = Valides();
        reglages.ColorHandling = ColorHandling.ApplicationManagesColor;
        reglages.PrinterProfile = @"catalog\icc\SC-P800 Canvas Matte.icc";

        Assert.Empty(reglages.Validate());
    }

    [Fact]
    public void Laisser_l_imprimante_gerer_ne_reclame_aucun_profil()
    {
        var reglages = Valides();
        reglages.ColorHandling = ColorHandling.PrinterManagesColor;

        Assert.Empty(reglages.Validate());
    }

    [Fact]
    public void La_copie_est_independante_de_l_original()
    {
        var original = Valides();
        original.DevModeBytes = [1, 2, 3];

        var copie = original.Clone();
        copie.ScalePercent = 42;
        copie.DevModeBytes![0] = 99;

        Assert.Equal(100, original.ScalePercent);
        Assert.Equal(1, original.DevModeBytes[0]);
        Assert.Equal(42, copie.ScalePercent);
    }
}
