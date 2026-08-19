using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// LA FEUILLE BLANCHE EST UN RÉGLAGE, ET IL DOIT SURVIVRE AUX ANCIENS FICHIERS.
///
/// <see cref="PosteSettings"/> est un record positionnel : la bascule est arrivée EN FIN de
/// liste, après <c>SupportsMasques</c>, et elle vaut <b>vrai</b> par défaut — c'est le seul
/// réglage de ce fichier dans ce cas.
///
/// Les quatre boutiques ont déjà un <c>poste.json</c> qui ne porte pas la clé. Si la lecture
/// retombait sur le <c>default</c> du type plutôt que sur celui du paramètre, elles
/// hériteraient d'un « faux » que personne n'a coché, et la feuille blanche ne sortirait
/// jamais sans qu'on comprenne pourquoi. C'est ce que ce fichier vérifie.
/// </summary>
public class SeparationDesCommandesTests
{
    private static string Dossier()
    {
        var chemin = Path.Combine(Path.GetTempPath(), "StudioPoste-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(chemin);
        return chemin;
    }

    /// <summary>Un poste neuf sépare les commandes : c'est la demande d'origine.</summary>
    [Fact]
    public void Vrai_par_defaut()
    {
        Assert.True(new PosteSettings().SeparerLesCommandes);
    }

    /// <summary>
    /// <b>Le cas des quatre boutiques.</b> Leur fichier date d'avant la file d'attente et
    /// ne porte pas la clé : elles doivent séparer les commandes malgré tout.
    /// </summary>
    [Fact]
    public void Un_fichier_d_avant_le_reglage_separe_quand_meme()
    {
        var dossier = Dossier();
        try
        {
            File.WriteAllText(Path.Combine(dossier, "poste.json"),
                """
                {
                  "DiLandRacine": "C:\\DiLand",
                  "ImprimanteSublimation": "DS620",
                  "CadrageAutoVisage": true
                }
                """);

            var poste = PosteSettings.Load(dossier);

            Assert.True(poste.SeparerLesCommandes);

            // et le reste du fichier est toujours lu comme avant
            Assert.Equal("C:\\DiLand", poste.DiLandRacine);
            Assert.True(poste.CadrageAutoVisage);
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    /// <summary>Décoché, ça reste décoché : un défaut à vrai ne doit pas écraser un choix.</summary>
    [Fact]
    public void Un_refus_explicite_est_respecte()
    {
        var dossier = Dossier();
        try
        {
            PosteSettings.Save(dossier, new PosteSettings(SeparerLesCommandes: false));

            Assert.False(PosteSettings.Load(dossier).SeparerLesCommandes);
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    /// <summary>Un fichier illisible ne doit pas empêcher le poste de démarrer.</summary>
    [Fact]
    public void Un_fichier_abime_retombe_sur_les_defauts()
    {
        var dossier = Dossier();
        try
        {
            File.WriteAllText(Path.Combine(dossier, "poste.json"), "{ ceci n'est pas du JSON");

            Assert.True(PosteSettings.Load(dossier).SeparerLesCommandes);
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }
}
