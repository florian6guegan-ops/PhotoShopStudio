using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le prix d'une planche d'identité, qui dépend du DOCUMENT et non du papier :
/// 10 € pour un document français, 15 € pour un étranger.
/// </summary>
public class TarifsIdentiteTests
{
    [Fact]
    public void Le_tarif_par_defaut_est_celui_de_la_boutique()
    {
        var tarifs = new TarifsIdentite();

        Assert.Equal(10m, tarifs.FranceEur);
        Assert.Equal(15m, tarifs.EtrangerEur);
    }

    [Fact]
    public void Un_document_francais_est_facture_dix_euros()
    {
        Assert.Equal(10m, new TarifsIdentite().Pour("France"));
        Assert.Equal(10m, new TarifsIdentite().Pour(IdDocumentSpec.France.Country));
    }

    [Theory]
    [InlineData("Espagne")]
    [InlineData("Algérie")]
    [InlineData("États-Unis")]
    [InlineData("")]
    [InlineData(null)]
    public void Tout_le_reste_est_facture_quinze_euros(string? pays)
    {
        Assert.Equal(15m, new TarifsIdentite().Pour(pays));
    }

    /// <summary>
    /// Le référentiel écrit « France » ; une saisie peut écrire autrement. Cinq euros de
    /// plus pour une majuscule serait un défaut visible en caisse.
    /// </summary>
    [Theory]
    [InlineData("france")]
    [InlineData("FRANCE")]
    [InlineData("  France  ")]
    public void La_casse_et_les_espaces_ne_changent_pas_le_prix(string pays)
    {
        Assert.Equal(10m, new TarifsIdentite().Pour(pays));
    }

    [Fact]
    public void Les_deux_tarifs_se_reglent()
    {
        var tarifs = new TarifsIdentite { FranceEur = 12m, EtrangerEur = 18m };

        Assert.Equal(12m, tarifs.Pour("France"));
        Assert.Equal(18m, tarifs.Pour("Portugal"));
    }
}
