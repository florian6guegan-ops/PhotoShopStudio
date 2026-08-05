using System.Diagnostics;
using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

// Mesure OU passe le temps du rendu d'un tirage, sur une vraie photo de la boutique.
//
// On n'optimise pas ce qu'on n'a pas mesuré : les journaux disent qu'une planche
// d'identite met plusieurs secondes, ils ne disent pas dans quoi.
//
// Usage : RenduProbe <photo.jpg> [tours]

var source = args.Length > 0
    ? args[0]
    : @"D:\PhotoStudioData\orders\2026\08\20260805-026-f0f55935\photos\001.jpg";

var tours = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 3;

if (!File.Exists(source))
{
    Console.WriteLine($"Photo introuvable : {source}");
    return 1;
}

MagickInit.Configure();

// Les valeurs REELLES de la planche d'identite du 05/08/2026 a 17:53 (commande 05-026) :
// cellule 35 x 45 mm a 300 ppp, recadrage serre, redressement de 1,25 degre.
const int dpi = 300;
var largeurCellule = (int)Math.Round(35 / 25.4 * dpi);
var hauteurCellule = (int)Math.Round(45 / 25.4 * dpi);

var crop = new CropSpec(0.15267415883930796, 0.1332187420858894,
                        0.6912857782285954, 0.5910840589792217);

var demande = new RenderRequest(
    SourcePath: source,
    Crop: crop,
    RotationQuarterTurns: 0,
    FineRotationDegrees: 1.25,
    Fit: FitMode.Fill,
    BorderPx: 0,
    TargetWidthPx: largeurCellule,
    TargetHeightPx: hauteurCellule,
    Adjustments: new ImageAdjustments());

using (var lue = new MagickImage(source))
{
    Console.WriteLine($"Photo    : {Path.GetFileName(source)}");
    Console.WriteLine($"Source   : {lue.Width} x {lue.Height} px " +
                      $"({lue.Width * lue.Height / 1_000_000.0:0.0} Mpx), " +
                      $"{new FileInfo(source).Length / (1024.0 * 1024):0.0} Mo");
}

Console.WriteLine($"Cellule  : {largeurCellule} x {hauteurCellule} px");
Console.WriteLine($"Tours    : {tours}");
Console.WriteLine();

// --- 1. le decodage seul, pleine resolution (ce que fait le rendu aujourd'hui) ---

Console.WriteLine("Decodage seul");
Mesurer("  pleine resolution", () =>
{
    using var image = new MagickImage(source);
    return $"{image.Width}x{image.Height}";
});

// --- 2. le decodage avec indication de taille (ce que fait deja ThumbnailService) ---

// De quoi le rendu a-t-il besoin ? La cellule, divisee par la part que le recadrage
// retient, et deux fois pour que le reechantillonnage final ait de la matiere.
var utileLargeur = (int)(largeurCellule / crop.Width * 2);
var utileHauteur = (int)(hauteurCellule / crop.Height * 2);

Mesurer($"  indication {utileLargeur}x{utileHauteur}", () =>
{
    var settings = new MagickReadSettings();
    settings.SetDefine(MagickFormat.Jpeg, "size", $"{utileLargeur}x{utileHauteur}");
    using var image = new MagickImage(source, settings);
    return $"{image.Width}x{image.Height}";
});

Console.WriteLine();

// --- 3. le rendu complet, tel qu'il tourne en boutique ---

Console.WriteLine("Rendu complet d'une cellule");

var sortie = Path.Combine(Path.GetTempPath(), "rendu-probe.png");
Mesurer("  ImagePipeline.RenderToFile", () =>
{
    ImagePipeline.RenderToFile(demande, sortie, dpi);
    using var rendu = new MagickImage(sortie);
    return $"{rendu.Width}x{rendu.Height}";
});

Console.WriteLine();

// --- 4. etape par etape, en reproduisant ce que fait RenderInto ---
//
// C'est le seul moyen de savoir DANS QUOI passent les secondes : le rendu complet ne
// donne qu'un total.

Console.WriteLine("Detail des etapes (une passe, sur l'image pleine resolution)");

using (var image = new MagickImage(source))
{
    var chrono = Stopwatch.StartNew();

    var profil = image.GetColorProfile();
    if (profil is not null) image.TransformColorSpace(profil, ColorProfiles.SRGB);
    Etape("  profil couleur -> sRGB", chrono, profil is null ? "(aucun profil)" : profil.Name);

    image.AutoOrient();
    Etape("  AutoOrient", chrono, $"{image.Width}x{image.Height}");

    // reduction avant redressement, comme ImagePipeline le fait depuis le 05/08
    var partL = Math.Clamp(crop.Width, 0.01, 1.0);
    var partH = Math.Clamp(crop.Height, 0.01, 1.0);
    var vouluL = largeurCellule / partL * 2.0;
    var vouluH = hauteurCellule / partH * 2.0;
    var facteur = Math.Max(vouluL / image.Width, vouluH / image.Height);

    if (facteur < 1)
    {
        image.Resize(new MagickGeometry(
            (uint)Math.Max(1, Math.Round(image.Width * facteur)),
            (uint)Math.Max(1, Math.Round(image.Height * facteur)))
        { IgnoreAspectRatio = true });
        image.ResetPage();
    }
    Etape("  reduction avant redressement", chrono, $"{image.Width}x{image.Height}");

    image.BackgroundColor = MagickColors.White;
    image.Rotate(1.25);
    image.ResetPage();
    Etape("  redressement 1,25 deg", chrono, $"{image.Width}x{image.Height}");

    var rect = Studio.Imaging.Geometry.CropMath.ToPixelRect(crop, (int)image.Width, (int)image.Height);
    image.Crop(new MagickGeometry(rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height));
    image.ResetPage();
    Etape("  recadrage", chrono, $"{image.Width}x{image.Height}");

    image.Resize(new MagickGeometry((uint)largeurCellule, (uint)hauteurCellule)
    { IgnoreAspectRatio = true });
    Etape("  mise a l'echelle finale", chrono, $"{image.Width}x{image.Height}");

    image.Write(Path.Combine(Path.GetTempPath(), "rendu-probe-etapes.png"));
    Etape("  ecriture PNG", chrono, "");
}

Console.WriteLine();

// --- 5. le redressement, qui pese le plus lourd de ce qui reste ---
//
// Une rotation de 1,25 degre sur 2 Mpx ne devrait pas coûter une seconde. On cherche
// pourquoi, et si une autre voie fait mieux SANS toucher au resultat.

Console.WriteLine("Variantes du redressement (sur l'image reduite, ~2 Mpx)");

MesurerRotation("  Rotate tel quel", img => { img.Rotate(1.25); });

MesurerRotation("  Rotate, alpha desactive", img =>
{
    img.Alpha(AlphaOption.Off);
    img.Rotate(1.25);
});

MesurerRotation("  Rotate, sans virtual pixels", img =>
{
    img.VirtualPixelMethod = VirtualPixelMethod.Background;
    img.Rotate(1.25);
});

MesurerRotation("  Distort ScaleRotateTranslate", img =>
{
    img.Distort(DistortMethod.ScaleRotateTranslate, 1.25);
});

Console.WriteLine();

// --- 5 bis. le cas le plus FREQUENT : un 10x15 sans redressement ---
//
// La planche d'identite est le rendu le plus lourd, mais pas le plus courant. Ce que la
// boutique tire toute la journee, c'est du 10x15 : c'est la que le gain se compte en
// minutes sur une commande de trente photos.

Console.WriteLine("Le cas courant : un 10x15 rempli, sans redressement");

var dix15 = demande with
{
    Crop = CropSpec.Full,
    FineRotationDegrees = 0,
    TargetWidthPx = 1795,
    TargetHeightPx = 1205,
};

Mesurer("  10x15 (rendu complet)", () =>
{
    ImagePipeline.RenderToFile(dix15, Path.Combine(Path.GetTempPath(), "rendu-10x15.png"), dpi);
    return "1795x1205";
});

Console.WriteLine();

// --- 6. LE CONTROLE QUI COMPTE : la qualite n'a pas bouge ---
//
// Le decodage econome change la facon dont TOUS les tirages sont lus. Un gain de temps
// qui abimerait les tirages ne serait pas un gain : on compare donc le rendu econome au
// rendu pleine resolution, pixel a pixel.

Console.WriteLine("Qualite : rendu econome compare au rendu pleine resolution");

var reference = Path.Combine(Path.GetTempPath(), "rendu-reference.png");
var econome = Path.Combine(Path.GetTempPath(), "rendu-econome.png");

// la reference : on relit la source entiere, sans indication de taille
RendreSansIndication(demande, reference, dpi);
ImagePipeline.RenderToFile(demande, econome, dpi);

Comparer("  planche identite (redressee)", demande, reference, econome);

// Le 10x15 est controle A PART : sans redressement la marge est plus courte (1,3 au lieu
// de 2), et c'est le tirage que la boutique sort toute la journee.
Comparer("  10x15 (sans redressement)", dix15,
    Path.Combine(Path.GetTempPath(), "ref-10x15.png"),
    Path.Combine(Path.GetTempPath(), "eco-10x15.png"));

void Comparer(string quoi, RenderRequest quoiRendre, string cheminRef, string cheminEco)
{
    RendreSansIndication(quoiRendre, cheminRef, dpi);
    ImagePipeline.RenderToFile(quoiRendre, cheminEco, dpi);

    using var a = new MagickImage(cheminRef);
    using var b = new MagickImage(cheminEco);

    var ecart = a.Compare(b, ErrorMetric.RootMeanSquared);
    var verdict = ecart < 0.02 ? "OK" : "ATTENTION";

    Console.WriteLine($"{quoi,-34} RMS {ecart,9:0.00000}   {a.Width}x{a.Height}   {verdict}");
}

Console.WriteLine();
Console.WriteLine("La planche en compose HUIT a partir d'une seule cellule rendue :");
Console.WriteLine("le decodage pese donc une fois, pas huit.");

return 0;

// Le rendu tel qu'il etait AVANT le decodage econome : la source lue en entier, puis les
// memes etapes. Sert de reference de qualite.
static void RendreSansIndication(RenderRequest demande, string sortie, int dpi)
{
    using var image = new MagickImage(demande.SourcePath);

    // on repasse par le meme pipeline en lui donnant une source deja decodee : le PNG
    // intermediaire n'a pas d'indication de taille, donc aucune reduction au decodage
    var intermediaire = Path.Combine(Path.GetTempPath(), "rendu-source-pleine.png");
    image.Write(intermediaire);

    ImagePipeline.RenderToFile(demande with { SourcePath = intermediaire }, sortie, dpi);
}

// Prepare une image reduite comme le rendu le fait, puis mesure UNE facon de la redresser.
void MesurerRotation(string quoi, Action<MagickImage> redresser)
{
    var temps = new List<long>(tours);
    var resultat = "";

    for (var i = 0; i < tours + 1; i++)
    {
        using var img = new MagickImage(source);
        img.AutoOrient();
        img.Resize(new MagickGeometry(1194, 1796) { IgnoreAspectRatio = true });
        img.ResetPage();
        img.BackgroundColor = MagickColors.White;

        var chrono = Stopwatch.StartNew();
        redresser(img);
        img.ResetPage();
        chrono.Stop();

        if (i > 0) temps.Add(chrono.ElapsedMilliseconds);   // le premier tour chauffe
        resultat = $"{img.Width}x{img.Height}";
    }

    Console.WriteLine($"{quoi,-34} {temps.Min(),6} ms (median {Median(temps),5} ms)  -> {resultat}");
}

void Etape(string quoi, Stopwatch chrono, string detail)
{
    Console.WriteLine($"{quoi,-34} {chrono.ElapsedMilliseconds,6} ms  {detail}");
    chrono.Restart();
}

void Mesurer(string quoi, Func<string> action)
{
    // un tour a blanc : le premier paie le chargement des bibliotheques natives
    action();

    var temps = new List<long>(tours);
    var resultat = "";

    for (var i = 0; i < tours; i++)
    {
        var chrono = Stopwatch.StartNew();
        resultat = action();
        chrono.Stop();
        temps.Add(chrono.ElapsedMilliseconds);
    }

    Console.WriteLine($"{quoi,-34} {temps.Min(),6} ms (median {Median(temps),5} ms)  -> {resultat}");
}

static long Median(List<long> valeurs)
{
    var triees = valeurs.OrderBy(v => v).ToList();
    return triees[triees.Count / 2];
}
