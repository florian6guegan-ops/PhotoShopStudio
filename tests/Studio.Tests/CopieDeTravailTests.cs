using Studio.App.Infrastructure;

namespace Studio.Tests;

/// <summary>
/// La copie de travail d'une photo de client : son nom, et la clé qui nomme ses pixels
/// auprès du cache des masques de détourage.
///
/// <b>Ce que ces essais protègent, et c'est le cœur de l'historique des trente jours :</b>
/// une photo qui change de dossier ne doit pas repayer son détourage. Elle change pourtant de
/// dossier plusieurs fois dans sa vie — le support du client, la copie du jour, la copie d'un
/// jour suivant quand on la rouvre. On nomme la PHOTO, jamais l'endroit où elle est.
///
/// Le défaut d'origine tenait en un appel : l'empreinte venait de <c>chemin.GetHashCode()</c>,
/// que .NET tire au sort à chaque démarrage — le nom de la copie changeait donc au moindre
/// déplacement, et même d'un lancement à l'autre.
/// </summary>
public class CopieDeTravailTests : IDisposable
{
    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "Copie-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Une photo dans un dossier, avec une date d'écriture imposée.</summary>
    private string Photo(string dossier, string nom, string contenu = "des pixels")
    {
        var chemin = Path.Combine(_racine, dossier);
        Directory.CreateDirectory(chemin);

        var fichier = Path.Combine(chemin, nom);
        File.WriteAllText(fichier, contenu);
        File.SetLastWriteTimeUtc(fichier, new DateTime(2026, 8, 19, 10, 30, 0, DateTimeKind.Utc));
        return fichier;
    }

    [Fact]
    public void La_meme_photo_dans_deux_dossiers_porte_la_meme_cle()
    {
        var surLaCarte = Photo("carte", "IMG_1234.jpg");
        var copieDuJour = Photo("20260819", "IMG_1234.jpg");

        Assert.Equal(CopieDeTravail.Cle(surLaCarte), CopieDeTravail.Cle(copieDuJour));
    }

    [Fact]
    public void Deux_photos_differentes_ne_partagent_pas_leur_cle()
    {
        var une = Photo("carte", "IMG_1234.jpg", "des pixels");
        var autre = Photo("carte2", "IMG_1234.jpg", "d'autres pixels, plus longs");

        Assert.NotEqual(CopieDeTravail.Cle(une), CopieDeTravail.Cle(autre));
    }

    [Fact]
    public void Un_fichier_absent_na_pas_de_cle()
    {
        Assert.Null(CopieDeTravail.Cle(Path.Combine(_racine, "jamais-ecrit.jpg")));
        Assert.Null(CopieDeTravail.Cle(null));
        Assert.Null(CopieDeTravail.Cle("   "));
    }

    [Fact]
    public void Le_nom_de_la_copie_ne_depend_pas_du_dossier()
    {
        var surLaCarte = Photo("carte", "IMG_1234.jpg");
        var ailleurs = Photo("telephone", "IMG_1234.jpg");

        Assert.Equal(
            CopieDeTravail.Nom("IMG_1234.jpg", surLaCarte),
            CopieDeTravail.Nom("IMG_1234.jpg", ailleurs));
    }

    [Fact]
    public void Le_nom_de_la_copie_garde_celui_du_client()
    {
        var source = Photo("carte", "IMG_1234.jpg");
        var nom = CopieDeTravail.Nom("IMG_1234.jpg", source);

        Assert.StartsWith("IMG_1234-", nom, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", nom, StringComparison.Ordinal);
    }

    [Fact]
    public void Deux_photos_de_meme_nom_ne_se_recouvrent_pas()
    {
        // deux clients, deux cartes, le même IMG_1234.jpg : c'est le cas ordinaire du
        // comptoir, et c'est ce que l'empreinte existe pour séparer
        var une = Photo("carte", "IMG_1234.jpg", "des pixels");
        var autre = Photo("carte2", "IMG_1234.jpg", "d'autres pixels, plus longs");

        Assert.NotEqual(
            CopieDeTravail.Nom("IMG_1234.jpg", une),
            CopieDeTravail.Nom("IMG_1234.jpg", autre));
    }

    /// <summary>
    /// Une copie conserve la date d'écriture de son original : c'est ce sur quoi tient toute
    /// la stabilité de la clé, et cela ne se devine pas en lisant <c>File.Copy</c>.
    /// </summary>
    [Fact]
    public void Une_copie_garde_la_cle_de_son_original()
    {
        var source = Photo("carte", "IMG_1234.jpg");
        var destination = Path.Combine(_racine, "20260819", "IMG_1234-ab12cd34.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);

        var infosSource = new FileInfo(source);
        var infosCopie = new FileInfo(destination);

        Assert.Equal(infosSource.LastWriteTimeUtc, infosCopie.LastWriteTimeUtc);
        Assert.Equal(infosSource.Length, infosCopie.Length);
    }
}
