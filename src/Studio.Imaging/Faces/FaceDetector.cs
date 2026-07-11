using ImageMagick;
using OpenCvSharp;
using Studio.Imaging.Geometry;

namespace Studio.Imaging.Faces;

/// <summary>Visage détecté, coordonnées normalisées sur l'image orientée EXIF.</summary>
public sealed record DetectedFace(NormRect Box, double Score);

/// <summary>
/// Détection de visage YuNet (ONNX local, hors-ligne) pour le pré-cadrage identité.
/// Le décodage passe par Magick (HEIC compris, orientation EXIF appliquée) pour que
/// les coordonnées soient dans le même repère que CropSpec.
/// </summary>
public sealed class FaceDetector
{
    private const int DetectionBoxPx = 800; // détection sur image réduite : rapide et suffisant

    private readonly string _modelPath;

    public FaceDetector(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Modèle YuNet introuvable : {modelPath}", modelPath);
        _modelPath = modelPath;
    }

    /// <summary>Visage principal (meilleur score), ou null si aucun visage exploitable.</summary>
    public DetectedFace? DetectMain(string imagePath)
    {
        byte[] jpeg;
        MagickInit.Configure();
        using (var magick = new MagickImage(imagePath))
        {
            magick.AutoOrient();
            if (Math.Max(magick.Width, magick.Height) > DetectionBoxPx)
                magick.Thumbnail(DetectionBoxPx, DetectionBoxPx);
            magick.Quality = 90;
            jpeg = magick.ToByteArray(MagickFormat.Jpeg);
        }

        using var image = Cv2.ImDecode(jpeg, ImreadModes.Color);
        if (image.Empty()) return null;

        // la taille d'entrée YuNet est fixée à la création : un détecteur par appel
        // (modèle de 230 Ko, coût négligeable pour un flux « photo d'identité »)
        using var detector = FaceDetectorYN.Create(
            _modelPath, "", new Size(image.Width, image.Height), scoreThreshold: 0.6f);
        using var faces = new Mat();
        detector.Detect(image, faces);
        if (faces.Rows == 0) return null;

        // colonnes YuNet : x, y, w, h, 5 points (x,y), score en colonne 14
        var bestRow = 0;
        var bestScore = float.MinValue;
        for (var row = 0; row < faces.Rows; row++)
        {
            var score = faces.At<float>(row, 14);
            if (score > bestScore)
            {
                bestScore = score;
                bestRow = row;
            }
        }

        var box = new NormRect(
            faces.At<float>(bestRow, 0) / image.Width,
            faces.At<float>(bestRow, 1) / image.Height,
            faces.At<float>(bestRow, 2) / image.Width,
            faces.At<float>(bestRow, 3) / image.Height);
        return new DetectedFace(box, bestScore);
    }
}
