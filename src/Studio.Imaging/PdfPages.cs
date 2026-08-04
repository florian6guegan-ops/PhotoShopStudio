using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PDFtoImage;

namespace Studio.Imaging;

/// <summary>
/// Éclate un PDF en une image par page, pour que le reste du logiciel n'ait jamais à
/// savoir qu'un PDF existe.
///
/// <b>Une page = une photo.</b> Un PDF de trois pages entre dans la planche comme trois
/// vignettes, recadrables, corrigeables et facturables une par une. C'est la seule
/// définition qui tienne au comptoir : le client tend une clé avec le PDF de ses billets
/// ou un scan de trois feuilles, et ce qu'il achète, ce sont des tirages.
///
/// <b>Pourquoi PDFium et non ImageMagick.</b> Magick.NET ne lit pas un PDF tout seul : il
/// délègue à Ghostscript, qui n'est installé sur aucun poste de la boutique. <c>PDFtoImage</c>
/// embarque PDFium en natif — rien à installer, rien à tenir à jour, et pas de licence
/// AGPL à traîner dans un dépôt public.
///
/// Les pages rendues vivent dans le CACHE, jamais à côté du PDF : le dossier ouvert est
/// souvent une clé USB ou une carte mémoire du client, sur laquelle on n'écrit rien.
///
/// <c>Studio.Imaging</c> cible <c>net8.0</c> sans système, mais PDFium ne se déclare
/// utilisable que sur une liste de plateformes : d'où l'annotation Windows, qui est de
/// toute façon la vérité de ce logiciel. Sans elle, l'analyseur CA1416 refuse la
/// compilation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PdfPages
{
    /// <summary>Journal optionnel.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Résolution de rendu, en points par pouce.
    ///
    /// 200 ppp sur une page A4 donne 1654 × 2339 px, soit de quoi tirer un 13×18 à
    /// 300 ppp sans interpolation. Monter à 300 quadruplerait presque le coût mémoire
    /// (3508 × 4961) pour un gain que le papier ne rend pas : ce qui arrive en PDF est du
    /// document scanné ou composé, pas du 24 mégapixels.
    /// </summary>
    public const int Ppp = 200;

    /// <summary>
    /// Nombre de pages au-delà duquel on s'arrête.
    ///
    /// Ce n'est pas une limite de confort. Une notice de 400 pages posée par erreur dans
    /// le dossier remplirait la planche de vignettes illisibles et le cache de quatre
    /// cents fichiers, avant même que l'opérateur ait vu ce qui se passe. Au-delà, on
    /// s'arrête et on le DIT.
    /// </summary>
    public const int MaxPages = 60;

    /// <summary>Vrai si le chemin désigne un PDF.</summary>
    public static bool EstUnPdf(string? chemin) =>
        !string.IsNullOrEmpty(chemin) &&
        Path.GetExtension(chemin).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Les pages d'un PDF, rendues en JPEG dans le cache, dans l'ordre du document.
    ///
    /// <b>Idempotent</b> : l'empreinte du fichier (chemin, taille, date) donne le dossier
    /// de cache, et un dossier déjà complet est repris tel quel. Rouvrir le même dossier
    /// deux fois de suite ne refait donc aucun rendu — et c'est le geste courant, entre
    /// « Modifier » et la planche.
    ///
    /// Les pages sont rendues UNE PAR UNE : PDFium tient la page décodée en mémoire, et
    /// une extraction en bloc ferait cohabiter soixante bitmaps.
    /// </summary>
    /// <param name="pdf">Le fichier PDF.</param>
    /// <param name="dossierCache">Racine du cache (voir <c>DataRoot\cache</c>).</param>
    /// <returns>Les chemins des pages rendues, dans l'ordre.</returns>
    public static IReadOnlyList<string> Extraire(string pdf, string dossierCache)
    {
        ArgumentException.ThrowIfNullOrEmpty(pdf);
        ArgumentException.ThrowIfNullOrEmpty(dossierCache);

        var dossier = Path.Combine(dossierCache, "pdf", Empreinte(pdf));
        var temoin = Path.Combine(dossier, "pages.txt");

        // déjà extrait : le témoin n'est écrit qu'une fois TOUTES les pages posées, donc
        // une extraction interrompue (coupure, clé retirée) se refait au lieu de rendre
        // une commande incomplète
        if (File.Exists(temoin))
        {
            var connues = File.ReadAllLines(temoin)
                .Select(nom => Path.Combine(dossier, nom))
                .Where(File.Exists)
                .ToList();

            if (connues.Count > 0) return connues;
        }

        Directory.CreateDirectory(dossier);

        int pages;
        using (var flux = File.OpenRead(pdf))
            pages = Conversion.GetPageCount(flux, leaveOpen: false, password: null);

        if (pages <= 0)
            throw new InvalidOperationException($"« {Path.GetFileName(pdf)} » ne contient aucune page.");

        var retenues = Math.Min(pages, MaxPages);
        if (retenues < pages)
            Log?.Invoke($"PDF « {Path.GetFileName(pdf)} » : {pages} pages, seules les " +
                        $"{MaxPages} premières sont reprises.");

        var options = new RenderOptions(Dpi: Ppp, WithAnnotations: true, WithFormFill: true);
        var noms = new List<string>(retenues);

        for (var page = 0; page < retenues; page++)
        {
            var nom = $"p{page + 1:000}.jpg";

            using (var flux = File.OpenRead(pdf))
                Conversion.SaveJpeg(Path.Combine(dossier, nom), flux, page,
                    leaveOpen: false, password: null, options: options);

            noms.Add(nom);
        }

        File.WriteAllLines(temoin, noms);
        Log?.Invoke($"PDF « {Path.GetFileName(pdf)} » : {noms.Count} page(s) rendue(s) à {Ppp} ppp.");

        return noms.Select(nom => Path.Combine(dossier, nom)).ToList();
    }

    /// <summary>
    /// Remplace les PDF d'une liste de fichiers par leurs pages, à leur place.
    ///
    /// L'ordre est conservé : un PDF de trois pages posé entre deux photos donne trois
    /// vignettes entre ces deux photos, et non trois vignettes à la fin. C'est ce que
    /// l'opérateur voit dans l'explorateur, et ce qu'il s'attend à retrouver.
    ///
    /// <b>Un PDF illisible est ÉCARTÉ, jamais fatal</b> : un fichier abîmé sur la clé d'un
    /// client ne doit pas empêcher d'ouvrir les trente photos qui l'accompagnent.
    /// </summary>
    public static List<string> Developper(IEnumerable<string> fichiers, string dossierCache)
    {
        ArgumentNullException.ThrowIfNull(fichiers);

        var resultat = new List<string>();
        foreach (var fichier in fichiers)
        {
            if (!EstUnPdf(fichier))
            {
                resultat.Add(fichier);
                continue;
            }

            try
            {
                resultat.AddRange(Extraire(fichier, dossierCache));
            }
            catch (Exception ex)
            {
                Log?.Invoke($"PDF « {Path.GetFileName(fichier)} » illisible, écarté : {ex.Message}");
            }
        }

        return resultat;
    }

    /// <summary>
    /// Empreinte d'un fichier : chemin, taille et date de modification.
    ///
    /// Pas le CONTENU — lire cinquante mégaoctets pour décider s'il faut relire cinquante
    /// mégaoctets n'aurait pas de sens. Un PDF réécrit change de taille ou de date dans
    /// tous les cas qui nous concernent (export, scan, copie).
    /// </summary>
    private static string Empreinte(string chemin)
    {
        var infos = new FileInfo(chemin);
        var graine = string.Create(CultureInfo.InvariantCulture,
            $"{infos.FullName.ToLowerInvariant()}|{infos.Length}|{infos.LastWriteTimeUtc.Ticks}");

        var octets = SHA256.HashData(Encoding.UTF8.GetBytes(graine));
        return Convert.ToHexString(octets, 0, 8).ToLowerInvariant();
    }
}
