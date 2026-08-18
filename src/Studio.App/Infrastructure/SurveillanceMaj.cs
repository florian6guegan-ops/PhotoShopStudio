using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using Studio.Core.Cloud;

namespace Studio.App.Infrastructure;

/// <summary>
/// Surveille les publications et prévient quand une version plus récente paraît.
///
/// <b>Elle ne fait qu'ANNONCER.</b> Rien n'est téléchargé, rien n'est installé : l'opérateur
/// décide dans les réglages. Un logiciel de comptoir ne se met pas à jour tout seul au milieu
/// d'une commande.
///
/// <b>Pourquoi c'est sorti des fenêtres.</b> Le bandeau vivait dans <c>MainWindow</c>, donc
/// dans le Studio complet et lui seul. Studio Photo Identité est né plus tard, avec sa propre
/// fenêtre, et n'a jamais rien annoncé : ses postes ont tourné deux versions en retard sans
/// que personne à la boutique puisse le savoir — kodakidpc était encore en 1.5.27 le
/// 18/08/2026 alors que la 1.5.29 était parue. Les BOUTONS se doublent, ce qu'ils font, non.
///
/// ⚠ <b>Et surtout : chaque logiciel a SA suite de publications.</b> Le Studio lit les
/// étiquettes <c>v1.5.29</c>, Identité les <c>identite-v1.5.29</c>. Recopier la surveillance
/// dans la seconde fenêtre aurait presque sûrement recopié aussi le préfixe du Studio — et
/// Identité aurait annoncé la version de l'autre logiciel, donc une mise à jour qui ne
/// l'installe pas.
/// </summary>
public static class SurveillanceMaj
{
    /// <summary>
    /// Un poste reste ouvert toute la journée, et des versions paraissent en journée : sans
    /// cette reprise, une correction du matin ne se verrait que le lendemain.
    /// </summary>
    public static readonly TimeSpan Intervalle = TimeSpan.FromHours(3);

    /// <summary>La version de l'exécutable qui tourne.</summary>
    public static Version VersionInstallee =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Le vérificateur de CE logiciel-ci — voir <see cref="Logiciel.EstIdentite"/>.
    ///
    /// Publique parce que l'écran des réglages en a besoin lui aussi, et que la règle du
    /// préfixe ne doit exister qu'à un seul endroit.
    /// </summary>
    public static MiseAJour Verificateur(HttpClient client) =>
        new(client) { PrefixeEtiquette = Logiciel.EstIdentite ? "identite-v" : "v" };

    /// <summary>
    /// Lance la surveillance : une fois tout de suite, puis à chaque <see cref="Intervalle"/>.
    /// </summary>
    /// <param name="fenetre">
    /// Le répartiteur de la fenêtre : <paramref name="annoncer"/> est appelé dessus, parce
    /// qu'il touche à l'interface.
    /// </param>
    /// <param name="annoncer">
    /// Appelé avec la version disponible, et seulement si elle est plus récente que celle qui
    /// tourne. Jamais appelé quand tout est à jour, ni sur panne réseau : la surveillance est
    /// SILENCIEUSE en cas d'échec — un poste hors ligne ne doit pas afficher d'alarme.
    /// </param>
    public static void Demarrer(Dispatcher fenetre, Action<Version> annoncer)
    {
        ArgumentNullException.ThrowIfNull(fenetre);
        ArgumentNullException.ThrowIfNull(annoncer);

        var minuterie = new DispatcherTimer { Interval = Intervalle };
        minuterie.Tick += (_, _) => _ = RegarderAsync(fenetre, annoncer);
        minuterie.Start();

        _ = RegarderAsync(fenetre, annoncer);
    }

    private static async Task RegarderAsync(Dispatcher fenetre, Action<Version> annoncer)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var publiee = await Verificateur(client).DernierePubliee();

            if (publiee is null || !MiseAJour.EstPlusRecente(publiee.Version, VersionInstallee))
                return;

            fenetre.Invoke(() => annoncer(publiee.Version));
        }
        catch (Exception ex)
        {
            // Le réseau d'une boutique tombe, GitHub a des pannes : ce n'est pas au comptoir
            // de s'en occuper. On l'écrit et on réessaiera au prochain tour.
            FileLog.Write("Vérification de mise à jour en fond impossible", ex);
        }
    }
}
