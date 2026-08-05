using ImageMagick;
using OpenCvSharp;
using Studio.Imaging.Geometry;

namespace Studio.Imaging.Faces;

/// <summary>Visage détecté, coordonnées normalisées sur l'image orientée EXIF.</summary>
/// <param name="Eyes">
/// Les deux yeux, quand le détecteur les a rendus. YuNet donne cinq points par visage —
/// les deux yeux, le nez, les deux coins de la bouche — et ce sont les seuls qui nous
/// servent : c'est là, et nulle part ailleurs, qu'on a le droit de toucher au rouge
/// (voir <c>YeuxRouges</c>).
/// </param>
public sealed record DetectedFace(NormRect Box, double Score, IReadOnlyList<NormPoint> Eyes)
{
    public DetectedFace(NormRect box, double score) : this(box, score, []) { }
}

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
        MagickInit.Configure();
        using var magick = new MagickImage(imagePath);
        return DetectAll(magick).OrderByDescending(f => f.Score).FirstOrDefault();
    }

    /// <summary>
    /// TOUS les visages d'une image déjà décodée, avec la position de leurs yeux.
    ///
    /// Pluriel, et sur une image en mémoire : c'est ce dont la correction des yeux rouges a
    /// besoin. Le cadrage d'identité ne s'intéresse qu'à un visage et part d'un fichier ;
    /// une photo de famille au flash en compte six, et elle est déjà ouverte.
    ///
    /// L'image N'EST PAS modifiée : la copie réduite qu'on donne à YuNet est faite ici.
    /// </summary>
    public IReadOnlyList<DetectedFace> DetectAll(IMagickImage<byte> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        MagickInit.Configure();

        byte[] jpeg;
        using (var magick = (IMagickImage<byte>)source.Clone())
        {
            magick.AutoOrient();
            if (Math.Max(magick.Width, magick.Height) > DetectionBoxPx)
                magick.Thumbnail(DetectionBoxPx, DetectionBoxPx);
            magick.Quality = 90;
            jpeg = magick.ToByteArray(MagickFormat.Jpeg);
        }

        using var image = Cv2.ImDecode(jpeg, ImreadModes.Color);
        if (image.Empty()) return [];

        // la taille d'entrée YuNet est fixée à la création : un détecteur par appel
        // (modèle de 230 Ko, coût négligeable pour un flux « photo d'identité »)
        using var detector = FaceDetectorYN.Create(
            _modelPath, "", new Size(image.Width, image.Height), scoreThreshold: 0.6f);
        using var faces = new Mat();
        detector.Detect(image, faces);

        var trouves = new List<DetectedFace>(faces.Rows);

        // colonnes YuNet : x, y, w, h, puis 5 points (x,y) — œil droit, œil gauche, nez et
        // les deux coins de la bouche —, score en colonne 14
        for (var row = 0; row < faces.Rows; row++)
        {
            var box = new NormRect(
                faces.At<float>(row, 0) / image.Width,
                faces.At<float>(row, 1) / image.Height,
                faces.At<float>(row, 2) / image.Width,
                faces.At<float>(row, 3) / image.Height);

            var yeux = new[]
            {
                new NormPoint(faces.At<float>(row, 4) / image.Width,
                              faces.At<float>(row, 5) / image.Height),
                new NormPoint(faces.At<float>(row, 6) / image.Width,
                              faces.At<float>(row, 7) / image.Height),
            };

            trouves.Add(new DetectedFace(box, faces.At<float>(row, 14), yeux));
        }

        return trouves;
    }
}
