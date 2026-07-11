using ImageMagick;
using Studio.Imaging.Faces;

namespace Studio.Tests;

public class FaceDetectorTests
{
    /// <summary>Remonte du dossier de sortie des tests jusqu'à models/ à la racine du dépôt.</summary>
    private static string ModelPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "face_detection_yunet_2023mar.onnx");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("models/face_detection_yunet_2023mar.onnx introuvable au-dessus du dossier de tests");
    }

    [Fact]
    public void BlankImage_NoFaceDetected()
    {
        // vérifie surtout que le natif OpenCV se charge et que le modèle se lit
        var detector = new FaceDetector(ModelPath());

        var path = Path.Combine(Path.GetTempPath(), $"studio-blank-{Guid.NewGuid():N}.jpg");
        using (var image = new MagickImage(MagickColors.Gray, 640, 480))
            image.Write(path, MagickFormat.Jpeg);

        try
        {
            Assert.Null(detector.DetectMain(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
