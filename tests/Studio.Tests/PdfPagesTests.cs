using System.Text;
using ImageMagick;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Les PDF acceptés dans les tirages, demandés par l'exploitant le 04/08/2026.
///
/// Une page = une photo : le PDF est éclaté avant d'atteindre la planche, et rien en aval
/// — rendu, minilab, DNP — ne sait qu'un PDF existe.
/// </summary>
public class PdfPagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioPdf-" + Guid.NewGuid().ToString("N"));

    private string Cache => Path.Combine(_root, "cache");

    public PdfPagesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Un PDF minimal mais VALIDE de <paramref name="pages"/> pages A6 paysage.
    ///
    /// Écrit à la main plutôt que posé en fichier d'essai : un binaire dans le dépôt ne se
    /// relit pas, et celui-ci dit exactement ce qu'il contient. La table des références
    /// croisées est calculée pour de bon — PDFium sait réparer un xref faux, et un essai
    /// qui s'appuierait sur cette indulgence ne prouverait rien.
    /// </summary>
    private string Pdf(string nom, int pages)
    {
        var chemin = Path.Combine(_root, nom);

        var objets = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            $"<</Type/Pages/Kids[{string.Join(' ', Enumerable.Range(3, pages).Select(n => $"{n} 0 R"))}]/Count {pages}>>",
        };
        for (var i = 0; i < pages; i++)
            objets.Add("<</Type/Page/Parent 2 0 R/MediaBox[0 0 420 297]>>");

        var corps = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();

        foreach (var (objet, index) in objets.Select((o, i) => (o, i)))
        {
            offsets.Add(corps.Length);
            corps.Append(index + 1).Append(" 0 obj").Append(objet).Append("endobj\n");
        }

        var xref = corps.Length;
        corps.Append("xref\n0 ").Append(objets.Count + 1).Append('\n');
        corps.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            corps.Append(offset.ToString("D10")).Append(" 00000 n \n");

        corps.Append("trailer<</Size ").Append(objets.Count + 1).Append("/Root 1 0 R>>\nstartxref\n")
             .Append(xref).Append("\n%%EOF");

        File.WriteAllText(chemin, corps.ToString(), Encoding.ASCII);
        return chemin;
    }

    [Fact]
    public void Un_pdf_de_trois_pages_donne_trois_images()
    {
        var pages = PdfPages.Extraire(Pdf("trois.pdf", 3), Cache);

        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.True(File.Exists(p), $"page manquante : {p}"));

        // ce sont de vraies images, à la bonne résolution : 420 pt de large à 200 ppp
        // font 420/72 × 200 ≈ 1167 px
        using var premiere = new MagickImage(pages[0]);
        Assert.InRange(premiere.Width, 1150u, 1180u);
    }

    /// <summary>Les pages gardent l'ordre du document : c'est celui du client.</summary>
    [Fact]
    public void Les_pages_gardent_l_ordre_du_document()
    {
        var pages = PdfPages.Extraire(Pdf("ordre.pdf", 4), Cache);

        Assert.Equal(
            ["p001.jpg", "p002.jpg", "p003.jpg", "p004.jpg"],
            pages.Select(Path.GetFileName));
    }

    /// <summary>
    /// Rouvrir le même dossier ne refait aucun rendu — et c'est le geste courant, entre la
    /// planche et « Modifier ».
    /// </summary>
    [Fact]
    public void Une_seconde_extraction_ne_refait_rien()
    {
        var pdf = Pdf("cache.pdf", 2);

        var premieres = PdfPages.Extraire(pdf, Cache);
        var dates = premieres.Select(p => File.GetLastWriteTimeUtc(p)).ToList();

        var secondes = PdfPages.Extraire(pdf, Cache);

        Assert.Equal(premieres, secondes);
        Assert.Equal(dates, secondes.Select(p => File.GetLastWriteTimeUtc(p)));
    }

    /// <summary>
    /// Les pages vont dans le CACHE, jamais à côté du PDF : le dossier ouvert est souvent
    /// la clé USB du client, sur laquelle on n'écrit rien.
    /// </summary>
    [Fact]
    public void Rien_n_est_ecrit_a_cote_du_pdf()
    {
        var pdf = Pdf("intact.pdf", 2);

        PdfPages.Extraire(pdf, Cache);

        Assert.Equal([pdf], Directory.GetFiles(_root));
    }

    /// <summary>
    /// Un PDF abîmé sur la clé d'un client ne doit pas empêcher d'ouvrir les photos qui
    /// l'accompagnent — il est écarté, et le reste passe.
    /// </summary>
    [Fact]
    public void Un_pdf_illisible_est_ecarte_sans_faire_echouer_l_ouverture()
    {
        var casse = Path.Combine(_root, "casse.pdf");
        File.WriteAllText(casse, "ceci n'est pas un PDF");

        var photo = Path.Combine(_root, "photo.jpg");
        File.WriteAllBytes(photo, [0xFF, 0xD8, 0xFF]);

        var resultat = PdfPages.Developper([photo, casse], Cache);

        Assert.Equal([photo], resultat);
    }

    /// <summary>
    /// Les pages prennent la PLACE du PDF dans la liste : un document posé entre deux
    /// photos donne ses pages entre ces deux photos, et non à la fin. C'est ce que
    /// l'opérateur voit dans l'explorateur.
    /// </summary>
    [Fact]
    public void Les_pages_prennent_la_place_du_pdf_dans_la_liste()
    {
        var avant = Path.Combine(_root, "avant.jpg");
        var apres = Path.Combine(_root, "apres.jpg");
        File.WriteAllBytes(avant, [0xFF, 0xD8, 0xFF]);
        File.WriteAllBytes(apres, [0xFF, 0xD8, 0xFF]);

        var pdf = Pdf("milieu.pdf", 2);

        var resultat = PdfPages.Developper([avant, pdf, apres], Cache);

        Assert.Equal(4, resultat.Count);
        Assert.Equal(avant, resultat[0]);
        Assert.Equal(apres, resultat[^1]);
        Assert.All(resultat[1..3], p => Assert.EndsWith(".jpg", p, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(pdf, resultat);
    }

    [Fact]
    public void Une_liste_sans_pdf_traverse_inchangee()
    {
        var photos = new[] { @"D:\a.jpg", @"D:\b.png" };

        Assert.Equal(photos, PdfPages.Developper(photos, Cache));
    }

    [Fact]
    public void Un_pdf_se_reconnait_a_son_extension()
    {
        Assert.True(PdfPages.EstUnPdf(@"D:\clients\billets.PDF"));
        Assert.False(PdfPages.EstUnPdf(@"D:\clients\photo.jpg"));
        Assert.False(PdfPages.EstUnPdf(null));
    }
}
