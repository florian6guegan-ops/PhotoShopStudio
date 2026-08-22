using System.Security.Cryptography;
using System.Text;
using ImageMagick;

namespace Studio.Imaging;

/// <summary>
/// Vignettes économes : décodage JPEG avec indication de taille (jpeg:size —
/// jamais de 24 Mpx entier en mémoire pour une miniature), cache disque
/// invalidé par taille + date du fichier source.
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>
    /// Les seules tailles jamais produites.
    ///
    /// Sans elle, chaque appelant demandait sa taille exacte et n'atteignait jamais le cache
    /// d'un autre : la planche index réclamait 219 px là où la grille venait de mettre 360 px
    /// de côté, et les vingt-sept photos étaient redécodées pour rien. Une vignette PLUS FINE
    /// que demandé fait toujours l'affaire — c'est ce que <see cref="GetJpeg"/> exploite.
    ///
    /// 512 en tête parce que c'est la taille de la planche-contact (<see cref="Defaut"/>) : tout
    /// ce qui est en dessous s'y sert sans rien recalculer.
    /// </summary>
    private static readonly int[] Paliers = [360, 512, 720, 1024, 1440, 2048];

    /// <summary>
    /// Taille produite par défaut, celle de la planche-contact.
    ///
    /// 512 et non 360 : c'est ce cache que la planche d'index vient reprendre, et son plafond
    /// est de 512. Les deux se rejoignent donc toujours, quel que soit le format du produit —
    /// sur un 30×40, la planche réclamait 751 px, montait au palier 1024, et redécodait les
    /// trente-six fichiers de 39 Mpx (5 109 ms). Le palier au-dessus coûte quelques centaines
    /// de millisecondes au chargement de la grille, qui se fait en tâche de fond, et les fait
    /// gagner à chaque planche.
    /// </summary>
    public const int Defaut = 512;

    /// <summary>
    /// Taille des aperçus PLEIN ÉCRAN — cadrage, corrections, photo d'identité.
    ///
    /// <b>1440 et non 1600, parce que 1600 n'existe pas.</b> Les écrans demandaient 1600, qui
    /// n'est pas un palier : la demande montait donc à 2048, soit <b>le double de pixels à
    /// décoder</b> pour un aperçu que personne ne regarde à cette finesse. 1440 est le palier
    /// juste en dessous, il tient largement sur un moniteur de boutique, et il est atteint
    /// tel quel — sans arrondi vers le haut.
    ///
    /// <b>Et une seule constante pour tous les écrans.</b> Ils écrivaient chacun 1600 dans
    /// leur coin ; deux écrans qui divergeraient d'un palier décoderaient deux fois la même
    /// photo, et le cache ne les rejoindrait jamais — c'est le défaut que la liste des
    /// <see cref="Paliers"/> existe pour empêcher, et qu'un nombre écrit en dur ramenait.
    /// </summary>
    public const int Apercu = 1440;

    private readonly string _cacheDir;

    public ThumbnailService(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    /// <summary>
    /// Une vignette et la définition de la photo dont elle vient.
    /// </summary>
    /// <param name="Jpeg">La vignette.</param>
    /// <param name="SourceWidth">Largeur de l'original, orientation EXIF appliquée.</param>
    /// <param name="SourceHeight">Hauteur de l'original, orientation EXIF appliquée.</param>
    public sealed record Vignette(byte[] Jpeg, int SourceWidth, int SourceHeight);

    /// <summary>
    /// Une vignette d'AU MOINS <paramref name="boxPx"/> de côté, en JPEG.
    ///
    /// La taille rendue est arrondie au palier supérieur : on ne renvoie jamais moins fin que
    /// demandé, mais on accepte volontiers plus fin s'il traîne déjà en cache.
    /// </summary>
    public byte[] GetJpeg(string sourcePath, int boxPx = Defaut) => Lire(sourcePath, boxPx).Jpeg;

    /// <summary>
    /// La vignette ET la définition de l'original, en une seule ouverture du fichier.
    ///
    /// <b>Pourquoi les deux ensemble.</b> La grille affiche la définition et le rapport sur
    /// chaque tuile, comme DiLand. Elle les demandait à <c>ImagePipeline.GetOrientedSize</c>,
    /// c'est-à-dire par un SECOND parcours du fichier — un <c>Ping</c>, certes, mais qui
    /// ouvre quand même chaque photo une fois de plus. Sur une carte SD ou une clé USB,
    /// c'est le coût qui compte, pas le décodage. Et il était payé même quand la vignette
    /// était déjà en cache : rouvrir un dossier déjà vu touchait donc les 33 fichiers pour
    /// rien.
    ///
    /// La définition est donc mise en cache À CÔTÉ de la vignette, dans un fichier
    /// <c>.dim</c>. Cache chaud = deux petites lectures dans le cache, zéro accès à
    /// l'original.
    /// </summary>
    public Vignette Lire(string sourcePath, int boxPx = Defaut)
    {
        var demande = Palier(boxPx);

        // tout palier au moins égal à ce qui est demandé convient : on prend le premier déjà là
        foreach (var palier in Paliers)
        {
            if (palier < demande) continue;

            var candidat = CheminCache(sourcePath, palier);
            if (!File.Exists(candidat)) continue;

            try
            {
                var jpeg = File.ReadAllBytes(candidat);

                // Les vignettes d'avant ce fichier compagnon n'en ont pas : on lit alors la
                // définition à l'ancienne, une fois, et on la dépose pour les suivantes.
                //
                // <b>Seulement dans ce cas-là.</b> Le dépôt était fait à CHAQUE lecture, y
                // compris quand le fichier compagnon venait d'être lu : rouvrir un dossier de
                // 1200 photos réécrivait donc 1200 petits fichiers, depuis les huit fils qui
                // chargent la planche — soit exactement l'accès disque que ce cache existe
                // pour supprimer, et de la contention entre fils par-dessus.
                if (LireLaDefinition(candidat) is { } connue)
                    return new Vignette(jpeg, connue.Width, connue.Height);

                var (largeur, hauteur) = DefinitionDeLOriginal(sourcePath);
                EcrireLaDefinition(candidat, largeur, hauteur);

                return new Vignette(jpeg, largeur, hauteur);
            }
            catch (IOException)
            {
                // vignette en cours d'écriture par un autre fil : on essaie le palier suivant
            }
        }

        // Le décodeur JPEG ne sait réduire que par 1/2, 1/4, 1/8, et il choisit le premier
        // facteur qui reste AU MOINS aussi grand que l'indication. Demander le double de la
        // vignette (1024 pour 512) lui faisait décoder deux fois trop de pixels : 3 557 ms
        // pour 20 photos de 6 Mo, contre 2 378 ms à l'indication juste — un tiers de moins,
        // pour une vignette d'un kilo-octet de différence. Mesuré le 04/08/2026 sur les
        // photos de la commande 08-012.
        using var image = MagickInit.Lire(sourcePath, demande);

        // La définition de l'ORIGINAL, avant toute réduction : BaseWidth/BaseHeight ne
        // suivent pas l'échelle appliquée par le décodeur. L'orientation se lit ici, avant
        // AutoOrient — après, elle vaut TopLeft et le portrait passerait pour un paysage.
        var (sourceW, sourceH) = OrienterLesCotes(
            (int)image.BaseWidth, (int)image.BaseHeight, image.Orientation);

        image.AutoOrient();

        // L'ESPACE DE LA SOURCE, comme au rendu et à l'aperçu.
        //
        // La vignette n'est pas qu'une image de liste : c'est elle que le récapitulatif de
        // planche donne à composer (voir IdSheetRecapView.RendreUnePlanche). Sans cette
        // ligne, une photo Adobe RGB s'y montrait dans ses couleurs fausses, et l'opérateur
        // validait une planche qui ne ressemblait pas à celle qui allait sortir.
        EspaceCouleurSource.RamenerEnSrgb(image);

        image.Thumbnail((uint)demande, (uint)demande); // conserve les proportions dans la boîte
        image.Quality = 82;
        var bytes = image.ToByteArray(MagickFormat.Jpeg);

        var chemin = CheminCache(sourcePath, demande);
        try
        {
            File.WriteAllBytes(chemin, bytes);
            EcrireLaDefinition(chemin, sourceW, sourceH);
        }
        catch (IOException)
        {
            // cache plein ou verrouillé : tant pis, la vignette est déjà en mémoire
        }
        return new Vignette(bytes, sourceW, sourceH);
    }

    /// <summary>
    /// Les cotes telles qu'on les VOIT, l'orientation EXIF appliquée : les quatre
    /// orientations à quart de tour échangent largeur et hauteur.
    ///
    /// Aucun essai ne la couvre : produire un JPEG portant une orientation EXIF avec
    /// Magick.NET s'est révélé peu fiable — l'étiquette écrite se relit à 0. La règle est
    /// en revanche celle, éprouvée, d'<c>ImagePipeline.GetOrientedSize</c>, dont ce code
    /// prend la place. À contrôler à l'œil sur une photo prise à la verticale : la tuile
    /// doit annoncer « 4000 × 6000 » et non l'inverse.
    /// </summary>
    private static (int Width, int Height) OrienterLesCotes(int width, int height, OrientationType orientation) =>
        orientation is OrientationType.LeftTop or OrientationType.RightTop
            or OrientationType.RightBottom or OrientationType.LeftBottom
            ? (height, width)
            : (width, height);

    /// <summary>Le fichier compagnon qui porte la définition de l'original.</summary>
    private static string CheminDefinition(string cheminVignette) => cheminVignette + ".dim";

    private static (int Width, int Height)? LireLaDefinition(string cheminVignette)
    {
        try
        {
            var chemin = CheminDefinition(cheminVignette);
            if (!File.Exists(chemin)) return null;

            var parts = File.ReadAllText(chemin).Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var w)
                && int.TryParse(parts[1], out var h)
                && w > 0 && h > 0)
                return (w, h);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // fichier compagnon illisible : on retombera sur la lecture de l'original
        }

        return null;
    }

    private static void EcrireLaDefinition(string cheminVignette, int width, int height)
    {
        try
        {
            File.WriteAllText(CheminDefinition(cheminVignette), $"{width}x{height}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // sans lui, on relira l'original la prochaine fois : coûteux, jamais faux
        }
    }

    /// <summary>
    /// La définition de l'original, lue dans son en-tête. Le repli quand le fichier
    /// compagnon manque — c'est le cas des vignettes mises en cache avant lui.
    /// </summary>
    private static (int Width, int Height) DefinitionDeLOriginal(string sourcePath)
    {
        MagickInit.Configure();

        using var image = new MagickImage();
        image.Ping(sourcePath);
        return OrienterLesCotes((int)image.Width, (int)image.Height, image.Orientation);
    }

    /// <summary>
    /// Le plus petit palier qui couvre la taille demandée. Au-delà du dernier, on rend la
    /// taille exacte : un appelant qui veut du 4000 px a ses raisons, et le cache n'a alors
    /// plus rien à partager.
    /// </summary>
    private static int Palier(int boxPx)
    {
        foreach (var palier in Paliers)
            if (palier >= boxPx)
                return palier;

        return boxPx;
    }

    private string CheminCache(string sourcePath, int box) =>
        Path.Combine(_cacheDir, CacheKey(sourcePath, box) + ".jpg");

    /// <summary>
    /// La façon dont une vignette est FABRIQUÉE, et non ce dont elle est tirée.
    ///
    /// <b>À changer dès que le rendu d'une vignette change</b> : la clé du cache ne regarde
    /// que le fichier source, si bien qu'une photo déjà vue garderait sa vignette d'avant
    /// pour toujours. C'est arrivé le 20/08/2026 avec la lecture de l'espace de couleur
    /// (voir <see cref="EspaceCouleurSource"/>) : sans ce marqueur, les photos Adobe RGB
    /// déjà ouvertes seraient restées dans leurs couleurs fausses, y compris à l'écran de
    /// récapitulatif qui compose la planche à partir d'elles.
    ///
    /// Le cache entier est recalculé une fois après la mise à jour, vignette par vignette,
    /// à mesure qu'on les regarde. Les anciennes partent au ménage du cache.
    /// </summary>
    private const string VersionDuRendu = "couleur-2";

    private static string CacheKey(string path, int box)
    {
        var info = new FileInfo(path);
        var raw = $"{path.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{box}"
                  + $"|{VersionDuRendu}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }
}
