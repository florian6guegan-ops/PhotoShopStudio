using ImageMagick;
using Studio.Imaging;
using Studio.Imaging.Faces;
using Studio.Imaging.Geometry;

// Essaie le detourage sur une photo reelle, DANS LES MEMES CONDITIONS que l'ecran
// d'identite : image redressee par l'EXIF, tete localisee par la detection de visage.
//
// Usage : MatteProbe <photo> <sortie.png>

var source = args.Length > 0 ? args[0] : "";
var sortie = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "detoure.png");

if (!File.Exists(source)) { Console.WriteLine($"Photo introuvable : {source}"); return 1; }

MagickInit.Configure();
using var image = new MagickImage(source);
image.AutoOrient();
Console.WriteLine($"Source redressee : {image.Width}x{image.Height}");

NormRect? tete = null;
try
{
    var modele = Path.Combine(AppContext.BaseDirectory, "models", "face_detection_yunet_2023mar.onnx");
    if (File.Exists(modele))
    {
        var detecteur = new FaceDetector(modele);
        if (detecteur.DetectMain(source) is { } visage)
        {
            tete = IdPhotoFr.EstimateHead(visage.Box);
            Console.WriteLine($"Tete : x={tete.X:0.00} y={tete.Y:0.00} l={tete.Width:0.00} h={tete.Height:0.00}");
        }
        else Console.WriteLine("Aucun visage detecte : amorce par defaut.");
    }
    else Console.WriteLine($"Modele absent ({modele}) : amorce par defaut.");
}
catch (Exception ex) { Console.WriteLine($"Detection impossible : {ex.Message}"); }

// on veut SAVOIR si le reseau a ete pris ou si on est retombe sur la methode couleur
BackgroundRemoval.Log = m => Console.WriteLine($"   [fond] {m}");
BiRefNetMatting.Log = m => Console.WriteLine($"   [reseau] {m}");
Console.WriteLine($"Modele BiRefNet installe : {(BiRefNetMatting.EstInstalle ? "OUI" : "NON")}");

// Premiere passe : elle paie le chargement du modele (une fois par session).
var depart = DateTime.Now;
var pose = BackgroundRemoval.PoserUnFondBlanc(image);
var premiere = (DateTime.Now - depart).TotalMilliseconds;
var verdict = pose ? "OUI" : "NON (fond juge non uniforme, photo laissee intacte)";
Console.WriteLine($"1re photo  : {premiere:0} ms (chargement du modele compris) — fond blanc pose : {verdict}");

// Seconde passe sur une copie neuve : c'est le temps que vivra l'operateur pour chaque
// photo suivante, le modele restant charge.
using (var seconde = new MagickImage(source))
{
    seconde.AutoOrient();
    var t2 = DateTime.Now;
    BackgroundRemoval.PoserUnFondBlanc(seconde);
    Console.WriteLine($"2e photo   : {(DateTime.Now - t2).TotalMilliseconds:0} ms (regime etabli)");
}

MagickInit.Write(image, sortie);
Console.WriteLine($"Ecrit : {sortie}");
return 0;
