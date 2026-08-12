using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// Le profil couleur qui manque, et qui ne doit rien empêcher.
///
/// <b>L'après-midi du 12/08/2026, sur le poste DESKTOP-KT88VDM.</b> Son catalogue venait
/// d'être repris et réclamait <c>DS620-R0.icc</c> ; son pilote DNP l'avait installé sous le
/// nom <c>PD_DS620-R0.icc</c>. L'import ne cherchait que le nom exact, le profil n'a pas
/// été posé — et le rendu levait alors une <c>FileNotFoundException</c> qui emportait toute
/// la commande :
///
/// <code>
/// Impression : commande 12-002 en échec | System.IO.FileNotFoundException:
///   Could not find file '...\catalog\icc\DS620-R0.icc'.
/// </code>
///
/// Deux défauts, deux séries d'essais : le profil doit se retrouver malgré le préfixe du
/// fabricant, et son absence ne doit JAMAIS coûter le tirage.
/// </summary>
public class ProfilIccManquantTests : IDisposable
{
    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "ProfilIcc-" + Guid.NewGuid().ToString("N"));

    private string Couleur => Path.Combine(_racine, "color");
    private string Catalogue => Path.Combine(_racine, "catalog");

    public ProfilIccManquantTests()
    {
        Directory.CreateDirectory(Couleur);
        Directory.CreateDirectory(Catalogue);
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* au mieux */ }
    }

    private void PoserLeCatalogue(string profilReclame)
    {
        ProductCatalog.Save(Path.Combine(Catalogue, "products.json"), new[]
        {
            new Product
            {
                Code = "ID-FR-6", Name = "Planche identité",
                WidthMm = 156.1, HeightMm = 105,
                PrinterName = "DP-DS620", IccProfile = profilReclame, Price = 10m,
            },
        });
    }

    private void PoserLeProfil(string nomDeFichier) =>
        File.WriteAllBytes(Path.Combine(Couleur, nomDeFichier), new byte[128]);

    /// <summary>Le cas nominal, celui de Maisons-Alfort : le nom tombe juste.</summary>
    [Fact]
    public void Un_profil_au_nom_exact_est_importe()
    {
        PoserLeCatalogue("DS620-R0.icc");
        PoserLeProfil("DS620-R0.icc");

        var importes = CatalogueLivre.ImporterLesProfilsManquants(Catalogue, Couleur);

        Assert.Contains("DS620-R0.icc", importes);
        Assert.True(File.Exists(Path.Combine(Catalogue, "icc", "DS620-R0.icc")));
    }

    /// <summary>
    /// LE cas de KT88VDM : le pilote a préfixé le nom. Le profil doit être retrouvé, et
    /// posé sous le nom que le catalogue réclame.
    /// </summary>
    [Fact]
    public void Un_profil_prefixe_par_le_pilote_est_retrouve()
    {
        PoserLeCatalogue("DS620-R0.icc");
        PoserLeProfil("PD_DS620-R0.icc");

        var importes = CatalogueLivre.ImporterLesProfilsManquants(Catalogue, Couleur);

        Assert.Contains("DS620-R0.icc", importes);
        Assert.True(File.Exists(Path.Combine(Catalogue, "icc", "DS620-R0.icc")),
            "le fichier doit être posé sous le nom que le catalogue réclame");
    }

    /// <summary>
    /// La limite : un préfixe doit se terminer proprement. Sans cela on rapprocherait deux
    /// profils qui n'ont rien à voir, et le tirage sortirait avec les couleurs d'une autre
    /// machine — pire qu'un tirage sans gestion des couleurs, parce que personne ne le
    /// verrait venir.
    /// </summary>
    [Fact]
    public void Un_nom_qui_finit_par_hasard_pareil_n_est_pas_pris()
    {
        PoserLeCatalogue("DS620-R0.icc");
        PoserLeProfil("MONDS620-R0.icc");

        var importes = CatalogueLivre.ImporterLesProfilsManquants(Catalogue, Couleur);

        Assert.Empty(importes);
        Assert.False(File.Exists(Path.Combine(Catalogue, "icc", "DS620-R0.icc")));
    }

    /// <summary>
    /// Un profil qu'on ne trouve nulle part ne fait pas échouer l'import : le poste
    /// démarre, et le tirage partira sans gestion des couleurs.
    /// </summary>
    [Fact]
    public void Un_profil_introuvable_ne_fait_pas_echouer_l_import()
    {
        PoserLeCatalogue("PAS-LA.icc");

        var importes = CatalogueLivre.ImporterLesProfilsManquants(Catalogue, Couleur);

        Assert.Empty(importes);
    }

    /// <summary>
    /// <b>Le plus important.</b> Un profil illisible ne doit pas lever : la gestion des
    /// couleurs est un raffinement, le tirage est le métier.
    /// </summary>
    [Fact]
    public void Un_profil_illisible_ne_fait_pas_echouer_le_rendu()
    {
        var source = Path.Combine(_racine, "photo.png");
        using (var image = new ImageMagick.MagickImage(
            ImageMagick.MagickColors.CadetBlue, 600, 400))
            image.Write(source);

        var sortie = Path.Combine(_racine, "rendu.png");

        var demande = new Studio.Imaging.RenderRequest(
            SourcePath: source,
            TargetWidthPx: 600, TargetHeightPx: 400,
            Crop: new CropSpec(0, 0, 1, 1),
            RotationQuarterTurns: 0, FineRotationDegrees: 0,
            Fit: FitMode.Fill, BorderPx: 0,
            Adjustments: new ImageAdjustments(),
            IccProfilePath: Path.Combine(_racine, "profil-qui-n-existe-pas.icc"));

        var dit = new List<string>();
        Studio.Imaging.ImagePipeline.Log = dit.Add;
        try
        {
            Studio.Imaging.ImagePipeline.RenderToFile(demande, sortie, 300);
        }
        finally
        {
            Studio.Imaging.ImagePipeline.Log = null;
        }

        Assert.True(File.Exists(sortie), "le tirage doit sortir même sans profil");
        Assert.Contains(dit, m => m.Contains("sRGB", StringComparison.OrdinalIgnoreCase));
    }
}
