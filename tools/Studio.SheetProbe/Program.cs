using ImageMagick;
using ImageMagick.Drawing;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

// Rend une planche identité telle qu'elle sortira, à partir de la vraie fiche produit du
// catalogue : format, cellules, nombre de poses, profil ICC.
//
// Sert à regarder la planche avant de gâcher du média, puis à l'imprimer avec
// PrintProbe image.
//
// Usage : SheetProbe <code produit> [photo source] [sortie.png]

var code = args.Length > 0 ? args[0] : "ID-FR-6";
var source = args.Length > 1 ? args[1] : "";
var sortie = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "planche-identite.png");

var catalogueDir = Path.Combine(@"D:\PhotoStudioData", "catalog");
var catalogue = ProductCatalog.Load(Path.Combine(catalogueDir, "products.json"));

if (catalogue.Find(code) is not { } produit)
{
    Console.WriteLine($"Produit « {code} » introuvable au catalogue.");
    return 1;
}

if (produit.Sheet is not { } planche)
{
    Console.WriteLine($"Le produit « {code} » n'est pas une planche.");
    return 1;
}

if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
{
    source = Path.Combine(Path.GetTempPath(), "mire-portrait.png");
    CreerMire(source);
    Console.WriteLine($"Aucune photo fournie, mire de controle utilisee : {source}");
}

var icc = produit.IccProfile is null ? null : Path.Combine(catalogueDir, "icc", produit.IccProfile);
if (icc is not null && !File.Exists(icc))
{
    Console.WriteLine($"Profil ICC introuvable : {icc}");
    icc = null;
}

var sheetW = MmPx.ToPixels(produit.WidthMm, produit.Dpi);
var sheetH = MmPx.ToPixels(produit.HeightMm, produit.Dpi);
var cellW = MmPx.ToPixels(planche.CellWidthMm, produit.Dpi);
var cellH = MmPx.ToPixels(planche.CellHeightMm, produit.Dpi);

ImagePipeline.RenderIdSheetToFile(
    new RenderRequest(source, cellW, cellH, CropSpec.Full, 0, 0, FitMode.Fill, 0,
        new ImageAdjustments(), icc),
    planche.Copies, planche.GapMm, planche.CutMarks,
    sheetW, sheetH, sortie, produit.Dpi,
    planche.CutBorder,
    planche.DateStamp ? DateTime.Now : null);

// meme reserve que le rendu, sinon la disposition annoncee ne serait pas celle produite
var disposition = IdSheetLayout.Layout(sheetW, sheetH, cellW, cellH,
    MmPx.ToPixels(planche.GapMm, produit.Dpi), planche.Copies,
    planche.CutMarks ? MmPx.ToPixels(3, produit.Dpi) : 0,
    planche.DateStamp ? MmPx.ToPixels(7, produit.Dpi) : 0);

Console.WriteLine($"Produit  : {produit.Name}");
Console.WriteLine($"Machine  : {produit.PrinterName}"
    + (produit.DevmodeFile is null ? "" : $"  (DEVMODE {produit.DevmodeFile})"));
Console.WriteLine($"Planche  : {produit.WidthMm}×{produit.HeightMm} mm = {sheetW}×{sheetH} px à {produit.Dpi} dpi");
Console.WriteLine($"Cellules : {planche.CellWidthMm}×{planche.CellHeightMm} mm, {planche.Copies} poses"
    + $" en {disposition.Columns}×{disposition.Rows}, écart {planche.GapMm} mm");
Console.WriteLine($"Découpe  : contour {(planche.CutBorder ? "oui" : "non")},"
    + $" repères {(planche.CutMarks ? "oui" : "non")}");
Console.WriteLine($"Horodate : {(planche.DateStamp ? "oui" : "non")},"
    + $" marge basse {sheetH - disposition.Cells.Max(c => c.Bottom)} px");
Console.WriteLine($"Profil   : {icc ?? "aucun"}");
Console.WriteLine($"Ecrit    : {sortie}");

// mesure reelle de la mention sur la planche produite : hauteur et largeur du texte,
// pour verifier qu'elle sort bien au corps voulu
using var relue = new MagickImage(sortie);
using var pixels = relue.GetPixels();
var basPhotos = disposition.Cells.Max(c => c.Bottom);

int hautTexte = int.MaxValue, basTexte = -1, gaucheTexte = int.MaxValue, droiteTexte = -1;
for (var y = basPhotos + 2; y < sheetH; y++)
    for (var x = 0; x < sheetW; x++)
        if (pixels.GetPixel(x, y).GetChannel(0) < 140)
        {
            if (y < hautTexte) hautTexte = y;
            if (y > basTexte) basTexte = y;
            if (x < gaucheTexte) gaucheTexte = x;
            if (x > droiteTexte) droiteTexte = x;
        }

if (basTexte < 0)
{
    Console.WriteLine("Mention  : absente de la planche");
}
else
{
    var hautMm = (basTexte - hautTexte + 1) * 25.4 / produit.Dpi;
    var largeMm = (droiteTexte - gaucheTexte + 1) * 25.4 / produit.Dpi;
    Console.WriteLine($"Mention  : {hautMm:0.0} × {largeMm:0.0} mm,"
        + $" de y={hautTexte} a {basTexte} (planche {sheetH} px)");
}

return 0;

// Mire de contrôle en forme de portrait : un dégradé pour juger les tons, des repères aux
// bords pour vérifier que rien n'est rogné, et un ovale à la place du visage.
static void CreerMire(string chemin)
{
    using var mire = new MagickImage(MagickColors.White, 700, 900);

    var dessin = new Drawables()
        .FillColor(new MagickColor("#B9C6D6"))
        .Rectangle(0, 0, 699, 899)
        .FillColor(new MagickColor("#E8C9A8"))
        .Ellipse(350, 430, 190, 250, 0, 360)
        .FillColor(new MagickColor("#333333"))
        .Ellipse(285, 390, 22, 14, 0, 360)
        .Ellipse(415, 390, 22, 14, 0, 360)
        .FillColor(MagickColors.Transparent)
        .StrokeColor(MagickColors.Black)
        .StrokeWidth(4)
        .Rectangle(2, 2, 697, 897);

    mire.Draw(dessin);
    mire.Format = MagickFormat.Png;
    mire.Write(chemin);
}
