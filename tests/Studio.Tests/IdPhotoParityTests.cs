using System.Text.Json;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Vérifie que notre planche d'identité respecte les mêmes cotes que DiLand.
///
/// Une photo d'identité non conforme est refusée au guichet, et le client revient. La
/// référence est donc contrôlée automatiquement, contre les 274 spécifications relevées
/// dans DiLand le 31/07/2026, plutôt que d'être tenue de mémoire.
/// </summary>
public class IdPhotoParityTests
{
    private sealed record DocumentIdentite(
        string Pays,
        string Document,
        double LargeurMm,
        double HauteurMm,
        double VisageHauteurMm,
        double VisageHauteurMaxMm);

    private static readonly Lazy<IReadOnlyList<DocumentIdentite>> Documents = new(Charger);

    private static IReadOnlyList<DocumentIdentite> Charger()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidat = Path.Combine(dir.FullName, "catalog", "diland-id-documents.json");
            if (File.Exists(candidat))
            {
                using var flux = File.OpenRead(candidat);
                using var json = JsonDocument.Parse(flux);
                return json.RootElement.GetProperty("Documents").EnumerateArray()
                    .Select(e => new DocumentIdentite(
                        e.GetProperty("Pays").GetString() ?? "",
                        e.GetProperty("Document").GetString() ?? "",
                        e.GetProperty("LargeurMm").GetDouble(),
                        e.GetProperty("HauteurMm").GetDouble(),
                        e.GetProperty("VisageHauteurMm").GetDouble(),
                        e.GetProperty("VisageHauteurMaxMm").GetDouble()))
                    .ToList();
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("catalog/diland-id-documents.json introuvable.");
    }

    private static DocumentIdentite France(string document) =>
        Documents.Value.Single(d => d.Pays == "France" && d.Document == document);

    [Fact]
    public void Le_referentiel_de_DiLand_est_complet()
    {
        Assert.Equal(274, Documents.Value.Count);
        Assert.Contains(Documents.Value, d => d.Pays == "France");
    }

    /// <summary>Le format du tirage : 35 × 45 mm, la norme française.</summary>
    [Theory]
    [InlineData("Passport")]
    [InlineData("ID Card")]
    [InlineData("Visa")]
    public void Le_format_francais_correspond_a_celui_de_DiLand(string document)
    {
        var reference = France(document);

        Assert.Equal(IdPhotoFr.PhotoWidthMm, reference.LargeurMm, 2);
        Assert.Equal(IdPhotoFr.PhotoHeightMm, reference.HauteurMm, 2);
    }

    /// <summary>
    /// La hauteur du visage est le critère qui fait refuser une photo au guichet :
    /// 32 mm minimum, 36 mm maximum.
    /// </summary>
    [Fact]
    public void Les_bornes_du_visage_correspondent_a_celles_de_DiLand()
    {
        var reference = France("Passport");

        Assert.Equal(IdPhotoFr.HeadMinMm, reference.VisageHauteurMm, 2);
        Assert.Equal(IdPhotoFr.HeadMaxMm, reference.VisageHauteurMaxMm, 2);
    }

    [Fact]
    public void La_hauteur_visee_tombe_entre_les_deux_bornes()
    {
        Assert.InRange(IdPhotoFr.TargetHeadMm, IdPhotoFr.HeadMinMm, IdPhotoFr.HeadMaxMm);
    }

    /// <summary>Les cotes du référentiel sont en millimètres, pas en unités internes.</summary>
    [Fact]
    public void Le_referentiel_est_bien_en_millimetres()
    {
        // une photo d'identité mesure quelques centimètres : au-delà de 150 mm, la
        // conversion depuis les unités de DiLand n'a pas été faite
        foreach (var document in Documents.Value.Where(d => d.LargeurMm > 0))
        {
            Assert.InRange(document.LargeurMm, 15, 150);
            Assert.InRange(document.HauteurMm, 15, 150);
        }
    }

    /// <summary>
    /// Quelques repères étrangers, pour que le référentiel ne dérive pas non plus sur
    /// les autres pays — la boutique reçoit des demandes de toute l'Europe.
    /// </summary>
    [Theory]
    [InlineData("Italy", "Passaporto", 35, 40)]
    [InlineData("Spain", "ID Card", 26, 32)]
    [InlineData("Japan", "ID Card", 35, 45)]
    public void Les_formats_etrangers_sont_conformes(string pays, string document, double largeur, double hauteur)
    {
        var reference = Documents.Value.Single(d => d.Pays == pays && d.Document == document);

        Assert.Equal(largeur, reference.LargeurMm, 2);
        Assert.Equal(hauteur, reference.HauteurMm, 2);
    }
}
