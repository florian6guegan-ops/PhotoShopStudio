using System.Drawing;
using System.Text.Json;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;
using Studio.Store;

namespace Studio.Printing;

/// <summary>État d'impression d'une enveloppe, persisté dans spool/envNN.state.</summary>
public sealed record SpoolState(string Status, DateTimeOffset At)
{
    public const string Rendering = "Rendering";
    public const string Spooled = "Spooled";
    public const string Printed = "Printed";
}

/// <summary>
/// Orchestration rendu → impression, avec la garantie anti-« replay storm » :
/// l'état Spooled est persisté sur disque AVANT l'envoi au spouleur, et une
/// enveloppe retrouvée dans cet état après un crash n'est JAMAIS resoumise
/// automatiquement — c'est l'opérateur qui tranche (confirmée / à réimprimer).
/// Un crash coûte au pire une confirmation manuelle, jamais un tirage en double.
/// </summary>
public sealed class PrintOrchestrator
{
    private readonly ProductCatalog _catalog;
    private readonly OrderFolderStore _store;
    private readonly string _catalogDir;

    /// <param name="catalogDir">Dossier catalog/ contenant les DEVMODE et profils ICC.</param>
    public PrintOrchestrator(ProductCatalog catalog, OrderFolderStore store, string catalogDir)
    {
        _catalog = catalog;
        _store = store;
        _catalogDir = catalogDir;
    }

    /// <summary>
    /// Imprime une enveloppe complète. <paramref name="operatorConfirmed"/> doit être
    /// vrai pour resoumettre une enveloppe déjà passée à l'état Spooled.
    /// </summary>
    public void PrintEnvelope(Order order, Envelope envelope, bool operatorConfirmed = false, string? pdfOverridePath = null)
    {
        var state = ReadSpoolState(order, envelope);
        if (state?.Status is SpoolState.Spooled or SpoolState.Printed && !operatorConfirmed)
            throw new InvalidOperationException(
                $"L'enveloppe {order.DisplayNumber}/{envelope.Number} a déjà été envoyée à l'impression " +
                "— confirmation opérateur requise pour réimprimer.");

        // une file sur le port `nul` avale les travaux sans rien imprimer : mieux vaut
        // refuser franchement que rendre, spouler et annoncer un succès imaginaire
        foreach (var printerName in envelope.Lines
                     .Select(l => _catalog.Require(l.ProductCode).PrinterName)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PrinterPorts.IsNullPort(printerName))
                throw new InvalidOperationException(
                    $"L'imprimante « {printerName} » est branchée sur le port « nul » : Windows accepte " +
                    "les travaux et les jette, aucun tirage ne sortira.\n\n" +
                    "C'est le cas des files DE100 installées par DiLand (le minilab est piloté par le SDK " +
                    "Fuji, pas par le spouleur). Il faut d'abord donner un vrai port à cette imprimante, " +
                    "ou choisir un produit imprimé sur la DS620.");
        }

        WriteSpoolState(order, envelope, SpoolState.Rendering);
        envelope.Status = EnvelopeStatus.Rendering;
        _store.Save(order);
        _store.AppendEvent(order, "render-start", $"env={envelope.Number}");

        var pages = RenderEnvelope(order, envelope);

        // moment décisif : on grave « Spooled » sur disque AVANT de soumettre au spouleur
        WriteSpoolState(order, envelope, SpoolState.Spooled);
        envelope.Status = EnvelopeStatus.Spooled;
        _store.Save(order);
        var destinations = string.Join(", ", pages
            .GroupBy(p => p.Product.PrinterName)
            .Select(g => $"{g.Key} × {g.Sum(p => p.Copies)}"));
        _store.AppendEvent(order, "spool-start",
            $"env={envelope.Number}, pages={pages.Sum(p => p.Copies)}, destinations=[{destinations}]");

        PrintPages(pages, pdfOverridePath, $"Studio {order.DisplayNumber}-{envelope.Number}");

        WriteSpoolState(order, envelope, SpoolState.Printed);
        envelope.Status = EnvelopeStatus.Printed;
        _store.Save(order);
        _store.AppendEvent(order, "printed", $"env={envelope.Number}");
    }

    /// <summary>
    /// À appeler au démarrage : enveloppes retrouvées à l'état Spooled sans confirmation
    /// d'impression — un crash a eu lieu entre la soumission et la fin. L'opérateur
    /// doit dire si le tirage est sorti ou s'il faut réimprimer.
    /// </summary>
    public List<(Order Order, Envelope Envelope)> FindEnvelopesNeedingConfirmation(IEnumerable<Order> orders)
    {
        var result = new List<(Order, Envelope)>();
        foreach (var order in orders)
            foreach (var envelope in order.Envelopes)
                if (ReadSpoolState(order, envelope)?.Status == SpoolState.Spooled)
                    result.Add((order, envelope));
        return result;
    }

    /// <summary>L'opérateur confirme que le tirage est bien sorti (rien à réimprimer).</summary>
    public void ConfirmPrinted(Order order, Envelope envelope)
    {
        WriteSpoolState(order, envelope, SpoolState.Printed);
        envelope.Status = EnvelopeStatus.Printed;
        _store.Save(order);
        _store.AppendEvent(order, "confirmed-by-operator", $"env={envelope.Number}");
    }

    /// <summary>
    /// Une page rendue et le produit qui l'a produite. Le produit est porté par la page
    /// (et non par l'enveloppe) : une enveloppe groupe un CANAL d'impression, qui peut
    /// contenir plusieurs produits — imprimante, DEVMODE et format diffèrent alors d'une
    /// page à l'autre.
    /// </summary>
    private sealed record RenderedPage(
        string Path, int Copies, double WidthMm, double HeightMm, Product Product, string? Finish);

    private List<RenderedPage> RenderEnvelope(Order order, Envelope envelope)
    {
        var photosDir = _store.GetPhotosFolder(order);
        var rendersDir = _store.GetRendersFolder(order);
        Directory.CreateDirectory(rendersDir);

        var pages = new List<RenderedPage>();
        foreach (var line in envelope.Lines)
        {
            var product = _catalog.Require(line.ProductCode);
            var targetW = MmPx.ToPixels(product.WidthMm, product.Dpi);
            var targetH = MmPx.ToPixels(product.HeightMm, product.Dpi);
            var borderPx = MmPx.ToPixels(product.BorderMm, product.Dpi);

            for (var i = 0; i < line.Items.Count; i++)
            {
                var item = line.Items[i];
                var output = Path.Combine(rendersDir, $"env{envelope.Number:00}-{line.ProductCode}-{i + 1:000}.png");

                // canevas orienté comme la photo (une paysage part en 15×10, pas rognée en 10×15) ;
                // les planches (identité) gardent leur orientation fixe
                var sourcePath = Path.Combine(photosDir, item.FileName);
                var (itemW, itemH) = (targetW, targetH);
                var (widthMm, heightMm) = (product.WidthMm, product.HeightMm);
                if (product.Sheet is null)
                {
                    var (imgW, imgH) = ImagePipeline.GetOrientedSize(sourcePath, item.RotationQuarterTurns);
                    (itemW, itemH) = CropMath.OrientCanvas(targetW, targetH, imgW, imgH, item.Crop);
                    if (itemW != targetW)
                        (widthMm, heightMm) = (product.HeightMm, product.WidthMm);
                }

                // le profil de la finition (média) l'emporte sur celui du produit
                var iccFile = product.Finishes
                                  .FirstOrDefault(f => string.Equals(f.Name, item.Finish, StringComparison.OrdinalIgnoreCase))
                                  ?.IccProfile
                              ?? product.IccProfile;
                var iccPath = iccFile is not null
                    ? Path.Combine(_catalogDir, "icc", iccFile)
                    : null;

                if (!File.Exists(output)) // rendu déterministe : réutilisable après un crash
                {
                    if (product.Sheet is { } sheet)
                    {
                        // planche identité : la cellule est rendue en Fill au format cellule
                        var cellW = MmPx.ToPixels(sheet.CellWidthMm, product.Dpi);
                        var cellH = MmPx.ToPixels(sheet.CellHeightMm, product.Dpi);
                        ImagePipeline.RenderIdSheetToFile(new RenderRequest(
                                sourcePath, cellW, cellH,
                                item.Crop, item.RotationQuarterTurns, FitMode.Fill, 0,
                                item.Adjustments, iccPath),
                            item.SheetCopiesOverride ?? sheet.Copies, sheet.GapMm, sheet.CutMarks,
                            targetW, targetH, output, product.Dpi);
                    }
                    else
                    {
                        ImagePipeline.RenderToFile(new RenderRequest(
                            sourcePath,
                            itemW, itemH,
                            item.Crop,
                            item.RotationQuarterTurns,
                            item.FitOverride ?? product.DefaultFit,
                            borderPx,
                            item.Adjustments,
                            iccPath),
                            output, product.Dpi);
                    }
                }

                pages.Add(new RenderedPage(output, item.Quantity, widthMm, heightMm, product, item.Finish));
            }
        }
        return pages;
    }

    private void PrintPages(List<RenderedPage> pages, string? pdfPath, string documentName)
    {
        var devModes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);

        // aplatit (page, copies) en séquence de pages physiques
        foreach (var page in pages)
        {
            var product = page.Product;
            var key = $"{product.Code}|{page.Finish}";
            if (!devModes.TryGetValue(key, out var devMode))
            {
                // la finition choisie l'emporte sur le DEVMODE par défaut du produit
                var file = product.Finishes
                               .FirstOrDefault(f => string.Equals(f.Name, page.Finish, StringComparison.OrdinalIgnoreCase))
                               ?.DevmodeFile
                           ?? product.DevmodeFile;
                devMode = file is not null
                    ? File.ReadAllBytes(Path.Combine(_catalogDir, file))
                    : null;
                devModes[key] = devMode;
            }

            using var bitmap = new Bitmap(page.Path);
            for (var copy = 0; copy < page.Copies; copy++)
            {
                BitmapPrinter.Print(
                    product.PrinterName, bitmap, page.WidthMm, page.HeightMm,
                    devMode, pdfPath, documentName);
            }
        }
    }

    private string SpoolStatePath(Order order, Envelope envelope) =>
        Path.Combine(_store.GetSpoolFolder(order), $"env{envelope.Number:00}.state");

    private SpoolState? ReadSpoolState(Order order, Envelope envelope)
    {
        var json = AtomicFile.ReadAllTextOrNull(SpoolStatePath(order, envelope));
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<SpoolState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteSpoolState(Order order, Envelope envelope, string status)
    {
        Directory.CreateDirectory(_store.GetSpoolFolder(order));
        AtomicFile.WriteAllText(SpoolStatePath(order, envelope), JsonSerializer.Serialize(new SpoolState(status, DateTimeOffset.Now)));
    }
}
