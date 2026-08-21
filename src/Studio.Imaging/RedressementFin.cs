using ImageMagick;
using OpenCvSharp;

namespace Studio.Imaging;

/// <summary>
/// Le redressement fin — la fraction de degré que l'opérateur pose pour remettre un horizon
/// d'aplomb — calculé par OpenCV et non par ImageMagick.
///
/// <b>C'est le poste le plus cher de tout le logiciel, et de très loin.</b> Mesuré le
/// 20/08/2026 sur une photo de 4000 × 6016 (le reflex de la boutique, redressé de 0,75°) :
///
/// | rotation | durée |
/// |---|---|
/// | `MagickImage.Rotate` | 11,9 s |
/// | `Cv2.WarpAffine`, aller-retour des octets compris | 0,37 s |
///
/// Trente-deux fois. Ce n'est pas une affaire de réglage : Magick.NET est livré SANS OpenMP
/// — <c>ResourceLimits.Thread</c> vaut 1 et le forcer à 8 ne change rien (11,6 s contre
/// 11,9 s, mesuré) — et sa rotation passe par trois cisaillements successifs, donc trois
/// traversées complètes de l'image sur un seul cœur. OpenCV fait une passe, sur huit cœurs,
/// en SIMD.
///
/// <b>Et l'image y GAGNE en piqué.</b> Les trois cisaillements interpolent trois fois de
/// suite, en linéaire : variance du laplacien mesurée à 6,4 au cœur de l'image, contre 7,7
/// pour la source non tournée — le redressement coûtait 17 % de détail. La même rotation en
/// bicubique, en une passe, rend 7,5. On va donc plus vite ET on abîme moins.
///
/// <b>Bicubique et non Lanczos.</b> Lanczos rend 8,0, c'est-à-dire PLUS que la source : il
/// sur-accentue, et sur un visage cela se voit en liseré clair au bord des cheveux. La
/// bicubique reste sur le détail qui existe, et coûte deux fois moins (0,33 s contre 0,71 s).
///
/// C'est le même choix qu'a déjà fait <see cref="PixelCorrections"/> pour les corrections de
/// tons, et pour la même raison : on ne laisse à Magick.NET que ce qu'il est seul à savoir
/// faire.
/// </summary>
internal static class RedressementFin
{
    /// <summary>
    /// Sous ce seuil, il n'y a rien à redresser. C'est celui qu'emploie
    /// <c>ImagePipeline.AppliquerLaGeometrie</c> pour décider d'appeler, repris ici pour que
    /// la méthode soit sûre appelée seule.
    /// </summary>
    private const double AngleNegligeable = 0.01;

    /// <summary>Journal facultatif : seuls les REPLIS s'y écrivent.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Tourne <paramref name="image"/> de <paramref name="degres"/> sur place, en agrandissant
    /// le canevas pour que rien ne soit coupé et en comblant les coins libérés avec
    /// <paramref name="remplissage"/> — exactement le contrat de <c>MagickImage.Rotate</c>,
    /// auquel on retombe si quoi que ce soit ici ne se présente pas comme prévu.
    ///
    /// <b>Le canevas fait un ou deux pixels de moins que celui d'ImageMagick</b>, qui majore
    /// la boîte englobante à cause de ses cisaillements successifs. C'est sans conséquence :
    /// le recadrage qui suit est exprimé en PROPORTIONS de l'image, et le masque du sujet
    /// traverse la même méthode que la photo — c'est toute la règle d'AppliquerLaGeometrie,
    /// et elle est préservée puisqu'il n'y a toujours qu'un seul chemin.
    /// </summary>
    public static void Appliquer(MagickImage image, double degres, MagickColor remplissage)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(remplissage);

        if (Math.Abs(degres) <= AngleNegligeable) return;

        try
        {
            if (ParOpenCv(image, degres, remplissage)) return;
        }
        catch (Exception ex) when (ex is OpenCVException or MagickException or OutOfMemoryException)
        {
            // Un redressement qui échoue ne doit pas emporter le tirage : on repasse par
            // ImageMagick, qui est lent mais qui a toujours marché.
            Log?.Invoke($"Redressement par OpenCV impossible ({ex.Message}) — repli sur ImageMagick.");
        }

        image.BackgroundColor = remplissage;
        image.Rotate(degres);
    }

    /// <summary>
    /// Le vrai travail. Rend faux — sans rien avoir touché — quand l'image ne se présente pas
    /// sous une forme dont on sait lire les octets ; l'appelant repasse alors par ImageMagick.
    /// </summary>
    private static bool ParOpenCv(MagickImage image, double degres, MagickColor remplissage)
    {
        if (image.Width == 0 || image.Height == 0) return false;

        // La carte des canaux, exactement comme la lisent les corrections de tons — un
        // masque du sujet arrive en un seul canal, une photo en trois, et un PNG du
        // catalogue peut en avoir quatre.
        var carte = Carte(image);
        if (carte is null) return false;

        var largeur = (int)image.Width;
        var hauteur = (int)image.Height;

        byte[] octets;
        using (var pixels = image.GetPixels())
            octets = pixels.ToByteArray(carte)!;

        if (octets is null || octets.LongLength != (long)largeur * hauteur * carte.Length)
            return false;

        // Marshal.Copy compte en int, et au-delà on ne saurait plus recopier d'un bloc.
        // Aucune image du logiciel n'en approche — une planche 50×70 à 300 ppp pèse 146 Mo —
        // mais un fichier client démesuré, lui, n'a pas de limite connue.
        if (octets.LongLength > int.MaxValue) return false;

        // ⚠ UN CANAL PAR CANAL, ET SURTOUT PAS DE CAS PAR DÉFAUT.
        //
        // La première version écrivait `_ => CV_8UC4`, en pensant n'avoir affaire qu'à du
        // gris, du RVB ou du RVBA. Il manquait le GRIS AVEC ALPHA — deux canaux —, et c'est
        // la forme sous laquelle Magick ouvre un simple JPEG noir et blanc : `Gray`,
        // `ChannelCount = 2`. OpenCV lisait alors quatre octets par pixel dans un tampon qui
        // n'en portait que deux.
        //
        // Le résultat ne ressemblait à rien de connu : l'image sortait ÉCARLATE, rayée de
        // franges horizontales — le décalage grandissant d'une ligne à l'autre. Une image
        // fausse, pas un plantage : sans l'essai du pré-dimensionnement, elle serait partie
        // chez un client.
        MatType? type = carte.Length switch
        {
            1 => MatType.CV_8UC1,
            2 => MatType.CV_8UC2,
            3 => MatType.CV_8UC3,
            4 => MatType.CV_8UC4,
            _ => null,
        };
        if (type is null) return false;

        // ⚠ OPENCV ALLOUE ET POSSÈDE SES DEUX TAMPONS. ON NE LUI PRÊTE RIEN.
        //
        // La version évidente est d'envelopper le tableau .NET — <c>Mat.FromPixelData</c> ne
        // copie rien et c'est tentant. Elle a coûté une demi-heure : les essais isolés
        // passaient tous, et la série complète mourait d'une AccessViolationException dans
        // <c>warpAffine</c>, à un endroit DIFFÉRENT à chaque exécution. C'est la signature
        // d'une mémoire corrompue à distance, pas d'un cas de données particulier — un
        // tableau managé n'a rien à faire sous un pointeur natif, épinglé ou non.
        //
        // On paie donc deux recopies de 72 Mo — 0,05 s, contre les 11,9 s qu'on économise —
        // et plus personne ne partage d'adresse avec personne. Ce n'est pas une prudence
        // abstraite : ce chemin sert AUSSI les rendus menés en parallèle par
        // <c>PrintOrchestrator</c>, où une corruption ne se serait vue qu'en boutique.
        using var source = new Mat(hauteur, largeur, type.Value);
        if (!source.IsContinuous()) return false;
        System.Runtime.InteropServices.Marshal.Copy(octets, 0, source.Data, octets.Length);

        // OpenCV compte les angles dans l'autre sens qu'ImageMagick : positif = sens
        // trigonométrique là-bas, sens des aiguilles ici. Le signe se retourne donc, sans
        // quoi l'horizon partirait deux fois plus de travers qu'il ne l'était.
        using var matrice = Cv2.GetRotationMatrix2D(
            new Point2f(largeur / 2f, hauteur / 2f), -degres, 1.0);

        // La boîte englobante exacte, tirée du cosinus et du sinus que la matrice porte
        // déjà : les recalculer à part serait une seconde vérité à tenir d'accord.
        var cos = Math.Abs(matrice.Get<double>(0, 0));
        var sin = Math.Abs(matrice.Get<double>(0, 1));
        var nouvelleLargeur = (int)Math.Round(largeur * cos + hauteur * sin);
        var nouvelleHauteur = (int)Math.Round(largeur * sin + hauteur * cos);

        if (nouvelleLargeur <= 0 || nouvelleHauteur <= 0) return false;

        // recentrer : la rotation s'est faite autour du centre de l'ANCIEN canevas, il faut
        // reporter l'image au centre du nouveau
        matrice.Set(0, 2, matrice.Get<double>(0, 2) + (nouvelleLargeur - largeur) / 2.0);
        matrice.Set(1, 2, matrice.Get<double>(1, 2) + (nouvelleHauteur - hauteur) / 2.0);

        using var tourne = new Mat();
        Cv2.WarpAffine(
            source, tourne, matrice, new OpenCvSharp.Size(nouvelleLargeur, nouvelleHauteur),
            InterpolationFlags.Cubic, BorderTypes.Constant, Remplissage(remplissage, carte));

        if (tourne.Width != nouvelleLargeur || tourne.Height != nouvelleHauteur
            || !tourne.IsContinuous())
            return false;

        var attendus = (long)nouvelleLargeur * nouvelleHauteur * carte.Length;
        if (attendus > int.MaxValue) return false;

        var sortie = new byte[attendus];
        System.Runtime.InteropServices.Marshal.Copy(tourne.Data, sortie, 0, sortie.Length);

        // ⚠ « RA » N'EST PAS DU GRIS AVEC ALPHA, C'EST DU ROUGE AVEC ALPHA.
        //
        // La carte de relecture est en général celle de la lecture, et un gris SANS alpha
        // relu sur « R » redevient bien gris — vérifié, parce que le contraire aurait été une
        // explication commode. Mais « RA » ne suit pas la même règle : Magick y voit un canal
        // rouge et un canal alpha, et rend une image écarlate translucide.
        //
        // On étale donc le gris sur trois canaux et on redit ensuite que c'est un gris ;
        // Magick le ramène à ses deux canaux, et l'image retrouve la forme qu'elle avait en
        // entrant. C'est le cas d'un JPEG noir et blanc, et il n'a rien d'exotique.
        if (carte == "RA")
        {
            var points = (long)nouvelleLargeur * nouvelleHauteur;
            if (points > int.MaxValue / 4) return false;

            var etale = new byte[points * 4];
            for (long lu = 0, ecrit = 0; lu < sortie.LongLength; lu += 2, ecrit += 4)
            {
                etale[ecrit] = etale[ecrit + 1] = etale[ecrit + 2] = sortie[lu];
                etale[ecrit + 3] = sortie[lu + 1];
            }

            image.ReadPixels(etale, new PixelReadSettings(
                (uint)nouvelleLargeur, (uint)nouvelleHauteur, StorageType.Char, "RGBA"));

            image.ColorSpace = ColorSpace.Gray;
        }
        else
        {
            image.ReadPixels(sortie, new PixelReadSettings(
                (uint)nouvelleLargeur, (uint)nouvelleHauteur, StorageType.Char, carte));
        }

        image.ResetPage();
        return true;
    }

    /// <summary>
    /// La disposition des octets, ou null si l'image n'en présente aucune que l'on sache
    /// relire. Même règle que <c>ImageAdjuster.SurLesOctets</c> : on refuse d'écrire à
    /// l'aveugle dans un tampon dont on ne connaît pas la forme.
    /// </summary>
    private static string? Carte(MagickImage image)
    {
        if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Gray))
            return null;

        var canaux = (int)image.ChannelCount;
        var carte = image.ColorSpace == ColorSpace.Gray ? "R" : "RGB";
        if (canaux == carte.Length + 1) carte += "A";

        return canaux == carte.Length ? carte : null;
    }

    /// <summary>
    /// La couleur des coins, dans l'ordre des canaux qu'on vient de sortir — et non dans
    /// celui d'OpenCV.
    ///
    /// <c>Scalar</c> se lit d'ordinaire en B, V, R parce qu'OpenCV range ainsi ses images.
    /// Les nôtres viennent de Magick en R, V, B et n'ont subi AUCUNE conversion de couleur :
    /// une rotation ne fait que déplacer des octets. C'est donc l'ordre de la carte qui
    /// commande. Se tromper ici sortirait un fond bleu là où l'on a demandé du rouge — et
    /// passerait inaperçu sur le blanc et le noir, qui sont justement les deux seules
    /// couleurs que le pipeline demande aujourd'hui.
    /// </summary>
    private static Scalar Remplissage(MagickColor couleur, string carte) => carte.Length switch
    {
        1 => new Scalar(couleur.R),
        3 => new Scalar(couleur.R, couleur.G, couleur.B),
        _ => new Scalar(couleur.R, couleur.G, couleur.B, couleur.A),
    };
}
