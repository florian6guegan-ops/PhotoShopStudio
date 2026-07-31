using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

// Rend une planche identité dans un fichier, pour la regarder avant de gâcher du média.
//
// Usage : SheetProbe <photo source> [sortie.png] [copies]
//
// Sans photo source, une mire grise est utilisée : suffisant pour contrôler la
// disposition, les contours de découpe et l'horodatage.

const int dpi = 300;

var source = args.Length > 0 ? args[0] : "";
var sortie = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "planche-identite.png");
var copies = args.Length > 2 && int.TryParse(args[2], out var n) ? n : 8;

Console.WriteLine("Polices vues par ImageMagick : "
    + string.Join(", ", MagickNET.FontFamilies.Take(12))
    + (MagickNET.FontFamilies.Count() > 12 ? $" … ({MagickNET.FontFamilies.Count()} au total)" : ""));

if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
{
    source = Path.Combine(Path.GetTempPath(), "mire-portrait.png");
    using var mire = new MagickImage(MagickColor.FromRgb(150, 150, 150), 700, 900);
    mire.Write(source);
    Console.WriteLine($"Aucune photo fournie, mire utilisée : {source}");
}

var sheetW = MmPx.ToPixels(152, dpi);
var sheetH = MmPx.ToPixels(102, dpi);
var cellW = MmPx.ToPixels(35, dpi);
var cellH = MmPx.ToPixels(45, dpi);

ImagePipeline.RenderIdSheetToFile(
    new RenderRequest(source, cellW, cellH, CropSpec.Full, 0, FitMode.Fill, 0, new ImageAdjustments()),
    copies, SheetSpec.DefaultGapMm, cutMarks: true,
    sheetW, sheetH, sortie, dpi,
    cutBorder: true, stamp: DateTime.Now);

var disposition = IdSheetLayout.Layout(sheetW, sheetH, cellW, cellH,
    MmPx.ToPixels(SheetSpec.DefaultGapMm, dpi), copies, MmPx.ToPixels(3, dpi));

Console.WriteLine($"Planche  : {sheetW}×{sheetH} px ({152}×{102} mm à {dpi} dpi)");
Console.WriteLine($"Grille   : {disposition.Columns} colonnes × {disposition.Rows} lignes, {copies} photos");
Console.WriteLine($"Marge basse : {sheetH - disposition.Cells.Max(c => c.Bottom)} px");
Console.WriteLine($"Ecrit    : {sortie}");

// compte l'encre deposee sous les photos, comme le fait le test de l'horodatage
using var relue = new MagickImage(sortie);
using var pixels = relue.GetPixels();
var basPhotos = disposition.Cells.Max(c => c.Bottom);
var sombres = 0;
for (var y = basPhotos + 2; y < sheetH; y++)
    for (var x = 0; x < sheetW; x += 2)
        if (pixels.GetPixel(x, y).GetChannel(0) < 100) sombres++;

Console.WriteLine($"Encre sous les photos : {sombres} pixels sombres");

// de quoi comprendre ce qu'on mesure vraiment : le plus sombre et son emplacement
var plusSombre = 255;
int xs = -1, ys = -1, sous200 = 0;
for (var y = basPhotos + 2; y < sheetH; y++)
    for (var x = 0; x < sheetW; x++)
    {
        var v = pixels.GetPixel(x, y).GetChannel(0);
        if (v < 200) sous200++;
        if (v < plusSombre) { plusSombre = v; xs = x; ys = y; }
    }

Console.WriteLine($"Pixel le plus sombre : {plusSombre} en ({xs},{ys}) ; sous 200 : {sous200}");
Console.WriteLine($"Bande analysee : y de {basPhotos + 2} a {sheetH}, canaux={relue.ChannelCount}, espace={relue.ColorSpace}");

foreach (var seuil in new[] { 80, 100, 120, 140, 150, 160, 200 })
{
    var compte = 0;
    for (var y = basPhotos + 2; y < sheetH; y++)
        for (var x = 0; x < sheetW; x++)
            if (pixels.GetPixel(x, y).GetChannel(0) < seuil) compte++;

    Console.WriteLine($"  sous {seuil,3} : {compte}");
}
