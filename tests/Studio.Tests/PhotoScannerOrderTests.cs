using Studio.Sources;

namespace Studio.Tests;

/// <summary>
/// L'ordre dans lequel les écrans présentent les photos d'un support.
///
/// Demandé par l'exploitant le 04/08/2026 : « les photos sont triées de la plus ancienne à
/// la plus récente, il faudrait que ce soit l'inverse par défaut ». Au comptoir, ce que le
/// client veut tirer est ce qu'il vient de prendre — l'ordre alphabétique le renvoyait en
/// bas d'une liste de mille vignettes.
/// </summary>
public class PhotoScannerOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioTri-" + Guid.NewGuid().ToString("N"));

    public PhotoScannerOrderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Un fichier daté à la fois en création et en modification — voir DateDeLaPhoto.</summary>
    private string Photo(string nom, DateTime date)
    {
        var chemin = Path.Combine(_root, nom);
        File.WriteAllBytes(chemin, [0xFF, 0xD8, 0xFF]);
        File.SetCreationTime(chemin, date);
        File.SetLastWriteTime(chemin, date);
        return chemin;
    }

    [Fact]
    public void La_plus_recente_vient_en_premier()
    {
        var vieille = Photo("aaa.jpg", new DateTime(2026, 1, 5, 9, 0, 0));
        var recente = Photo("zzz.jpg", new DateTime(2026, 8, 4, 9, 0, 0));
        var moyenne = Photo("mmm.jpg", new DateTime(2026, 5, 2, 9, 0, 0));

        var triees = PhotoScanner.TrierParDateDecroissante([vieille, recente, moyenne]);

        Assert.Equal([recente, moyenne, vieille], triees);
    }

    /// <summary>
    /// Deux photos prises dans la même seconde — une rafale — ne doivent pas changer
    /// d'ordre d'une ouverture à l'autre : le nom départage, et il est stable.
    /// </summary>
    [Fact]
    public void A_date_egale_le_nom_departage()
    {
        var date = new DateTime(2026, 8, 4, 11, 3, 12);
        var b = Photo("IMG_002.jpg", date);
        var a = Photo("IMG_001.jpg", date);

        Assert.Equal([a, b], PhotoScanner.TrierParDateDecroissante([b, a]));
        Assert.Equal([a, b], PhotoScanner.TrierParDateDecroissante([a, b]));
    }

    /// <summary>
    /// La date retenue est la plus ANCIENNE des deux que Windows tient : copier une carte
    /// mémoire remet la création à l'instant de la copie, et toutes les photos du client se
    /// retrouveraient à la même seconde. La modification, elle, survit à la copie.
    /// </summary>
    [Fact]
    public void Une_copie_recente_ne_masque_pas_la_date_de_prise_de_vue()
    {
        var prise = new DateTime(2026, 3, 1, 14, 0, 0);
        var copie = new DateTime(2026, 8, 4, 10, 0, 0);

        var copiee = Path.Combine(_root, "IMG_100.jpg");
        File.WriteAllBytes(copiee, [0xFF, 0xD8, 0xFF]);
        File.SetLastWriteTime(copiee, prise);
        File.SetCreationTime(copiee, copie);   // l'explorateur Windows fait exactement ceci

        var plusRecente = Photo("IMG_200.jpg", new DateTime(2026, 6, 1, 8, 0, 0));

        Assert.Equal([plusRecente, copiee], PhotoScanner.TrierParDateDecroissante([copiee, plusRecente]));
    }

    /// <summary>
    /// Un fichier disparu entre le recensement et le tri — la clé retirée en pleine
    /// commande — part en fin de liste. C'est un ordre d'AFFICHAGE : il n'a pas le droit
    /// d'échouer.
    /// </summary>
    [Fact]
    public void Un_fichier_illisible_ne_fait_pas_echouer_le_tri()
    {
        var bonne = Photo("bonne.jpg", new DateTime(2026, 7, 1, 12, 0, 0));
        var fantome = Path.Combine(_root, "disparue.jpg");

        var triees = PhotoScanner.TrierParDateDecroissante([fantome, bonne]);

        Assert.Equal([bonne, fantome], triees);
    }

    [Fact]
    public void Une_liste_vide_reste_vide()
    {
        Assert.Empty(PhotoScanner.TrierParDateDecroissante([]));
    }

    /// <summary>
    /// Le PDF compte comme « photo » au recensement — sans quoi un dossier qui n'en
    /// contient que serait annoncé vide — mais il se reconnaît, pour que les écrans qui ne
    /// savent pas l'éclater en pages l'écartent.
    /// </summary>
    [Fact]
    public void Un_pdf_est_recense_et_reconnaissable()
    {
        Assert.True(PhotoScanner.IsPhoto(@"D:\clients\billets.pdf"));
        Assert.True(PhotoScanner.IsPdf(@"D:\clients\billets.PDF"));
        Assert.False(PhotoScanner.IsPdf(@"D:\clients\photo.jpg"));
    }
}
