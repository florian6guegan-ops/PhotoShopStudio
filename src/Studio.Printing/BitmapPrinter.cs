using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;

namespace Studio.Printing;

/// <summary>
/// Impression d'un bitmap déjà rendu à la taille finale : le pilote reçoit
/// l'image posée 1:1 sur une page aux dimensions exactes du produit.
/// Toute la mise à l'échelle a eu lieu en amont dans le pipeline de rendu.
/// </summary>
public static class BitmapPrinter
{
    /// <summary>Journal optionnel (branché sur FileLog par l'app) : trace la page réellement obtenue.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Ouvre un rendu et l'aplatit en 24 bits SUR DU BLANC, prêt à partir au pilote.
    ///
    /// <b>Le canal alpha n'a aucun sens sur du papier</b>, et il ne doit pas arriver jusqu'au
    /// pilote. Nos rendus sortent en PNG 32 bits (ImageMagick garde le canal alpha même quand
    /// il est plein), et <c>new Bitmap(chemin)</c> les charge donc en <c>Format32bppArgb</c>.
    /// Les pilotes photo, eux, sont réglés en 24 bits — la DS620 annonce
    /// <c>ColorMode=24bpp</c> dans son DEVMODE. GDI+ doit alors convertir à la volée, à
    /// chaque tirage, dans le chemin d'impression : c'est du travail en plus sur le fil qui
    /// alimente la machine, et le résultat dépend du pilote plutôt que de nous.
    ///
    /// On convertit donc UNE fois, ici, et ce qui part est exactement ce qu'on a voulu.
    /// </summary>
    public static Bitmap ChargerPourImpression(string path)
    {
        using var lu = new Bitmap(path);

        // déjà sans transparence : rien à refaire, la copie coûterait pour rien
        if (lu.PixelFormat == PixelFormat.Format24bppRgb) return new Bitmap(lu);

        var plat = new Bitmap(lu.Width, lu.Height, PixelFormat.Format24bppRgb);
        try
        {
            plat.SetResolution(lu.HorizontalResolution, lu.VerticalResolution);

            using var g = Graphics.FromImage(plat);
            // le fond du papier : ce que l'alpha laissait voir, c'est du blanc
            g.Clear(Color.White);
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.NearestNeighbor; // 1:1, aucune remise à l'échelle
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(lu, new Rectangle(0, 0, lu.Width, lu.Height));

            return plat;
        }
        catch
        {
            plat.Dispose();
            throw;
        }
    }

    /// <param name="printerName">Nom exact de la file Windows.</param>
    /// <param name="bitmap">Image finale (déjà à la bonne résolution).</param>
    /// <param name="widthMm">Largeur physique de la page.</param>
    /// <param name="heightMm">Hauteur physique de la page.</param>
    /// <param name="devModeBytes">Réglages pilote capturés (papier, média, sans marges…).</param>
    /// <param name="printToFilePath">Chemin de sortie pour les imprimantes fichier (Print to PDF) — évite le dialogue.</param>
    /// <param name="documentName">Nom affiché dans la file d'impression.</param>
    public static void Print(
        string printerName,
        Bitmap bitmap,
        double widthMm,
        double heightMm,
        byte[]? devModeBytes = null,
        string? printToFilePath = null,
        string documentName = "Studio Photo")
    {
        using var doc = new PrintDocument();
        doc.DocumentName = documentName;
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Imprimante invalide ou hors ligne : « {printerName} »");

        if (devModeBytes is not null)
            DevMode.Apply(doc.PrinterSettings, devModeBytes);

        if (printToFilePath is not null)
        {
            doc.PrinterSettings.PrintToFile = true;
            doc.PrinterSettings.PrintFileName = printToFilePath;
        }

        // dimensions de page en centièmes de pouce (unité de System.Drawing.Printing)
        var w100 = (int)Math.Round(widthMm / 25.4 * 100);
        var h100 = (int)Math.Round(heightMm / 25.4 * 100);
        // certains pilotes (dont Microsoft Print to PDF) ignorent les formats personnalisés
        // et retombent en A4 : on privilégie donc un format déclaré par le pilote quand
        // il correspond aux dimensions demandées, dans les deux orientations
        var formatPilote = FindDriverPaperSize(doc.PrinterSettings, w100, h100);

        // Une imprimante qui ne déclare QUE des formats privés n'accepte rien d'autre.
        //
        // La DS620 en publie onze (RawKind 119 à 129), et pas un seul format standard : son
        // firmware sélectionne le média par cet identifiant. Un PaperSize fabriqué ici porte
        // RawKind 0, soit DMPAPER_USER — la machine reçoit une forme qu'elle ne connaît pas
        // et JETTE le travail, sans erreur ni page. C'est ce qui est arrivé aux planches
        // d'identité les 01 et 02/08/2026 : le journal montre « page obtenue 152×102 mm
        // (Format produit) », et rien n'est sorti.
        //
        // On préfère donc refuser en nommant les formats disponibles. Les pilotes qui
        // savent composer un format libre (Print to PDF, XPS…) déclarent, eux, des formats
        // standard : le repli leur reste ouvert.
        if (formatPilote is null && DeclareUniquementDesFormatsPrives(doc.PrinterSettings))
            throw FormatIntrouvable(printerName, widthMm, heightMm, doc.PrinterSettings);

        doc.DefaultPageSettings.PaperSize = formatPilote ?? new PaperSize("Format produit", w100, h100);
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.OriginAtMargins = false;
        doc.PrintController = new StandardPrintController(); // pas de fenêtre de progression

        if (doc.DefaultPageSettings.PaperSize.Width > doc.DefaultPageSettings.PaperSize.Height != w100 > h100
            && w100 != h100)
        {
            // le format du pilote est déclaré dans l'autre orientation : on bascule en paysage
            doc.DefaultPageSettings.Landscape = true;
        }

        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            // compense le décalage matériel pour que (0,0) soit le coin physique de la page
            g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);

            // On dessine sur la page que le pilote a RÉELLEMENT retenue, pas sur celle qu'on a
            // demandée : beaucoup de pilotes photo (dont la DS620) ignorent un format personnalisé
            // et retombent sur le média chargé. Dessiner à la taille nominale débordait alors de
            // la page et rognait l'image en silence (planche de 6 sortie amputée de sa 3e rangée).
            var page = e.PageSettings.Bounds;

            var wanted = $"{widthMm:0}×{heightMm:0} mm";
            var got = $"{page.Width / 100.0 * 25.4:0}×{page.Height / 100.0 * 25.4:0} mm";
            var paperName = doc.DefaultPageSettings.PaperSize.PaperName;
            Log?.Invoke($"Impression « {documentName} » sur {printerName} : demandé {wanted}, " +
                        $"page obtenue {got} ({paperName}, paysage={e.PageSettings.Landscape})" +
                        (wanted == got ? "" : "  ⚠ le pilote a substitué son média — capturez les réglages (DEVMODE)"));

            if (bitmap.Width > bitmap.Height != page.Width > page.Height && page.Width != page.Height)
            {
                // page retenue dans l'autre orientation : on pivote l'image plutôt que de la laisser rogner
                g.TranslateTransform(page.Width, 0);
                g.RotateTransform(90);
                g.DrawImage(bitmap, new RectangleF(0, 0, page.Height, page.Width));
            }
            else
            {
                g.DrawImage(bitmap, new RectangleF(0, 0, page.Width, page.Height));
            }

            e.HasMorePages = false;
        };

        doc.Print();
    }

    /// <summary>
    /// Vérifie que le pilote saura sortir ce format — <b>avant</b> le moindre envoi.
    ///
    /// À appeler au début de l'enveloppe : la même vérification a lieu dans
    /// <see cref="Print"/>, mais elle y survient une fois l'enveloppe déjà marquée « remise
    /// au spouleur », ce qui la ferait proposer à la réimpression au démarrage suivant alors
    /// que rien n'est parti.
    /// </summary>
    public static void EnsurePageSizeAvailable(string printerName, double widthMm, double heightMm)
    {
        var settings = new PrinterSettings { PrinterName = printerName };
        if (!settings.IsValid)
            throw new InvalidOperationException($"Imprimante invalide ou hors ligne : « {printerName} »");

        var w100 = (int)Math.Round(widthMm / 25.4 * 100);
        var h100 = (int)Math.Round(heightMm / 25.4 * 100);

        if (FindDriverPaperSize(settings, w100, h100) is not null) return;
        if (!DeclareUniquementDesFormatsPrives(settings)) return;

        throw FormatIntrouvable(printerName, widthMm, heightMm, settings);
    }

    private static InvalidOperationException FormatIntrouvable(
        string printerName, double widthMm, double heightMm, PrinterSettings settings) =>
        new($"L'imprimante « {printerName} » n'accepte que ses propres formats de papier, et aucun " +
            $"ne correspond à {widthMm:0.#} × {heightMm:0.#} mm. Rien n'a été imprimé.\n\n" +
            "Formats acceptés : " + DecrireLesFormats(settings) + ".\n\n" +
            "Corrigez les dimensions du produit dans Catalogue pour qu'elles tombent sur l'un d'eux.");

    /// <summary>
    /// Le format déclaré par le pilote qui convient le mieux au tirage demandé, dans l'une
    /// ou l'autre orientation, ou null s'il n'y en a aucun.
    ///
    /// Trois règles, dans cet ordre :
    ///
    /// 1. <b>jamais plus petit</b> que le tirage (au-delà de <see cref="Rognage"/>) — une
    ///    page trop courte rogne l'image en silence ;
    /// 2. <b>pas trop grand</b> non plus (<see cref="Etirement"/>) : l'image est ensuite
    ///    dessinée aux dimensions de la page retenue, donc un format bien plus large
    ///    l'étirerait. Sur une planche d'identité, un demi-millimètre de trop suffit à
    ///    faire refuser la photo au guichet ;
    /// 3. entre les candidats, <b>le plus proche</b>.
    ///
    /// L'ancienne version n'acceptait qu'un écart de 1,5 mm et prenait le premier venu :
    /// le (6x4) de la DS620, à 156,2 × 104,9 mm, était écarté pour une planche déclarée à
    /// 152 × 102, et le travail partait sur un format que la machine ne connaît pas.
    /// </summary>
    private static PaperSize? FindDriverPaperSize(PrinterSettings settings, int width100, int height100)
    {
        var formats = settings.PaperSizes.Cast<PaperSize>().ToList();
        var retenu = ChoisirFormat(formats.Select(p => (p.Width, p.Height)).ToList(), width100, height100);
        return retenu < 0 ? null : formats[retenu];
    }

    /// <summary>
    /// La règle de choix, isolée du pilote pour être vérifiable : aucun poste de
    /// développement n'a de DS620 branchée.
    /// </summary>
    /// <param name="formats">Formats déclarés par le pilote, en centièmes de pouce.</param>
    /// <returns>Indice du format retenu, ou −1 si aucun ne convient.</returns>
    internal static int ChoisirFormat(IReadOnlyList<(int Width, int Height)> formats,
        int width100, int height100)
    {
        var retenu = -1;
        var meilleureNote = int.MaxValue;

        for (var i = 0; i < formats.Count; i++)
        {
            var (w, h) = formats[i];

            // le format déclaré peut l'être dans l'autre sens (« PR (4x6) » contre « (6x4) ») :
            // c'est l'appelant qui bascule ensuite la page en paysage
            foreach (var (largeur, hauteur, retourne) in new[] { (w, h, false), (h, w, true) })
            {
                var dLargeur = largeur - width100;
                var dHauteur = hauteur - height100;

                if (dLargeur < -Rognage || dHauteur < -Rognage) continue;
                if (dLargeur > Etirement || dHauteur > Etirement) continue;

                // à écart égal, le format déclaré DANS LE BON SENS l'emporte : il évite la
                // bascule en paysage, donc un aller-retour de plus par le pilote
                var note = 2 * (Math.Abs(dLargeur) + Math.Abs(dHauteur)) + (retourne ? 1 : 0);
                if (note >= meilleureNote) continue;

                meilleureNote = note;
                retenu = i;
            }
        }
        return retenu;
    }

    /// <summary>Ce qu'on accepte de perdre sur un bord, en centièmes de pouce (~1,5 mm).</summary>
    private const int Rognage = 6;

    /// <summary>Ce qu'on accepte d'étirer sur un bord, en centièmes de pouce (~2,5 mm).</summary>
    private const int Etirement = 10;

    /// <summary>
    /// Vrai si le pilote ne publie aucun format standard : ses formats sont des formes
    /// privées, identifiées par un numéro que lui seul comprend (les onze de la DS620).
    /// Lui demander autre chose ne donne pas un tirage approximatif, mais aucun tirage.
    /// </summary>
    private static bool DeclareUniquementDesFormatsPrives(PrinterSettings settings)
    {
        var formats = settings.PaperSizes.Cast<PaperSize>().ToList();
        return formats.Count > 0 && formats.All(p => p.Kind == PaperKind.Custom);
    }

    private static string DecrireLesFormats(PrinterSettings settings) =>
        string.Join(", ", settings.PaperSizes
            .Cast<PaperSize>()
            .Select(p => $"{p.PaperName} ({p.Width / 100.0 * 25.4:0.#} × {p.Height / 100.0 * 25.4:0.#} mm)"));
}
