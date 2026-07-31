using System.Text.RegularExpressions;

namespace Studio.Tests;

/// <summary>
/// Vérifie que chaque <c>{StaticResource X}</c> des vues désigne une ressource qui existe.
///
/// WPF ne le signale qu'à l'ouverture de l'écran, en pleine boutique : une clé mal
/// orthographiée compile sans broncher puis fait échouer la vue devant le client.
/// C'est arrivé le 31/07/2026 avec « Subtitle », qui s'appelle en réalité « Hint ».
/// </summary>
public class XamlResourceTests
{
    private static readonly Regex CleDefinie = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CleUtilisee = new(@"\{StaticResource\s+([^\}\s]+)\s*\}", RegexOptions.Compiled);

    private static string RacineApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidat = Path.Combine(dir.FullName, "src", "Studio.App");
            if (Directory.Exists(candidat)) return candidat;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Dossier src/Studio.App introuvable depuis " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> FichiersXaml(string racine) =>
        Directory.EnumerateFiles(racine, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static HashSet<string> ClesDe(string fichier) =>
        CleDefinie.Matches(File.ReadAllText(fichier)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Toutes_les_ressources_statiques_des_vues_existent()
    {
        var racine = RacineApp();
        var fichiers = FichiersXaml(racine).ToList();
        Assert.NotEmpty(fichiers);

        // les ressources d'App.xaml sont visibles depuis toutes les vues
        var globales = ClesDe(Path.Combine(racine, "App.xaml"));
        Assert.Contains("PageTitle", globales);

        var manquantes = new List<string>();

        foreach (var fichier in fichiers)
        {
            var locales = ClesDe(fichier);
            var texte = File.ReadAllText(fichier);

            foreach (Match usage in CleUtilisee.Matches(texte))
            {
                var cle = usage.Groups[1].Value;

                // les ressources système (SystemColors.X, etc.) ne nous concernent pas
                if (cle.Contains('.')) continue;

                if (!globales.Contains(cle) && !locales.Contains(cle))
                    manquantes.Add($"{Path.GetFileName(fichier)} → {{StaticResource {cle}}}");
            }
        }

        Assert.True(manquantes.Count == 0,
            "Ressources introuvables, ces écrans planteraient à l'ouverture :\n  " +
            string.Join("\n  ", manquantes));
    }

    /// <summary>
    /// Les gestionnaires d'événements référencés en XAML doivent exister dans le
    /// code-behind : là encore, WPF ne s'en aperçoit qu'à l'ouverture de l'écran.
    /// </summary>
    [Fact]
    public void Tous_les_gestionnaires_d_evenements_existent()
    {
        var racine = RacineApp();
        var evenement = new Regex(@"\b(?:Click|SelectionChanged|TextChanged|Checked|Unchecked|SizeChanged|" +
                                  @"MouseLeftButtonUp|MouseLeftButtonDown|MouseMove|MouseWheel|Loaded|" +
                                  @"ManipulationStarting|ManipulationDelta)=""([A-Za-z_][A-Za-z0-9_]*)""",
            RegexOptions.Compiled);

        var manquants = new List<string>();

        foreach (var fichier in FichiersXaml(racine))
        {
            var codeBehind = fichier + ".cs";
            if (!File.Exists(codeBehind)) continue;

            var code = File.ReadAllText(codeBehind);
            foreach (Match m in evenement.Matches(File.ReadAllText(fichier)))
            {
                var methode = m.Groups[1].Value;
                if (!code.Contains(methode, StringComparison.Ordinal))
                    manquants.Add($"{Path.GetFileName(fichier)} → {methode}");
            }
        }

        Assert.True(manquants.Count == 0,
            "Gestionnaires absents du code-behind :\n  " + string.Join("\n  ", manquants));
    }
}
