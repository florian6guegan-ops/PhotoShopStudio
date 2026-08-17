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

        _modelPath = CheminLisibleParOpenCv(modelPath);
    }

    /// <summary>
    /// Un chemin qu'OpenCV saura ouvrir : le sien s'il est en pur ASCII, sinon une copie
    /// du fichier dans un dossier qui l'est.
    ///
    /// <b>OpenCV ne lit pas les chemins accentués.</b> Sa couche fichier prend des
    /// <c>char*</c> et ne fait pas la conversion que Windows attend : le modèle est là,
    /// bien là, et il répond « Can't read ONNX file ». Le message le montre d'ailleurs
    /// lui-même, l'accent déjà abîmé — <c>C:\Users\PhotoConcept CrÃ©teil\…</c>.
    ///
    /// <b>Ce n'est pas un cas rare, c'est le cas français.</b> Le poste de Créteil ouvre
    /// une session « PhotoConcept Créteil » : tout ce qui vit sous son profil porte donc un
    /// accent, et la détection de visage y était morte — donc le pré-cadrage, donc les
    /// photos d'identité, c'est-à-dire le module qu'on y utilise le plus. Ici, à
    /// Maisons-Alfort, l'application tourne depuis <c>D:\PhotoShopStudio</c> et rien ne le
    /// laissait voir. Constaté le 08/08/2026 sur une identité étrangère.
    ///
    /// L'IMAGE, elle, ne passe jamais par un chemin : elle est décodée par Magick et
    /// remise à OpenCV en mémoire (voir <see cref="DetectAll"/>). C'est ce qui a limité le
    /// défaut au seul chargement du modèle.
    /// </summary>
    internal static string CheminLisibleParOpenCv(string chemin)
    {
        if (EstAscii(chemin)) return chemin;

        try
        {
            // ProgramData plutôt que le dossier temporaire : celui de l'utilisateur vit
            // sous son profil, donc sous le même accent. Le nom de ProgramData n'est pas
            // localisé, il est en ASCII sur toutes les installations de Windows.
            var abri = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "StudioPhoto", "modeles");

            var cible = Path.Combine(abri, Path.GetFileName(chemin));
            if (!EstAscii(cible)) return chemin;

            // recopié seulement si absent ou différent : ce chemin est pris à chaque
            // détection, et le modèle ne change qu'avec la version
            if (!File.Exists(cible)
                || new FileInfo(cible).Length != new FileInfo(chemin).Length)
            {
                Directory.CreateDirectory(abri);
                File.Copy(chemin, cible, overwrite: true);
            }

            return cible;
        }
        catch (Exception)
        {
            // droits refusés, disque plein : on rend le chemin d'origine. La détection
            // échouera comme avant, ce qui vaut mieux qu'empêcher d'ouvrir l'écran.
            return chemin;
        }
    }

    private static bool EstAscii(string valeur)
    {
        foreach (var c in valeur)
            if (c > 127) return false;

        return true;
    }

    /// <summary>Visage principal (meilleur score), ou null si aucun visage exploitable.</summary>
    public DetectedFace? DetectMain(string imagePath)
    {
        // par MagickInit et non « new MagickImage(chemin) » : les photos du client sont
        // sur SA carte, et c'est là que la projection en mémoire tue le processus quand
        // elle est retirée (voir MagickInit.Lire)
        //
        // ⚠ ON NE DÉCODE QUE CE QU'ON VA REGARDER. Cette lecture demandait la photo ENTIÈRE
        // — un fichier d'appareil de 50 Mo, décodé en 24 mégapixels — pour la réduire
        // aussitôt à 800 px dans DetectAll. Sur une carte mémoire c'est pire encore :
        // MagickInit.Lire en copie d'abord tous les octets pour survivre à un retrait.
        //
        // C'est ce qui fait « traîner » l'ouverture d'une photo sur l'écran d'identité,
        // signalé le 18/08/2026 : l'écran venait de décoder la même photo une ligne plus
        // haut pour l'afficher, et la détection la relisait entièrement derrière.
        //
        // L'indication de taille laisse le décodeur JPEG travailler à l'échelle 1/2, 1/4 ou
        // 1/8 : il rend directement une image d'environ 800 px, sans jamais construire les
        // 24 mégapixels. Le résultat est le MÊME — DetectAll réduisait déjà à cette taille,
        // et les boîtes rendues sont NORMALISÉES (voir DetectedFace), donc indépendantes de
        // la définition décodée.
        using var magick = MagickInit.Lire(imagePath, DetectionBoxPx);
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
