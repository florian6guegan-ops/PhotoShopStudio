using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// La recherche du dépôt DiLand.
///
/// <b>Pourquoi elle existe</b> : le chemin était écrit en dur — « C:\Program Files (x86)\
/// DiLand Studio 2\… ». Juste sur le poste de la boutique, faux partout ailleurs. Une
/// installation sur D:, une version « DiLand Studio 3 », et Studio n'ouvrait plus une seule
/// commande de borne. C'était le premier obstacle à donner l'application à un collègue.
/// </summary>
public class DiLandLocatorTests : IDisposable
{
    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "DiLandLocator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* au mieux */ }
    }

    /// <summary>Fabrique une installation DiLand plausible, et rend son dossier racine.</summary>
    private string Installation(string nom, bool avecBase = true, bool avecCommandes = true)
    {
        var racine = Path.Combine(_racine, nom);
        var depot = Path.Combine(racine, @"Data\AllUsersData\Repositories\Default");
        Directory.CreateDirectory(depot);

        if (avecBase) File.WriteAllText(Path.Combine(depot, "Database.db"), "");
        if (avecCommandes) Directory.CreateDirectory(Path.Combine(depot, "Orders"));

        return racine;
    }

    // ————— ce qu'on accepte comme chemin réglé —————

    /// <summary>
    /// On ne peut pas demander à quelqu'un de retenir
    /// « Data\AllUsersData\Repositories\Default » : le dossier d'installation suffit.
    /// </summary>
    [Fact]
    public void Le_dossier_d_installation_suffit()
    {
        var racine = Installation("DiLand Studio 2");

        var depot = DiLandLocator.DepotDe(racine);

        Assert.NotNull(depot);
        Assert.True(DiLandLocator.EstUnDepot(depot!));
    }

    /// <summary>Mais le dépôt lui-même est accepté aussi : c'est ce que l'ancien réglage contenait.</summary>
    [Fact]
    public void Le_depot_lui_meme_est_accepte()
    {
        var racine = Installation("DiLand Studio 2");
        var depot = Path.Combine(racine, @"Data\AllUsersData\Repositories\Default");

        Assert.Equal(depot, DiLandLocator.DepotDe(depot));
    }

    [Fact]
    public void Un_chemin_qui_ne_mene_a_rien_est_refuse()
    {
        Assert.Null(DiLandLocator.DepotDe(Path.Combine(_racine, "nulle-part")));
        Assert.Null(DiLandLocator.DepotDe(""));
        Assert.Null(DiLandLocator.DepotDe("   "));
    }

    /// <summary>Un chemin syntaxiquement impossible ne doit pas faire tomber le démarrage.</summary>
    [Fact]
    public void Un_chemin_absurde_ne_leve_pas()
    {
        Assert.Null(DiLandLocator.DepotDe("|<>:*?"));
        Assert.False(DiLandLocator.EstUnDepot("|<>:*?"));
    }

    // ————— ce qui fait un dépôt —————

    /// <summary>
    /// La base OU les commandes, jamais les deux exigées : DiLand purge sa base alors que
    /// les dossiers de commandes restent, et Studio sait encore en tirer les photos.
    /// Exiger les deux ferait déclarer introuvable un dépôt parfaitement exploitable.
    /// </summary>
    [Fact]
    public void Les_dossiers_de_commandes_suffisent_sans_la_base()
    {
        var racine = Installation("DiLand purgé", avecBase: false, avecCommandes: true);

        Assert.NotNull(DiLandLocator.DepotDe(racine));
    }

    [Fact]
    public void La_base_suffit_sans_les_dossiers_de_commandes()
    {
        var racine = Installation("DiLand neuf", avecBase: true, avecCommandes: false);

        Assert.NotNull(DiLandLocator.DepotDe(racine));
    }

    [Fact]
    public void Un_dossier_vide_n_est_pas_un_depot()
    {
        var racine = Installation("DiLand vide", avecBase: false, avecCommandes: false);

        Assert.Null(DiLandLocator.DepotDe(racine));
    }

    // ————— la préséance —————

    /// <summary>
    /// <b>Le réglage de l'opérateur l'emporte toujours.</b> Lui seul sait où est son
    /// installation, et une détection qui passerait devant son choix serait impossible à
    /// contourner — c'est pourtant le seul filet quand la détection se trompe.
    /// </summary>
    [Fact]
    public void Le_chemin_regle_l_emporte_sur_la_detection()
    {
        var racine = Installation("DiLand ailleurs");

        var trouve = DiLandLocator.Trouver(racine);

        Assert.NotNull(trouve);
        Assert.StartsWith(racine, trouve!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Un réglage qui ne mène à rien ne doit pas EMPÊCHER la détection : un chemin devenu
    /// faux — disque changé, DiLand réinstallé — laisserait sinon l'application aveugle
    /// alors qu'elle sait très bien retrouver le dépôt toute seule.
    /// </summary>
    [Fact]
    public void Un_reglage_perime_laisse_la_detection_reprendre_la_main()
    {
        var reglagePerime = Path.Combine(_racine, "disque-disparu");

        // sur ce poste la boutique a un vrai DiLand ; ailleurs il n'y en a pas, et les
        // deux réponses sont bonnes. Ce qui compte : on ne rend PAS le chemin périmé.
        var trouve = DiLandLocator.Trouver(reglagePerime);

        Assert.NotEqual(reglagePerime, trouve);
    }

    /// <summary>
    /// On rend toujours quelque chose : un message d'erreur qui nomme un chemin plausible
    /// aide plus qu'un vide.
    /// </summary>
    [Fact]
    public void On_rend_toujours_un_chemin_nommable()
    {
        Assert.False(string.IsNullOrWhiteSpace(DiLandLocator.TrouverOuDefaut(null)));
    }
}
