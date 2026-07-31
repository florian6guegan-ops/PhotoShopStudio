using System.Drawing;
using System.Drawing.Printing;

namespace Studio.Printing.LargeFormat;

/// <summary>
/// Impression d'un agrandissement posé à un endroit précis de la feuille, selon les
/// réglages de <see cref="LargeFormatPrintSettings"/>.
///
/// À la différence de <see cref="BitmapPrinter"/>, qui pose l'image 1:1 sur une page aux
/// dimensions exactes du produit, on travaille ici sur le média que l'opérateur a choisi
/// dans le pilote (A3+, rouleau…) et l'image y est mise à l'échelle et positionnée —
/// exactement ce que fait la boîte d'impression de Photoshop.
/// </summary>
public static class LargeFormatPrinter
{
    private const double MmPerInch = 25.4;

    /// <summary>Journal optionnel : trace le média retenu et l'emplacement obtenu.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Dimensions de la feuille telle que le pilote la retient, en millimètres. C'est sur
    /// cette taille que la boîte de dialogue calcule l'aperçu et le centrage.
    /// </summary>
    public static (double WidthMm, double HeightMm) GetPageSizeMm(string printerName, byte[]? devModeBytes,
        bool landscape = false)
    {
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Imprimante invalide ou hors ligne : « {printerName} »");

        if (devModeBytes is not null)
            DevMode.Apply(doc.PrinterSettings, devModeBytes);

        doc.DefaultPageSettings.Landscape = landscape;
        var bounds = doc.DefaultPageSettings.Bounds; // centièmes de pouce
        return (bounds.Width / 100.0 * MmPerInch, bounds.Height / 100.0 * MmPerInch);
    }

    /// <summary>Formats déclarés par le pilote, pour information dans l'interface.</summary>
    public static IReadOnlyList<string> ListPaperSizes(string printerName)
    {
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid) return [];

        var names = new List<string>();
        foreach (PaperSize paper in doc.PrinterSettings.PaperSizes)
            names.Add($"{paper.PaperName} — {paper.Width / 100.0 * MmPerInch:0} × {paper.Height / 100.0 * MmPerInch:0} mm");
        return names;
    }

    /// <summary>
    /// Imprime <paramref name="bitmap"/> selon <paramref name="settings"/>.
    /// </summary>
    /// <param name="bitmap">Image source, à sa résolution d'origine.</param>
    /// <param name="sourceDpi">Résolution d'origine de l'image (celle du fichier).</param>
    /// <param name="documentName">Nom affiché dans la file d'impression.</param>
    public static void Print(Bitmap bitmap, LargeFormatPrintSettings settings, double sourceDpi,
        string documentName = "Studio Photo — agrandissement")
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(settings);

        var problems = settings.Validate();
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Les réglages d'impression sont incomplets :\n• " + string.Join("\n• ", problems));

        using var doc = new PrintDocument();
        doc.DocumentName = documentName;
        doc.PrinterSettings.PrinterName = settings.PrinterName;
        if (!doc.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Imprimante invalide ou hors ligne : « {settings.PrinterName} »");

        if (settings.DevModeBytes is not null)
            DevMode.Apply(doc.PrinterSettings, settings.DevModeBytes);

        doc.PrinterSettings.Copies = (short)Math.Max(1, settings.Copies);
        doc.DefaultPageSettings.Landscape = settings.Landscape;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.OriginAtMargins = false;
        doc.PrintController = new StandardPrintController();

        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            // (0,0) au coin physique de la feuille, comme dans BitmapPrinter
            g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);

            var page = e.PageSettings.Bounds;
            var pageWidthMm = page.Width / 100.0 * MmPerInch;
            var pageHeightMm = page.Height / 100.0 * MmPerInch;

            var placement = settings.ComputePlacement(bitmap.Width, bitmap.Height, sourceDpi,
                pageWidthMm, pageHeightMm);

            var rect = new RectangleF(
                (float)(placement.LeftMm / MmPerInch * 100),
                (float)(placement.TopMm / MmPerInch * 100),
                (float)(placement.WidthMm / MmPerInch * 100),
                (float)(placement.HeightMm / MmPerInch * 100));

            Log?.Invoke(
                $"Agrandissement « {documentName} » sur {settings.PrinterName} : " +
                $"feuille {pageWidthMm:0}×{pageHeightMm:0} mm, " +
                $"tirage {placement.WidthMm:0.0}×{placement.HeightMm:0.0} mm à {placement.ScalePercent:0.#} % " +
                $"({placement.EffectiveDpi:0} ppp), position {placement.LeftMm:0.0};{placement.TopMm:0.0} mm" +
                (placement.OverflowsPaper(pageWidthMm, pageHeightMm) ? "  ⚠ le tirage déborde de la feuille" : ""));

            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(bitmap, rect);

            e.HasMorePages = false;
        };

        doc.Print();
    }
}
