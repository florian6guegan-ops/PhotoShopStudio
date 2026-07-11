using System.Drawing;
using System.Drawing.Printing;

namespace Studio.Printing;

/// <summary>
/// Impression d'un bitmap déjà rendu à la taille finale : le pilote reçoit
/// l'image posée 1:1 sur une page aux dimensions exactes du produit.
/// Toute la mise à l'échelle a eu lieu en amont dans le pipeline de rendu.
/// </summary>
public static class BitmapPrinter
{
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
        // il correspond aux dimensions demandées (à ~1,5 mm près), dans les deux orientations
        doc.DefaultPageSettings.PaperSize =
            FindDriverPaperSize(doc.PrinterSettings, w100, h100)
            ?? new PaperSize("Format produit", w100, h100);
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
            g.DrawImage(bitmap, new RectangleF(0, 0, w100, h100));
            e.HasMorePages = false;
        };

        doc.Print();
    }

    /// <summary>
    /// Cherche parmi les formats déclarés par le pilote un format aux dimensions
    /// demandées (tolérance ~1,5 mm), dans les deux orientations.
    /// </summary>
    private static PaperSize? FindDriverPaperSize(PrinterSettings settings, int width100, int height100)
    {
        const int tolerance = 6; // centièmes de pouce ≈ 1,5 mm

        PaperSize? swappedMatch = null;
        foreach (PaperSize paper in settings.PaperSizes)
        {
            if (Math.Abs(paper.Width - width100) <= tolerance && Math.Abs(paper.Height - height100) <= tolerance)
                return paper;
            if (Math.Abs(paper.Width - height100) <= tolerance && Math.Abs(paper.Height - width100) <= tolerance)
                swappedMatch ??= paper;
        }
        return swappedMatch;
    }
}
