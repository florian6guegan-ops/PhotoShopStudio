using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Référentiel des 274 documents d'identité repris de DiLand. La boutique reçoit des
/// demandes de toute l'Europe et au-delà : chaque pays a ses cotes, et se tromper de
/// format fait refuser le dossier au guichet.
/// </summary>
public class IdDocumentCatalogTests
{
    private static readonly Lazy<IReadOnlyList<IdDocumentSpec>> Documents = new(() =>
        IdDocumentCatalog.Load(Chemin()));

    private static string Chemin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidat = Path.Combine(dir.FullName, "catalog", "diland-id-documents.json");
            if (File.Exists(candidat)) return candidat;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("catalog/diland-id-documents.json introuvable.");
    }

    private static IdDocumentSpec Trouver(string pays, string document) =>
        Documents.Value.Single(d => d.Country == pays && d.Document == document);

    [Fact]
    public void Le_referentiel_se_charge()
    {
        Assert.NotEmpty(Documents.Value);
        Assert.True(Documents.Value.Count > 250, $"seulement {Documents.Value.Count} documents chargés");
    }

    [Fact]
    public void Les_documents_sont_classes_par_pays()
    {
        var pays = Documents.Value.Select(d => d.Country).ToList();
        Assert.Equal(pays.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase), pays);
    }

    [Theory]
    [InlineData("France", "Passport", 35, 45)]
    [InlineData("Spain", "Passport", 26, 32)]
    [InlineData("Canada", "Passport", 50, 70)]
    [InlineData("Japan", "ID Card", 35, 45)]
    public void Les_cotes_correspondent_a_DiLand(string pays, string document, double largeur, double hauteur)
    {
        var spec = Trouver(pays, document);

        Assert.Equal(largeur, spec.WidthMm, 2);
        Assert.Equal(hauteur, spec.HeightMm, 2);
    }

    [Fact]
    public void Le_document_francais_porte_les_bornes_de_visage()
    {
        var spec = Trouver("France", "Passport");

        Assert.True(spec.HasHeadBounds);
        Assert.Equal(32, spec.HeadMinMm, 2);
        Assert.Equal(36, spec.HeadMaxMm, 2);

        // TargetHeadMm est exprimée dans les unités de l'estimateur : ramenée à la norme,
        // elle vaut bien le milieu des bornes.
        Assert.Equal(34, spec.TargetHeadMm / IdPhotoFr.SurestimationDeLEstimateur, 2);
    }

    /// <summary>
    /// Certains documents ne précisent aucune borne de visage : il faut le savoir plutôt
    /// que de cadrer au hasard en croyant contrôler la conformité.
    /// </summary>
    [Fact]
    public void Les_documents_sans_borne_de_visage_sont_signales()
    {
        var sansBorne = Documents.Value.Where(d => !d.HasHeadBounds).ToList();

        Assert.All(sansBorne, d => Assert.True(d.IsUsable, $"{d.Label} devrait rester utilisable"));
        Assert.All(sansBorne, d => Assert.True(d.TargetHeadMm > 0, $"{d.Label} : hauteur visée nulle"));
    }

    [Fact]
    public void Toutes_les_cotes_sont_plausibles()
    {
        Assert.All(Documents.Value, d =>
        {
            Assert.InRange(d.WidthMm, 15, 150);
            Assert.InRange(d.HeightMm, 15, 150);
        });
    }

    [Fact]
    public void La_marge_au_dessus_du_crane_laisse_place_au_visage()
    {
        Assert.All(Documents.Value, d =>
            Assert.True(d.TargetCrownMarginMm + d.TargetHeadMm <= d.HeightMm,
                $"{d.Label} : la tête déborde du tirage"));
    }

    // — recherche —

    [Fact]
    public void La_recherche_vide_renvoie_tout()
    {
        Assert.Equal(Documents.Value.Count, IdDocumentCatalog.Search(Documents.Value, "").Count());
        Assert.Equal(Documents.Value.Count, IdDocumentCatalog.Search(Documents.Value, null).Count());
    }

    [Fact]
    public void La_recherche_trouve_un_pays_sans_tenir_compte_de_la_casse()
    {
        var trouves = IdDocumentCatalog.Search(Documents.Value, "fran").ToList();

        Assert.NotEmpty(trouves);
        Assert.All(trouves, d => Assert.Contains("fran", d.Country, StringComparison.CurrentCultureIgnoreCase));
    }

    [Fact]
    public void La_recherche_porte_aussi_sur_le_type_de_document()
    {
        var trouves = IdDocumentCatalog.Search(Documents.Value, "visa").ToList();

        Assert.NotEmpty(trouves);
        Assert.All(trouves, d => Assert.True(
            d.Country.Contains("visa", StringComparison.CurrentCultureIgnoreCase)
            || d.Document.Contains("visa", StringComparison.CurrentCultureIgnoreCase)));
    }

    [Fact]
    public void Le_libelle_affiche_le_pays_le_document_et_les_cotes()
    {
        var libelle = Trouver("France", "Passport").Label;

        Assert.Contains("France", libelle);
        Assert.Contains("35", libelle);
        Assert.Contains("45", libelle);
    }

    /// <summary>La référence française codée en dur doit rester alignée sur le référentiel.</summary>
    [Fact]
    public void La_reference_francaise_reste_alignee()
    {
        var reference = IdDocumentSpec.France;
        var diland = Trouver("France", "Passport");

        Assert.Equal(diland.WidthMm, reference.WidthMm, 2);
        Assert.Equal(diland.HeightMm, reference.HeightMm, 2);
        Assert.Equal(diland.HeadMinMm, reference.HeadMinMm, 2);
        Assert.Equal(diland.HeadMaxMm, reference.HeadMaxMm, 2);
    }
}
