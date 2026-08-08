using Studio.Core.Catalog;

namespace Studio.Tests;

/// <summary>
/// Le rapprochement entre le nom d'un produit et ses cotes.
///
/// <b>Le tirage de quatre centimètres, 08/08/2026.</b> Un poste équipé d'un traceur grand
/// format a sorti un « 40×50 » en 4 × 5 cm : le produit était réglé sur 40 × 50
/// millimètres. Les noms du métier sont en centimètres, les cotes en millimètres — lire
/// « 40x50 » et saisir 40 puis 50 est le raisonnement le plus naturel du monde.
/// </summary>
public class CotesProduitTests
{
    private static (double, double)? Verdict(string nom, double l, double h) =>
        CotesProduit.SiSaisiEnCentimetres(nom, null, l, h);

    /// <summary>
    /// <b>Le cas réel.</b> « 40x50 » mesurant 40 × 50 mm : on propose 400 × 500.
    /// </summary>
    [Fact]
    public void Le_quarante_par_cinquante_du_08_08_est_attrape()
    {
        Assert.Equal((400d, 500d), Verdict("40x50", 40, 50));
    }

    /// <summary>
    /// <b>Aucun faux positif sur le catalogue réel.</b> Ces cotes sont celles de la
    /// boutique, relevées dans products.json : un « 10x15 » fait 102 × 152 mm, un
    /// « 20x30 » fait 203 × 307. Les écarts au nom sont la norme, pas l'exception.
    /// </summary>
    [Theory]
    [InlineData("10x10", 102, 102)]
    [InlineData("10x15", 102, 152)]
    [InlineData("13x13", 127, 127)]
    [InlineData("13x18", 127, 180)]
    [InlineData("15x20", 152, 203)]
    [InlineData("15x30", 152, 304)]
    [InlineData("20x25", 203, 256)]
    [InlineData("20x30", 203, 307)]
    [InlineData("21x29,7", 210, 297)]
    public void Le_catalogue_de_la_boutique_ne_declenche_rien(string nom, double l, double h)
    {
        Assert.Null(Verdict(nom, l, h));
    }

    /// <summary>
    /// Se tromper d'ORIENTATION n'est pas se tromper d'unité : un « 10x15 » saisi en
    /// paysage reste un 10×15.
    /// </summary>
    [Fact]
    public void L_orientation_inversee_ne_declenche_rien()
    {
        Assert.Null(Verdict("10x15", 152, 102));
    }

    /// <summary>
    /// La correction proposée garde l'ORDRE saisi : un produit saisi 50 × 40 devient
    /// 500 × 400, et non 400 × 500. L'opérateur a choisi son orientation, on ne corrige
    /// que l'unité.
    /// </summary>
    [Fact]
    public void La_correction_garde_l_orientation_choisie()
    {
        Assert.Equal((500d, 400d), Verdict("40x50", 50, 40));
        Assert.Equal((400d, 500d), Verdict("40x50", 40, 50));
    }

    /// <summary>
    /// Un nom sans format lisible ne se juge pas — la planche d'identité s'appelle
    /// « Photos d'identité — planche 10×15 » et mesure 156,1 × 105 mm, ce qui est juste.
    /// </summary>
    [Theory]
    [InlineData("Envoi des photos par courriel", 102, 152)]
    [InlineData("Agrandissement sur mesure", 400, 500)]
    public void Un_nom_sans_format_ne_declenche_rien(string nom, double l, double h)
    {
        Assert.Null(Verdict(nom, l, h));
    }

    /// <summary>
    /// La planche d'identité de la boutique : son nom porte « 10×15 » et elle mesure
    /// 156,1 × 105 mm — c'est le papier avec son débord, et c'est juste. Elle ne doit pas
    /// être signalée.
    /// </summary>
    [Fact]
    public void La_planche_identite_de_la_boutique_ne_declenche_rien()
    {
        Assert.Null(Verdict("Photos d'identité — planche 10×15", 156.1, 105));
    }

    /// <summary>Le code sert quand le nom ne porte pas de format.</summary>
    [Fact]
    public void Le_code_est_essaye_a_defaut_du_nom()
    {
        Assert.Equal((400d, 500d),
            CotesProduit.SiSaisiEnCentimetres("Grand tirage", "40x50", 40, 50));
    }

    /// <summary>Des cotes absentes ou absurdes ne se jugent pas.</summary>
    [Theory]
    [InlineData(0, 50)]
    [InlineData(40, 0)]
    [InlineData(-40, -50)]
    public void Des_cotes_nulles_ne_declenchent_rien(double l, double h)
    {
        Assert.Null(Verdict("40x50", l, h));
    }

    /// <summary>
    /// Un produit dont les cotes n'ont rien à voir avec son nom n'est pas notre affaire :
    /// on ne signale QUE le rapport de dix, qui ne s'invente pas.
    /// </summary>
    [Fact]
    public void Des_cotes_sans_rapport_avec_le_nom_ne_declenchent_rien()
    {
        Assert.Null(Verdict("10x15", 250, 300));
    }
}
