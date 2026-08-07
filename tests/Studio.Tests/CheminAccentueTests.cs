using ImageMagick;
using Studio.Imaging.Faces;

namespace Studio.Tests;

/// <summary>
/// Les chemins accentués, et la détection de visage qu'ils faisaient échouer.
///
/// <b>Le défaut du 08/08/2026.</b> Le poste de Créteil ouvre une session
/// « PhotoConcept Créteil » : tout ce qui vit sous son profil porte un accent. OpenCV prend
/// des <c>char*</c> pour ses chemins et ne fait pas la conversion que Windows attend — il
/// répondait donc « Can't read ONNX file » sur un modèle pourtant présent, en montrant
/// l'accent déjà abîmé dans son propre message : <c>C:\Users\PhotoConcept CrÃ©teil\…</c>.
///
/// Sans détection de visage, pas de pré-cadrage, donc pas de photo d'identité : le module
/// le plus utilisé de ce poste était mort depuis son installation. Rien ne le laissait voir
/// depuis Maisons-Alfort, où l'application tourne dans <c>D:\PhotoShopStudio</c>.
/// </summary>
public class CheminAccentueTests : IDisposable
{
    /// <summary>Un dossier qui porte le même accent que le compte de Créteil.</summary>
    private readonly string _dossier = Path.Combine(
        Path.GetTempPath(), "Studio-Créteil-" + Guid.NewGuid().ToString("N"));

    public CheminAccentueTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    private static string ModeleDuDepot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidat = Path.Combine(dir.FullName, "models", "face_detection_yunet_2023mar.onnx");
            if (File.Exists(candidat)) return candidat;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("models/face_detection_yunet_2023mar.onnx introuvable");
    }

    /// <summary>
    /// <b>L'essai qui aurait attrapé le défaut.</b> Le modèle est posé sous un dossier
    /// accentué, exactement comme sur le poste de Créteil, et la détection doit aboutir.
    /// </summary>
    [Fact]
    public void Le_modele_se_charge_depuis_un_dossier_accentue()
    {
        var modele = Path.Combine(_dossier, "face_detection_yunet_2023mar.onnx");
        File.Copy(ModeleDuDepot(), modele);

        var detecteur = new FaceDetector(modele);

        var photo = Path.Combine(_dossier, "unie.jpg");
        using (var image = new MagickImage(MagickColors.Gray, 640, 480))
            image.Write(photo, MagickFormat.Jpeg);

        // une image unie ne porte aucun visage : ce qui compte est qu'on arrive JUSQUE-LÀ
        // sans « Can't read ONNX file »
        Assert.Null(detecteur.DetectMain(photo));
    }

    /// <summary>
    /// L'image aussi vit sous le profil de l'opérateur — les photos d'un client arrivent
    /// dans ses Téléchargements. Elle ne passe jamais par OpenCV directement (Magick la
    /// décode et la remet en mémoire), et cet essai le verrouille.
    /// </summary>
    [Fact]
    public void Une_photo_dans_un_dossier_accentue_se_lit()
    {
        var detecteur = new FaceDetector(ModeleDuDepot());

        var photo = Path.Combine(_dossier, "photo-é-client.jpg");
        using (var image = new MagickImage(MagickColors.White, 320, 240))
            image.Write(photo, MagickFormat.Jpeg);

        Assert.Null(detecteur.DetectMain(photo));
    }

    /// <summary>Un chemin déjà en ASCII n'est pas recopié : on rend le sien, tel quel.</summary>
    [Fact]
    public void Un_chemin_sans_accent_est_rendu_tel_quel()
    {
        var modele = ModeleDuDepot();

        Assert.Equal(modele, FaceDetector.CheminLisibleParOpenCv(modele));
    }

    /// <summary>
    /// Un chemin accentué est remplacé par un abri en ASCII — et le fichier y est
    /// réellement, avec la même taille.
    /// </summary>
    [Fact]
    public void Un_chemin_accentue_est_remplace_par_un_abri_ascii()
    {
        var modele = Path.Combine(_dossier, "face_detection_yunet_2023mar.onnx");
        File.Copy(ModeleDuDepot(), modele);

        var abri = FaceDetector.CheminLisibleParOpenCv(modele);

        Assert.NotEqual(modele, abri);
        Assert.All(abri, c => Assert.True(c <= 127, $"l'abri porte encore « {c} » : {abri}"));
        Assert.True(File.Exists(abri));
        Assert.Equal(new FileInfo(modele).Length, new FileInfo(abri).Length);
    }
}
