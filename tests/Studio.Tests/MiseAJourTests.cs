using Studio.Core.Cloud;

namespace Studio.Tests;

/// <summary>
/// La mise à jour prise sur les publications du dépôt.
///
/// <b>Pourquoi elle existe.</b> Le raccourci du bureau recompile depuis les sources : c'est
/// le poste de celui qui développe. Un collègue n'a ni le SDK, ni le dépôt — sans mise à
/// jour automatique, chaque correction demanderait un déplacement ou un fichier transmis à
/// la main.
///
/// La lecture de la réponse est vérifiée SANS réseau : c'est elle qui décide si un poste
/// va remplacer son application, et elle doit se tromper du bon côté.
/// </summary>
public class MiseAJourTests
{
    /// <summary>Une réponse de GitHub, réduite aux champs que l'on lit.</summary>
    private static string Reponse(
        string etiquette = "v1.2.0",
        string nomArchive = "StudioPhoto-1.2.0.zip",
        bool brouillon = false,
        bool preversion = false) =>
        $$"""
        {
          "tag_name": "{{etiquette}}",
          "name": "Version {{etiquette}}",
          "body": "Corrige le liseré blanc.",
          "draft": {{(brouillon ? "true" : "false")}},
          "prerelease": {{(preversion ? "true" : "false")}},
          "assets": [
            {
              "name": "{{nomArchive}}",
              "size": 52428800,
              "browser_download_url": "https://github.com/x/y/releases/download/{{etiquette}}/{{nomArchive}}"
            }
          ]
        }
        """;

    // ————— ce qu'on retient —————

    [Fact]
    public void Une_publication_ordinaire_est_lue()
    {
        var version = MiseAJour.Lire(Reponse());

        Assert.NotNull(version);
        Assert.Equal(new Version(1, 2, 0), version!.Version);
        Assert.Contains("liseré blanc", version.Notes);
        Assert.EndsWith(".zip", version.Url);
        Assert.Equal("50,0 Mo", version.TailleLisible);
    }

    /// <summary>Les deux conventions se croisent : on ne fait pas échouer une mise à jour sur un « v ».</summary>
    [Theory]
    [InlineData("v1.2.0")]
    [InlineData("1.2.0")]
    [InlineData("V1.2.0")]
    [InlineData("  v1.2.0  ")]
    public void L_etiquette_est_lue_avec_ou_sans_v(string etiquette)
    {
        Assert.Equal(new Version(1, 2, 0), MiseAJour.LireLaVersion(etiquette));
    }

    // ————— ce qu'on refuse —————

    /// <summary>
    /// Un brouillon ou une préversion n'est pas destiné aux postes de la boutique : les y
    /// envoyer ferait tirer sur du code qu'on n'a pas fini d'écrire.
    /// </summary>
    [Fact]
    public void Un_brouillon_n_est_pas_propose()
    {
        Assert.Null(MiseAJour.Lire(Reponse(brouillon: true)));
    }

    [Fact]
    public void Une_preversion_n_est_pas_proposee()
    {
        Assert.Null(MiseAJour.Lire(Reponse(preversion: true)));
    }

    /// <summary>
    /// Une publication sans archive n'a rien à installer. Le dire vaut mieux que de
    /// proposer une mise à jour qui échouerait au téléchargement.
    /// </summary>
    [Fact]
    public void Une_publication_sans_archive_n_est_pas_proposee()
    {
        Assert.Null(MiseAJour.Lire(Reponse(nomArchive: "notes-de-version.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ceci n'est pas du JSON")]
    [InlineData("{}")]
    public void Une_reponse_inutilisable_ne_leve_pas(string json)
    {
        Assert.Null(MiseAJour.Lire(json));
    }

    /// <summary>Une étiquette qui n'est pas un numéro — « derniere », « prod » — est écartée.</summary>
    [Theory]
    [InlineData("derniere")]
    [InlineData("")]
    [InlineData("v")]
    public void Une_etiquette_qui_n_est_pas_un_numero_est_ecartee(string etiquette)
    {
        Assert.Null(MiseAJour.LireLaVersion(etiquette));
    }

    // ————— quand proposer —————

    /// <summary>
    /// <b>Strictement supérieur.</b> Republier la même version — parce qu'on a corrigé sa
    /// description — ne doit pas proposer une réinstallation à tous les postes.
    /// </summary>
    [Theory]
    [InlineData("1.2.0", "1.1.0", true)]
    [InlineData("1.2.1", "1.2.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.1.0", "1.2.0", false)]
    public void On_ne_propose_que_ce_qui_est_plus_recent(
        string publiee, string installee, bool attendu)
    {
        Assert.Equal(attendu,
            MiseAJour.EstPlusRecente(Version.Parse(publiee), Version.Parse(installee)));
    }

    // ————— l'installation —————

    /// <summary>
    /// Windows verrouille les fichiers d'un programme qui tourne : l'application ne peut
    /// pas se remplacer elle-même. Le script doit donc ATTENDRE sa fermeture avant de
    /// copier quoi que ce soit — sans cela, la mise à jour échouerait une fois sur deux et
    /// laisserait une installation à moitié remplacée.
    /// </summary>
    [Fact]
    public void Le_script_attend_la_fermeture_avant_de_copier()
    {
        var travail = Path.Combine(Path.GetTempPath(), "MajTest-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(travail, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Studio.App.dll"), "nouvelle version");

        var archive = Path.Combine(travail, "maj.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, archive);

        try
        {
            var script = MiseAJour.PreparerLInstallation(
                archive, @"C:\Studio", @"C:\Studio\Studio.App.exe");

            var texte = File.ReadAllText(script);

            Assert.Contains("tasklist", texte);
            Assert.Contains("Studio.App.exe", texte);
            Assert.Contains("robocopy", texte);

            // la copie ne doit pas précéder l'attente
            Assert.True(texte.IndexOf("tasklist", StringComparison.Ordinal)
                        < texte.IndexOf("robocopy", StringComparison.Ordinal),
                "le script copie avant d'avoir attendu la fermeture");

            // et le relais du minilab tient les mêmes DLL
            Assert.Contains("Studio.De100Host.exe", texte);
        }
        finally
        {
            try { Directory.Delete(travail, recursive: true); } catch { /* au mieux */ }
        }
    }

    /// <summary>L'adresse interrogée doit être celle du dépôt de la boutique.</summary>
    [Fact]
    public void L_adresse_interrogee_est_celle_du_depot()
    {
        Assert.Contains(MiseAJour.Depot, MiseAJour.UrlDerniereVersion);
        Assert.StartsWith("https://", MiseAJour.UrlDerniereVersion);
    }
}
