using ImageMagick;

namespace Studio.Imaging;

/// <summary>
/// Dans quel espace de couleur une photo a été enregistrée, et comment la ramener en sRGB.
///
/// <b>Le profil embarqué ne suffit pas, et c'est tout le sujet.</b> Un reflex réglé en Adobe
/// RGB écrit un JPEG SANS profil ICC : il le déclare dans l'EXIF, et nulle part ailleurs —
/// <c>ColorSpace = 65535</c> (« Uncalibrated »), plus un <c>InteropIndex = R03</c> dans
/// l'IFD d'interopérabilité. Ne regarder que le profil embarqué revient donc à lire ces
/// photos-là comme du sRGB : les couleurs sortent fausses, et d'autant plus qu'elles sont
/// saturées.
///
/// <b>Le cas relevé.</b> Commande 20-013 du 20/08/2026, planche d'identité tirée depuis
/// Studio Photo Identité — fichier <c>_DSC0905.JPG</c>, un Nikon D3200 réglé en Adobe RGB
/// (l'underscore en tête du nom est la marque que Nikon en donne). La peau du front,
/// 216,170,147 dans le fichier, vaut 232,172,147 une fois ramenée en sRGB : seize niveaux
/// de rouge perdus, sur une planche où le sujet est un visage.
///
/// <b>Un seul endroit</b>, parce qu'il y a trois lecteurs — le rendu du tirage, l'aperçu de
/// l'écran de cadrage et les vignettes — et que deux règles qui divergent donneraient un
/// écran qui ne ressemble pas au papier. C'est la panne que ce dépôt a déjà payée plusieurs
/// fois sur des méthodes jumelles.
/// </summary>
public static class EspaceCouleurSource
{
    /// <summary>
    /// Ramène la photo dans l'espace de travail sRGB, quel que soit ce qui le déclare.
    ///
    /// Ne fait rien quand la photo est déjà en sRGB — le cas de la quasi-totalité des
    /// fichiers du comptoir : téléphones, compacts, cartes de clients.
    /// </summary>
    public static void RamenerEnSrgb(IMagickImage<byte> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Le profil EMBARQUÉ fait foi quand il existe : c'est la déclaration la plus forte,
        // et elle peut porter autre chose qu'Adobe RGB (Display P3, ProPhoto).
        if (image.GetColorProfile() is { } embarque)
        {
            image.TransformColorSpace(embarque, ColorProfiles.SRGB);
            return;
        }

        if (DeclareParLExif(image) is { } declare)
            image.TransformColorSpace(declare, ColorProfiles.SRGB);
    }

    /// <summary>
    /// L'espace que l'EXIF déclare, quand ce n'est pas du sRGB. Null quand la photo est en
    /// sRGB, quand elle n'a pas d'EXIF, ou quand l'EXIF est illisible — <b>et null vaut
    /// alors « sRGB présumé »</b>, la convention des JPEG grand public, qui était le
    /// comportement du logiciel avant cette règle.
    ///
    /// Deux valeurs mènent à Adobe RGB :
    ///
    /// - <b>65535, « Uncalibrated »</b> : la valeur du standard EXIF pour « autre chose que
    ///   du sRGB ». Sans profil embarqué pour dire quoi, Adobe RGB est ce que suppose toute
    ///   la chaîne photo — c'est ce que proposent Photoshop et Lightroom devant ce fichier,
    ///   et ce que confirme l'<c>InteropIndex R03</c> des appareils qui l'écrivent.
    /// - <b>2</b> : hors standard, mais des appareils l'écrivent pour dire Adobe RGB.
    /// </summary>
    public static ColorProfile? DeclareParLExif(IMagickImage<byte> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        try
        {
            var espace = image.GetExifProfile()?.GetValue(ExifTag.ColorSpace)?.Value;

            return espace switch
            {
                65535 => ColorProfiles.AdobeRGB1998,
                2 => ColorProfiles.AdobeRGB1998,
                _ => null,
            };
        }
        catch (Exception)
        {
            // Un EXIF abîmé ne doit pas empêcher de tirer la photo : on retombe sur le
            // sRGB présumé, exactement comme un fichier qui n'en aurait pas.
            return null;
        }
    }
}
