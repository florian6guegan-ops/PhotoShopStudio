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
    private readonly string _cacheDir;

    public ThumbnailService(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public byte[] GetJpeg(string sourcePath, int boxPx = 360)
    {
        var cachePath = Path.Combine(_cacheDir, CacheKey(sourcePath, boxPx) + ".jpg");
        if (File.Exists(cachePath))
            return File.ReadAllBytes(cachePath);

        MagickInit.Configure();

        var settings = new MagickReadSettings();
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{boxPx * 2}x{boxPx * 2}");

        using var image = new MagickImage(sourcePath, settings);
        image.AutoOrient();
        image.Thumbnail((uint)boxPx, (uint)boxPx); // conserve les proportions dans la boîte
        image.Quality = 82;
        var bytes = image.ToByteArray(MagickFormat.Jpeg);

        try
        {
            File.WriteAllBytes(cachePath, bytes);
        }
        catch (IOException)
        {
            // cache plein ou verrouillé : tant pis, la vignette est déjà en mémoire
        }
        return bytes;
    }

    private static string CacheKey(string path, int box)
    {
        var info = new FileInfo(path);
        var raw = $"{path.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{box}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }
}
