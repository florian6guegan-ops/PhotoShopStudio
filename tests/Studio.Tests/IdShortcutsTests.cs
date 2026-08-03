using Studio.Core.Catalog;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

public class IdShortcutsTests
{
    /// <summary>
    /// Le raccourci « France » livré par défaut doit tomber sur une norme réelle.
    ///
    /// Le référentiel DiLand ne contient aucun « Passeport / CNI » — il a « ID Card »,
    /// « Passport » et « Visa ». Sans le rattrapage vers la norme de la boutique, la tuile
    /// France disparaissait de l'écran sans le moindre message.
    /// </summary>
    [Fact]
    public void RaccourciFranceParDefaut_SeResout()
    {
        var referentiel = new[]
        {
            new IdDocumentSpec("France", "ID Card", 35, 45, 32, 36),
            new IdDocumentSpec("Espagne", "Passport", 26, 32, 25, 29),
        };

        var france = IdShortcuts.Defaults.Single(r => r.Kind == IdShortcutKind.Document);
        var trouve = IdDocumentCatalog.FindByKey(referentiel, france.Cle);

        Assert.NotNull(trouve);
        Assert.Equal("France", trouve!.Country);
        Assert.Equal(35, trouve.WidthMm);
        Assert.Equal(45, trouve.HeightMm);
    }

    /// <summary>Une entrée du référentiel se retrouve par sa clé, casse comprise.</summary>
    [Fact]
    public void FindByKey_TrouveUneEntreeDuReferentiel()
    {
        var referentiel = new[] { new IdDocumentSpec("Espagne", "Passport", 26, 32, 25, 29) };

        Assert.NotNull(IdDocumentCatalog.FindByKey(referentiel, "Espagne|Passport"));
        Assert.NotNull(IdDocumentCatalog.FindByKey(referentiel, "espagne|passport"));
        Assert.Null(IdDocumentCatalog.FindByKey(referentiel, "Espagne|Visa"));
        Assert.Null(IdDocumentCatalog.FindByKey(referentiel, "sans-separateur"));
    }

    /// <summary>
    /// Un fichier absent rend les raccourcis par défaut plutôt qu'une liste vide : l'écran
    /// doit rester utilisable sur une installation neuve.
    /// </summary>
    [Fact]
    public void Load_SansFichier_RendLesDefauts()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "studio-raccourcis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            Assert.Equal(IdShortcuts.Defaults, IdShortcuts.Load(dossier));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    /// <summary>Ce qu'on enregistre est ce qu'on relit, dans le même ordre.</summary>
    [Fact]
    public void SaveePuisLoad_ConserveOrdreEtContenu()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "studio-raccourcis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var voulu = new[]
            {
                new IdShortcut(IdShortcutKind.Produit, "e-photo-dnp", "E-Photo"),
                new IdShortcut(IdShortcutKind.Document, "Espagne|Passport", "Espagne"),
            };

            IdShortcuts.Save(dossier, voulu);
            var relu = IdShortcuts.Load(dossier);

            Assert.Equal(voulu, relu);
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    /// <summary>
    /// Une liste vide est un choix légitime — « aucun raccourci, tout par la recherche » —
    /// et ne doit pas faire réapparaître les défauts au prochain démarrage.
    /// </summary>
    [Fact]
    public void Load_ListeVideEnregistree_ResteVide()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "studio-raccourcis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            IdShortcuts.Save(dossier, []);
            Assert.Empty(IdShortcuts.Load(dossier));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }
}
