using ImageMagick;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le RENDU de la planche d'index, et surtout son coût.
///
/// <see cref="IndexSheetTests"/> vérifie la disposition ; ici on vérifie ce qui se paie au
/// comptoir : la planche mettait une dizaine de secondes à sortir pour vingt-sept photos,
/// parce qu'elle redécodait chaque fichier au lieu de se servir des vignettes que la
/// planche-contact venait de mettre en cache.
/// </summary>
public class IndexSheetRenderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "IndexSheet-" + Guid.NewGuid().ToString("N"));

    private readonly string _cache;
    private readonly string _sortie;
    private readonly ThumbnailService _vignettes;

    private const int Dpi = 300;
    private static int Largeur => MmPx.ToPixels(152, Dpi);
    private static int Hauteur => MmPx.ToPixels(102, Dpi);

    public IndexSheetRenderTests()
    {
        _cache = Path.Combine(_root, "cache");
        _sortie = Path.Combine(_root, "planches");
        Directory.CreateDirectory(_root);
        _vignettes = new ThumbnailService(_cache);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Des photos couchées, assez grandes pour que le décodage se remarque.</summary>
    private List<string> Photos(int combien, uint largeur = 2400, uint hauteur = 1600)
    {
        var chemins = new List<string>(combien);
        for (var i = 0; i < combien; i++)
        {
            var chemin = Path.Combine(_root, $"photo-{i:00}.jpg");
            using var image = new MagickImage(
                MagickColor.FromRgb((byte)(20 + i * 7), 120, 200), largeur, hauteur);
            image.Write(chemin, MagickFormat.Jpeg);
            chemins.Add(chemin);
        }
        return chemins;
    }

    private IndexSheet.Result Rendre(
        IReadOnlyList<string> photos, IReadOnlyList<double>? rapports = null) =>
        IndexSheet.Render(
            new IndexSheet.Request(photos, Largeur, Hauteur, Dpi, "Index",
                new DateTime(2026, 8, 2), rapports),
            _vignettes,
            Path.Combine(_sortie, Guid.NewGuid().ToString("N")));

    /// <summary>
    /// Le cas de la boutique : vingt-sept photos, une planche, aux dimensions du tirage.
    /// </summary>
    [Fact]
    public void Vingt_sept_photos_donnent_une_planche_au_format_du_tirage()
    {
        var resultat = Rendre(Photos(27));

        Assert.Single(resultat.Files);
        Assert.True(File.Exists(resultat.Files[0]));

        using var planche = new MagickImage(resultat.Files[0]);
        Assert.Equal((uint)Largeur, planche.Width);
        Assert.Equal((uint)Hauteur, planche.Height);
    }

    /// <summary>
    /// LE point de la correction : une planche demandée après l'affichage de la grille ne
    /// redécode rien.
    ///
    /// La grille met ses vignettes en cache à 360 px ; la planche en réclame environ 220. Ces
    /// tailles ne se rencontraient jamais — clé de cache différente — et les vingt-sept
    /// fichiers repassaient au décodeur. On le constate ici sans chronomètre : après le
    /// passage de la grille, le rendu de la planche n'écrit AUCUN nouveau fichier de cache.
    /// </summary>
    [Fact]
    public void Une_planche_apres_la_grille_ne_redecode_aucune_photo()
    {
        var photos = Photos(27);

        // ce que fait la planche-contact en affichant ses vignettes
        foreach (var photo in photos) _vignettes.GetJpeg(photo);

        // les seules VIGNETTES : chacune a depuis un petit fichier compagnon « .dim » qui
        // porte la définition de l'original, et ce qu'on compte ici est ce qui a été
        // décodé, pas le nombre d'entrées du dossier
        var avant = Directory.GetFiles(_cache, "*.jpg").Length;
        Assert.Equal(27, avant);

        Rendre(photos);

        Assert.Equal(avant, Directory.GetFiles(_cache, "*.jpg").Length);
    }

    /// <summary>
    /// Les rapports fournis par l'appelant donnent la même planche que ceux lus dans les
    /// fichiers : l'économie ne change pas le résultat.
    /// </summary>
    [Fact]
    public void Les_rapports_fournis_donnent_la_meme_planche_que_les_rapports_lus()
    {
        var photos = Photos(12);

        var lus = Rendre(photos);
        var fournis = Rendre(photos, Enumerable.Repeat(2400.0 / 1600.0, 12).ToList());

        Assert.Equal(lus.PerSheet, fournis.PerSheet);
        Assert.Equal(lus.Columns, fournis.Columns);
        Assert.Equal(lus.Rows, fournis.Rows);
    }

    /// <summary>Un rapport inconnu (0) est lu dans le fichier, pas pris pour un carré.</summary>
    [Fact]
    public void Un_rapport_inconnu_est_lu_dans_le_fichier()
    {
        var photos = Photos(12);
        var partiels = Enumerable.Repeat(0.0, 12).ToList(); // rien de connu

        var lus = Rendre(photos);
        var partiel = Rendre(photos, partiels);

        Assert.Equal(lus.Columns, partiel.Columns);
        Assert.Equal(lus.Rows, partiel.Rows);
    }

    /// <summary>
    /// La vignette d'affichage est rendue AVEC la planche : l'appelant n'a plus à relire —
    /// donc à redécoder — le fichier qu'on vient d'écrire.
    /// </summary>
    [Fact]
    public void Chaque_planche_rend_sa_vignette_d_affichage()
    {
        var resultat = Rendre(Photos(27));

        Assert.Equal(resultat.Files.Count, resultat.Thumbnails.Count);

        using var vignette = new MagickImage(resultat.Thumbnails[0]);
        Assert.True(vignette.Width <= 360, $"vignette de {vignette.Width}px : trop grande");
        Assert.True(vignette.Width > 100, "vignette illisible");

        // même forme que la planche : c'est bien elle qu'on montre
        using var planche = new MagickImage(resultat.Files[0]);
        Assert.Equal(
            planche.Width / (double)planche.Height,
            vignette.Width / (double)vignette.Height,
            1);
    }

    /// <summary>
    /// Une photo illisible laisse sa case blanche et n'emporte pas la planche : le client
    /// attend au comptoir, vingt-six vignettes valent mieux qu'une erreur.
    /// </summary>
    [Fact]
    public void Une_photo_illisible_n_emporte_pas_la_planche()
    {
        var photos = Photos(6);
        var casse = Path.Combine(_root, "casse.jpg");
        File.WriteAllText(casse, "ceci n'est pas un JPEG");
        photos.Add(casse);

        var resultat = Rendre(photos);

        Assert.Single(resultat.Files);
        Assert.True(File.Exists(resultat.Files[0]));
    }
}
