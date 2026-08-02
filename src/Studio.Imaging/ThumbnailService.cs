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

    private readonly string _cacheDir;

    public ThumbnailService(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    /// <summary>
    /// Une vignette d'AU MOINS <paramref name="boxPx"/> de côté, en JPEG.
    ///
    /// La taille rendue est arrondie au palier supérieur : on ne renvoie jamais moins fin que
    /// demandé, mais on accepte volontiers plus fin s'il traîne déjà en cache.
    /// </summary>
    public byte[] GetJpeg(string sourcePath, int boxPx = Defaut)
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
                return File.ReadAllBytes(candidat);
            }
            catch (IOException)
            {
                // vignette en cours d'écriture par un autre fil : on essaie le palier suivant
            }
        }

        MagickInit.Configure();

        var settings = new MagickReadSettings();
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{demande * 2}x{demande * 2}");

        using var image = new MagickImage(sourcePath, settings);
        image.AutoOrient();
        image.Thumbnail((uint)demande, (uint)demande); // conserve les proportions dans la boîte
        image.Quality = 82;
        var bytes = image.ToByteArray(MagickFormat.Jpeg);

        try
        {
            File.WriteAllBytes(CheminCache(sourcePath, demande), bytes);
        }
        catch (IOException)
        {
            // cache plein ou verrouillé : tant pis, la vignette est déjà en mémoire
        }
        return bytes;
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

    private static string CacheKey(string path, int box)
    {
        var info = new FileInfo(path);
        var raw = $"{path.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{box}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }
}
