using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Faces;
using Studio.Imaging.Geometry;

// Reproduit EXACTEMENT ce que fait l'ecran d'identite, et DIT ce qu'il calcule :
// detection, estimation de la tete, cadre demande, cadre reellement obtenu.
//
// Usage : IdProbe <photo> <sortie.png>

var source = args.Length > 0 ? args[0] : "";
var sortie = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "cellule-id.png");
if (!File.Exists(source)) { Console.WriteLine($"Photo introuvable : {source}"); return 1; }

MagickInit.Configure();
var doc = IdDocumentSpec.France;
const int dpi = 300;

var modele = Path.Combine(AppContext.BaseDirectory, "models", "face_detection_yunet_2023mar.onnx");
var detecteur = new FaceDetector(modele);
var visage = detecteur.DetectMain(source);
if (visage is null) { Console.WriteLine("Aucun visage detecte."); return 2; }

var tete = IdPhotoFr.EstimateHead(visage.Box);

using var brute = new MagickImage(source);
brute.AutoOrient();
int iw = (int)brute.Width, ih = (int)brute.Height;

// le cadre AVANT bornage, pour voir si le bornage mord
var cropH = tete.Height * (doc.HeightMm / doc.TargetHeadMm);
var cropW = cropH * (doc.WidthMm / doc.HeightMm) * ih / (double)iw;
var top = tete.Y - cropH * (doc.TargetCrownMarginMm / doc.HeightMm);
var demande = new CropSpec(tete.CenterX - cropW / 2, top, cropW, cropH);
var obtenu = IdPhotoFr.ComputeCrop(tete, iw, ih, doc);

Console.WriteLine($"Image         : {iw}x{ih}");
Console.WriteLine($"Tete detectee : y={tete.Y:0.0000} h={tete.Height:0.0000}");
Console.WriteLine($"Cible         : visage {doc.TargetHeadMm:0.##} mm, marge {doc.TargetCrownMarginMm:0.##} mm");
Console.WriteLine($"Cadre demande : y={demande.Y:0.0000} h={demande.Height:0.0000} w={demande.Width:0.0000}");
Console.WriteLine($"Cadre obtenu  : y={obtenu.Y:0.0000} h={obtenu.Height:0.0000} w={obtenu.Width:0.0000}");
Console.WriteLine(Math.Abs(demande.Height - obtenu.Height) > 1e-6 || Math.Abs(demande.Y - obtenu.Y) > 1e-6
    ? "  >>> LE BORNAGE A MORDU : le cadre demande sortait de l'image"
    : "  (aucun bornage)");
Console.WriteLine($"Ratio tete/cadre attendu : {tete.Height / obtenu.Height:0.000}");

var cw = MmPx.ToPixels(doc.WidthMm, dpi);
var ch = MmPx.ToPixels(doc.HeightMm, dpi);
ImagePipeline.RenderToFile(
    new RenderRequest(source, cw, ch, obtenu, 0, 0, FitMode.Fill, 0, new ImageAdjustments()),
    sortie, dpi);
Console.WriteLine($"Cellule ecrite: {sortie} ({cw}x{ch} px)");
return 0;
