using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// Le ménage du cache du poste.
///
/// Ce que ces essais tiennent :
///
/// - <b>trente jours, et les copies de photos de clients s'effacent</b> — la même rétention
///   que l'historique des photos d'identité, pour que la fiche et les pixels qu'elle désigne
///   disparaissent ensemble ;
/// - <b>la date vient du NOM du dossier</b>, jamais de la date d'écriture : une sauvegarde
///   ou une copie de disque la rafraîchiraient, et trois mois de photos resteraient là ;
/// - <b>ce qui n'est pas daté n'est pas touché.</b> Le cache porte aussi les vignettes et
///   les pages de PDF rendues, qui ne regardent pas ce ménage.
/// </summary>
public class MenageDuCacheTests : IDisposable
{
    private readonly string _cache =
        Path.Combine(Path.GetTempPath(), "Cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    private string JourneeDeTravail(DateTime jour)
    {
        var dossier = Path.Combine(_cache, "travail", jour.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, "IMG_1234-a1b2c3d4.jpg"), "photo");
        return dossier;
    }

    [Fact]
    public void Les_copies_de_plus_de_trente_jours_seffacent()
    {
        var vieille = JourneeDeTravail(DateTime.Today.AddDays(-31));

        Assert.Equal(1, MenageDuCache.PurgerLesCopiesDeTravail(_cache));
        Assert.False(Directory.Exists(vieille));
    }

    [Fact]
    public void Celles_de_moins_de_trente_jours_restent()
    {
        var hier = JourneeDeTravail(DateTime.Today.AddDays(-1));
        var limite = JourneeDeTravail(DateTime.Today.AddDays(-29));

        Assert.Equal(0, MenageDuCache.PurgerLesCopiesDeTravail(_cache));
        Assert.True(Directory.Exists(hier));
        Assert.True(Directory.Exists(limite));
    }

    [Fact]
    public void Un_dossier_qui_nest_pas_une_date_nest_pas_touche()
    {
        // le cache porte aussi les vignettes : elles ne regardent pas ce ménage
        var vignettes = Path.Combine(_cache, "travail", "en-cours");
        Directory.CreateDirectory(vignettes);

        Assert.Equal(0, MenageDuCache.PurgerLesCopiesDeTravail(_cache));
        Assert.True(Directory.Exists(vignettes));
    }

    [Fact]
    public void Un_cache_qui_nexiste_pas_ne_leve_rien()
    {
        Assert.Equal(0, MenageDuCache.PurgerLesCopiesDeTravail(
            Path.Combine(_cache, "jamais-cree")));
        Assert.Equal(0, MenageDuCache.PurgerLesMasques(
            Path.Combine(_cache, "jamais-cree")));
    }

    [Fact]
    public void Les_masques_de_plus_de_trente_jours_seffacent()
    {
        var dossier = Path.Combine(_cache, "masques", "birefnet-lite-fp16");
        Directory.CreateDirectory(dossier);

        var vieux = Path.Combine(dossier, "aaaa.png");
        var recent = Path.Combine(dossier, "bbbb.png");
        File.WriteAllText(vieux, "png");
        File.WriteAllText(recent, "png");
        File.SetLastWriteTime(vieux, DateTime.Now.AddDays(-31));

        Assert.Equal(1, MenageDuCache.PurgerLesMasques(_cache));
        Assert.False(File.Exists(vieux));
        Assert.True(File.Exists(recent));
    }
}
