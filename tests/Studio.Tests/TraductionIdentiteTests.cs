using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le référentiel des 274 documents, dit en français — demandé le 18/08/2026 : « les noms
/// des autres formats doivent être écrits en français, et pas en anglais ».
///
/// Ce qui se vérifie ici n'est pas la table elle-même — elle se relit — mais les deux
/// choses qu'une traduction casse quand on n'y prend pas garde : les raccourcis déjà
/// enregistrés d'un poste, et la recherche.
/// </summary>
public class TraductionIdentiteTests
{
    private static readonly Lazy<IReadOnlyList<IdDocumentSpec>> Documents =
        new(() => IdDocumentCatalog.Load(IdDocumentCatalogTests.CheminDuReferentiel()));

    [Theory]
    [InlineData("Spain", "Espagne")]
    [InlineData("Germany", "Allemagne")]
    [InlineData("United States", "États-Unis")]
    [InlineData("United Kingdom", "Royaume-Uni")]
    // les fautes de frappe du référentiel sont des clés à part entière
    [InlineData("Combodia", "Cambodge")]
    [InlineData("Uzbeskistan", "Ouzbékistan")]
    [InlineData("Andora", "Andorre")]
    public void Les_pays_sont_dits_en_francais(string anglais, string francais) =>
        Assert.Equal(francais, TraductionIdentite.Pays(anglais));

    [Theory]
    [InlineData("Passport", "Passeport")]
    [InlineData("ID Card", "Carte d'identité")]
    [InlineData("VISA", "Visa")]
    [InlineData("Patente nautica", "Permis bateau")]
    public void Les_documents_sont_dits_en_francais(string anglais, string francais) =>
        Assert.Equal(francais, TraductionIdentite.Document(anglais));

    /// <summary>
    /// Un pays que la table ne connaît pas garde son nom : un référentiel corrigé ou
    /// complété par un poste ne doit jamais faire DISPARAÎTRE une ligne de l'écran.
    /// </summary>
    [Fact]
    public void Un_pays_inconnu_garde_son_nom()
    {
        Assert.Equal("Wakanda", TraductionIdentite.Pays("Wakanda"));
        Assert.Equal("Laissez-passer", TraductionIdentite.Document("Laissez-passer"));
        Assert.Equal("", TraductionIdentite.Pays(null));
    }

    [Fact]
    public void Le_referentiel_charge_est_en_francais()
    {
        var espagne = Documents.Value.Single(d => d.CountryEn == "Spain" && d.DocumentEn == "Passport");

        Assert.Equal("Espagne", espagne.Country);
        Assert.Equal("Passeport", espagne.Document);
    }

    /// <summary>
    /// ⚠ LE POINT QUI COÛTERAIT UNE TUILE. Les raccourcis d'un poste sont enregistrés sous
    /// « Pays|Type » — en anglais pour tous ceux réglés avant la traduction. Le picker les
    /// omet quand il ne les retrouve pas : la tuile disparaîtrait de l'écran sans un mot.
    /// </summary>
    [Fact]
    public void Un_raccourci_enregistre_en_anglais_retrouve_son_document()
    {
        var trouve = IdDocumentCatalog.FindByKey(Documents.Value, "Spain|Passport");

        Assert.NotNull(trouve);
        Assert.Equal("Espagne", trouve.Country);
    }

    [Fact]
    public void Un_raccourci_enregistre_en_francais_retrouve_son_document()
    {
        var trouve = IdDocumentCatalog.FindByKey(Documents.Value, "Espagne|Passeport");

        Assert.NotNull(trouve);
        Assert.Equal("Espagne", trouve.Country);
    }

    /// <summary>
    /// L'opérateur tape ce qu'il a sous les yeux : le formulaire du client dit souvent
    /// « Spain » quand l'écran affiche « Espagne ». Les deux doivent trouver.
    /// </summary>
    [Theory]
    [InlineData("espagne")]
    [InlineData("spain")]
    public void La_recherche_repond_aux_deux_langues(string tape)
    {
        var trouves = IdDocumentCatalog.Search(Documents.Value, tape).ToList();

        Assert.NotEmpty(trouves);
        Assert.All(trouves, d => Assert.Equal("Espagne", d.Country));
    }
}
