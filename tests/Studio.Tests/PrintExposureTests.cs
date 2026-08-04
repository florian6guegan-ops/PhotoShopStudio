using Studio.Core.Domain;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Correction d'exposition propre à un produit — l'écart de la MACHINE, pas celui de la
/// photo. La DS620 sort plus sombre que le minilab sur le même fichier ; signalé par
/// l'exploitant le 04/08/2026 sur les photos d'identité et les E-Photo.
/// </summary>
public class PrintExposureTests
{
    private static Product Produit(double exposition) => new()
    {
        Code = "ID-FR-6",
        Name = "Planche identité",
        WidthMm = 156.2,
        HeightMm = 104.9,
        PrinterName = "DP-DS620",
        PrintExposure = exposition,
    };

    [Fact]
    public void La_correction_du_produit_s_ajoute_a_l_exposition()
    {
        var reglages = new ImageAdjustments { Exposure = 0.5 };

        var corriges = PrintOrchestrator.AvecLaCorrectionDuProduit(reglages, Produit(0.25));

        Assert.Equal(0.75, corriges.Exposure, 3);
    }

    /// <summary>
    /// L'objet de l'ARTICLE n'est jamais touché : il appartient à la commande enregistrée.
    /// L'y ajouter ferait s'empiler la correction à chaque réimpression, et la troisième
    /// sortirait délavée.
    /// </summary>
    [Fact]
    public void Les_reglages_de_l_article_ne_sont_pas_modifies()
    {
        var reglages = new ImageAdjustments { Exposure = 0.5 };
        var produit = Produit(0.25);

        PrintOrchestrator.AvecLaCorrectionDuProduit(reglages, produit);
        PrintOrchestrator.AvecLaCorrectionDuProduit(reglages, produit);
        var troisieme = PrintOrchestrator.AvecLaCorrectionDuProduit(reglages, produit);

        Assert.Equal(0.5, reglages.Exposure, 3);
        Assert.Equal(0.75, troisieme.Exposure, 3);
    }

    /// <summary>
    /// Sans correction, on rend l'objet tel quel : pas de copie inutile sur les milliers de
    /// tirages du minilab, qui n'en ont pas besoin.
    /// </summary>
    [Fact]
    public void Sans_correction_les_reglages_passent_tels_quels()
    {
        var reglages = new ImageAdjustments { Exposure = 0.5 };

        Assert.Same(reglages, PrintOrchestrator.AvecLaCorrectionDuProduit(reglages, Produit(0)));
    }

    /// <summary>Le reste des réglages voyage avec — la copie est complète.</summary>
    [Fact]
    public void Les_autres_reglages_sont_conserves()
    {
        var reglages = new ImageAdjustments
        {
            WhiteBackground = true,
            Grayscale = true,
            Contrast = 12,
            Shadows = -8,
        };

        var corriges = PrintOrchestrator.AvecLaCorrectionDuProduit(reglages, Produit(0.25));

        Assert.True(corriges.WhiteBackground);
        Assert.True(corriges.Grayscale);
        Assert.Equal(12, corriges.Contrast, 3);
        Assert.Equal(-8, corriges.Shadows, 3);
    }

    /// <summary>
    /// Une correction négative doit rester possible : une machine peut aussi sortir trop
    /// clair, et le réglage ne vaudrait rien s'il ne marchait que dans un sens.
    /// </summary>
    [Fact]
    public void Une_correction_negative_assombrit()
    {
        var corriges = PrintOrchestrator.AvecLaCorrectionDuProduit(
            new ImageAdjustments(), Produit(-0.3));

        Assert.Equal(-0.3, corriges.Exposure, 3);
    }
}
