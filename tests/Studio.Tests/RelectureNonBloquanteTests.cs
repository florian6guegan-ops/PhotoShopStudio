using Studio.App.Infrastructure;

namespace Studio.Tests;

/// <summary>
/// La relecture du dépôt DiLand TENUE HORS DU FIL DE L'INTERFACE.
///
/// Le défaut corrigé : l'accueil relisait les commandes des bornes toutes les quinze
/// secondes sur le fil de l'interface. Tant que DiLand va bien, personne ne le voit ; le
/// jour où le dépôt ne répond plus, Studio attend avec lui et Windows déclare la fenêtre
/// figée au bout de cinq secondes. C'est arrivé deux fois à Créteil, le 12/08/2026 en
/// 1.4.3 puis le 14/08/2026 en 1.5.6 — les deux fois, DiLand était en difficulté au même
/// moment.
///
/// Ce qui compte ici : le plafond ne doit jamais ABANDONNER la lecture (une E/S bloquée ne
/// s'annule pas, et une réponse tardive reste la bonne réponse), et un dépôt bloqué ne doit
/// pas faire empiler un fil toutes les quinze secondes.
/// </summary>
public class RelectureNonBloquanteTests
{
    private static RelectureNonBloquante Relecture(double plafondMs = 50) =>
        new(TimeSpan.FromMilliseconds(plafondMs));

    // — le cas courant : le dépôt répond —

    [Fact]
    public async Task Une_lecture_qui_repond_pose_son_resultat()
    {
        var pose = 0;

        await Relecture().Demander(() => 7, resultat => pose = resultat);

        Assert.Equal(7, pose);
    }

    [Fact]
    public async Task Une_lecture_qui_repond_ne_previent_de_rien()
    {
        var retard = false;
        var echec = false;

        await Relecture().Demander(
            () => 7, _ => { }, enRetard: () => retard = true, enEchec: _ => echec = true);

        Assert.False(retard);
        Assert.False(echec);
    }

    // — LE point de la correction : le plafond rend la main sans abandonner la lecture —

    [Fact]
    public async Task Une_lecture_trop_longue_previent_avant_d_avoir_fini()
    {
        using var porte = new ManualResetEventSlim(false);
        var prevenu = new TaskCompletionSource();

        var demande = Relecture().Demander(
            () => { porte.Wait(); return 7; },
            _ => { },
            enRetard: () => prevenu.SetResult());

        // le rappel arrive alors que la lecture n'a toujours pas rendu la main
        await prevenu.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(demande.IsCompleted);

        porte.Set();
        await demande;
    }

    /// <summary>
    /// Une lecture partie en retard n'est pas perdue : un dépôt qui répond enfin au bout de
    /// trente secondes a quand même la bonne réponse, et elle se pose à son retour.
    /// </summary>
    [Fact]
    public async Task Une_lecture_en_retard_pose_quand_meme_son_resultat()
    {
        using var porte = new ManualResetEventSlim(false);
        var pose = 0;

        var demande = Relecture().Demander(
            () => { porte.Wait(); return 7; },
            resultat => pose = resultat);

        porte.Set();
        await demande;

        Assert.Equal(7, pose);
    }

    // — un dépôt illisible, ce n'est pas un dépôt lent —

    [Fact]
    public async Task Une_lecture_qui_leve_passe_par_l_echec_et_ne_pose_rien()
    {
        var pose = false;
        Exception? attrapee = null;

        await Relecture().Demander<int>(
            () => throw new IOException("order.json occupé"),
            _ => pose = true,
            enEchec: ex => attrapee = ex);

        Assert.False(pose);
        Assert.IsType<IOException>(attrapee);
    }

    /// <summary>Un échec ne doit pas laisser la relecture verrouillée pour toujours.</summary>
    [Fact]
    public async Task Une_lecture_qui_leve_rend_quand_meme_la_main()
    {
        var relecture = Relecture();

        await relecture.Demander<int>(() => throw new IOException(), _ => { }, enEchec: _ => { });

        Assert.False(relecture.EnCours);
    }

    // — pas deux à la fois —

    /// <summary>
    /// Sans ce verrou, un dépôt bloqué ferait partir un fil de plus toutes les quinze
    /// secondes, jusqu'à en manquer.
    /// </summary>
    [Fact]
    public async Task Une_demande_pendant_une_lecture_en_cours_est_ignoree()
    {
        var relecture = Relecture();
        using var demarree = new ManualResetEventSlim(false);
        using var porte = new ManualResetEventSlim(false);
        var lectures = 0;

        var premiere = relecture.Demander(
            () => { Interlocked.Increment(ref lectures); demarree.Set(); porte.Wait(); return 7; },
            _ => { });

        // Attendre que la lecture ait VRAIMENT commencé, et pas seulement que le verrou soit
        // pris : le verrou se prend sur le fil appelant, le comptage sur le fil de fond. Se
        // fier au verrou faisait échouer ce test une fois sur plusieurs, la seconde demande
        // arrivant avant que la première n'ait compté.
        demarree.Wait(TimeSpan.FromSeconds(5));

        await relecture.Demander(() => { Interlocked.Increment(ref lectures); return 7; }, _ => { });
        Assert.Equal(1, lectures);

        porte.Set();
        await premiere;
    }

    [Fact]
    public async Task Une_lecture_finie_laisse_passer_la_suivante()
    {
        var relecture = Relecture();
        var lectures = 0;

        await relecture.Demander(() => ++lectures, _ => { });
        await relecture.Demander(() => ++lectures, _ => { });

        Assert.Equal(2, lectures);
    }
}
