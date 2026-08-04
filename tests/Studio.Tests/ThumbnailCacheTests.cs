using ImageMagick;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le cache des vignettes, et sa règle centrale : une vignette PLUS FINE que demandée fait
/// l'affaire.
///
/// Sans cette règle, chaque appelant demandait sa taille au pixel près et n'atteignait jamais
/// le cache d'un autre. La planche d'index en réclamait 219 là où la planche-contact venait
/// d'en écrire 360 : vingt-sept fichiers redécodés pour rien, devant le client.
/// </summary>
public class ThumbnailCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Thumbs-" + Guid.NewGuid().ToString("N"));

    private readonly string _cache;
    private readonly string _photo;
    private readonly ThumbnailService _vignettes;

    public ThumbnailCacheTests()
    {
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_root);
        _vignettes = new ThumbnailService(_cache);

        _photo = Path.Combine(_root, "photo.jpg");
        using var image = new MagickImage(MagickColors.SteelBlue, 2400, 1600);
        image.Write(_photo, MagickFormat.Jpeg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Les VIGNETTES en cache. Le compte porte sur les seuls « .jpg » : chacune a depuis un
    /// petit fichier compagnon « .dim » qui porte la définition de l'original, et ce qu'on
    /// vérifie ici est le nombre de vignettes produites, pas le nombre d'entrées du dossier.
    /// </summary>
    private int FichiersEnCache() => Directory.GetFiles(_cache, "*.jpg").Length;

    private static uint Cote(byte[] jpeg)
    {
        using var image = new MagickImage(jpeg);
        return Math.Max(image.Width, image.Height);
    }

    [Fact]
    public void Une_demande_plus_petite_reprend_la_vignette_deja_en_cache()
    {
        _vignettes.GetJpeg(_photo);          // ce que fait la planche-contact
        Assert.Equal(1, FichiersEnCache());

        var reprise = _vignettes.GetJpeg(_photo, 219); // ce que demande la planche d'index

        Assert.Equal(1, FichiersEnCache());  // rien de neuf n'a été produit
        Assert.Equal((uint)ThumbnailService.Defaut, Cote(reprise));
    }

    /// <summary>
    /// La taille par défaut couvre le plafond de la planche d'index, sans quoi les deux ne se
    /// rencontrent jamais : sur un 30×40, la planche réclamait 751 px, montait au palier 1024 et
    /// redécodait les trente-six fichiers (5 109 ms mesurés).
    /// </summary>
    [Fact]
    public void Le_plafond_de_la_planche_index_est_couvert_par_le_cache_de_la_grille()
    {
        _vignettes.GetJpeg(_photo);

        // ce que demande une cellule de 30×40, plafonnée par IndexSheet
        var plafonnee = _vignettes.GetJpeg(_photo, ThumbnailService.Defaut);

        Assert.Equal(1, FichiersEnCache());
        Assert.Equal((uint)ThumbnailService.Defaut, Cote(plafonnee));
    }

    [Fact]
    public void Une_demande_plus_grande_produit_bien_une_vignette_plus_fine()
    {
        _vignettes.GetJpeg(_photo);

        var fine = _vignettes.GetJpeg(_photo, 800);

        Assert.Equal(2, FichiersEnCache());
        Assert.True(Cote(fine) >= 800, $"vignette de {Cote(fine)}px, moins fine que demandé");
    }

    /// <summary>
    /// Les tailles sont arrondies à des paliers : deux demandes voisines partagent le même
    /// fichier au lieu d'en écrire deux.
    /// </summary>
    [Fact]
    public void Deux_demandes_voisines_partagent_le_meme_fichier()
    {
        _vignettes.GetJpeg(_photo, 400);
        _vignettes.GetJpeg(_photo, 500);

        Assert.Equal(1, FichiersEnCache());
    }

    /// <summary>
    /// La vignette n'est jamais moins fine que demandé — c'est ce qui autorise la reprise.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(219)]
    [InlineData(360)]
    [InlineData(361)]
    [InlineData(1000)]
    public void Une_vignette_n_est_jamais_moins_fine_que_demandee(int demande)
    {
        var jpeg = _vignettes.GetJpeg(_photo, demande);

        Assert.True(Cote(jpeg) >= demande, $"{demande}px demandés, {Cote(jpeg)}px rendus");
    }

    /// <summary>Les proportions de la photo sont conservées, quelle que soit la taille.</summary>
    [Fact]
    public void Les_proportions_sont_conservees()
    {
        var jpeg = _vignettes.GetJpeg(_photo, 219);

        using var image = new MagickImage(jpeg);
        Assert.Equal(2400.0 / 1600.0, image.Width / (double)image.Height, 1);
    }

    // — la définition de l'original, rendue avec la vignette —

    /// <summary>
    /// La grille affiche la définition et le rapport sur chaque tuile. Elle les demandait
    /// par un SECOND parcours du fichier ; ils viennent maintenant de la lecture qui
    /// fabrique la vignette.
    /// </summary>
    [Fact]
    public void La_definition_de_l_original_est_rendue_avec_la_vignette()
    {
        var lue = _vignettes.Lire(_photo);

        Assert.Equal(2400, lue.SourceWidth);
        Assert.Equal(1600, lue.SourceHeight);
    }

    /// <summary>
    /// <b>C'est tout l'objet du fichier compagnon</b> : cache chaud, le CONTENU de
    /// l'original n'est plus lu du tout. C'est le second parcours qu'on cherchait à
    /// supprimer — sur une carte SD, ce qui coûte est d'ouvrir le fichier, pas de le
    /// décoder.
    ///
    /// Le test remplace les pixels par du charabia de MÊME longueur et remet la date : la
    /// clé de cache est donc inchangée, mais le fichier est devenu indécodable. S'il
    /// fallait encore le lire, l'appel lèverait.
    /// </summary>
    [Fact]
    public void Cache_chaud_le_contenu_de_l_original_n_est_plus_lu()
    {
        var premiere = _vignettes.Lire(_photo);

        var taille = new FileInfo(_photo).Length;
        var date = File.GetLastWriteTimeUtc(_photo);
        File.WriteAllBytes(_photo, new byte[taille]);
        File.SetLastWriteTimeUtc(_photo, date);

        var seconde = _vignettes.Lire(_photo);

        Assert.Equal(2400, seconde.SourceWidth);
        Assert.Equal(1600, seconde.SourceHeight);
        Assert.Equal(premiere.Jpeg.Length, seconde.Jpeg.Length);
    }

    /// <summary>
    /// Les vignettes mises en cache avant le fichier compagnon n'en ont pas : la définition
    /// se relit alors sur l'original, une fois, et le compagnon est déposé pour les
    /// suivantes. Sans ce repli, les 4 300 vignettes déjà en cache rendraient 0 × 0.
    /// </summary>
    [Fact]
    public void Une_vignette_d_avant_le_compagnon_retrouve_sa_definition()
    {
        _vignettes.Lire(_photo);

        // on remet le cache dans l'état d'avant : la vignette, sans son compagnon
        foreach (var compagnon in Directory.GetFiles(_cache, "*.dim")) File.Delete(compagnon);

        var lue = _vignettes.Lire(_photo);

        Assert.Equal(2400, lue.SourceWidth);
        Assert.Equal(1600, lue.SourceHeight);
        Assert.Single(Directory.GetFiles(_cache, "*.dim")); // et le compagnon est reposé
    }

    /// <summary>
    /// Une photo modifiée ne doit pas ressortir de l'ancien cache — la clé porte la date et
    /// la taille du fichier, la reprise par palier ne doit pas contourner cela.
    /// </summary>
    [Fact]
    public void Une_photo_modifiee_n_est_pas_reprise_de_l_ancien_cache()
    {
        _vignettes.GetJpeg(_photo);

        using (var autre = new MagickImage(MagickColors.Firebrick, 1200, 1200))
            autre.Write(_photo, MagickFormat.Jpeg);
        File.SetLastWriteTimeUtc(_photo, DateTime.UtcNow.AddSeconds(5));

        var apres = _vignettes.GetJpeg(_photo, 219);

        using var image = new MagickImage(apres);
        Assert.Equal(1.0, image.Width / (double)image.Height, 1); // la nouvelle photo est carrée
    }
}
