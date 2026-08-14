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

    /// <summary>
    /// <b>Le chemin d'installation survit à la page de codes de cmd.exe.</b>
    ///
    /// Le corps du script est écrit sans accent, mais les chemins ne se choisissent pas :
    /// ils portent le nom du compte Windows. Le poste de Créteil est ouvert sous
    /// « PhotoConcept Créteil » ; son <c>é</c> écrit en UTF-8 (<c>C3 A9</c>) et relu par
    /// cmd.exe en page 850 devenait <c>├╣</c>. robocopy cherchait un dossier inexistant,
    /// et le bouton « Mettre à jour » ne faisait rien de visible — deux fois de suite,
    /// le 10/08/2026, sans qu'aucune trace n'en dise la cause.
    ///
    /// On relit donc le script comme cmd.exe le relira, et le chemin doit en ressortir
    /// intact.
    /// </summary>
    [Fact]
    public void Un_chemin_accentue_survit_a_la_page_de_codes_de_cmd()
    {
        const string installe = @"C:\Users\PhotoConcept Créteil\Desktop\StudioPhoto";

        var travail = Path.Combine(Path.GetTempPath(), "MajTest-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(travail, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Studio.App.dll"), "nouvelle version");

        var archive = Path.Combine(travail, "maj.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, archive);

        try
        {
            var script = MiseAJour.PreparerLInstallation(
                archive, installe, Path.Combine(installe, "Studio.App.exe"));

            var octets = File.ReadAllBytes(script);

            // le bug lui-même : « é » ne doit plus être écrit sur deux octets UTF-8
            Assert.DoesNotContain(0xC3, octets.Zip(octets.Skip(1))
                .Where(p => p.Second == 0xA9)
                .Select(p => (int)p.First));

            // et la vérification qui compte : relu dans la page OEM du poste — celle que
            // cmd.exe emploie — le chemin doit être exactement celui qu'on lui a donné
            System.Text.Encoding.RegisterProvider(
                System.Text.CodePagesEncodingProvider.Instance);
            var oem = System.Text.Encoding.GetEncoding(
                System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

            Assert.Contains(installe, oem.GetString(octets));
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

    // ===== UN DÉPÔT, DEUX APPLICATIONS =====
    //
    // Studio Photo est publié en « v1.5.19 », Studio Photo Identité en
    // « identite-v1.5.19 » — et ce dernier en PRÉVERSION, ce qui le tient hors de
    // /releases/latest pour que les postes du Studio ne le voient jamais. Chacun doit
    // donc trouver les siennes, et seulement les siennes.

    /// <summary>Deux publications d'Identité et une du Studio, comme GitHub les rend.</summary>
    private const string ListeDeDeuxLogiciels = """
    [
      { "tag_name": "v1.5.19", "name": "Version 1.5.19", "body": "le Studio",
        "draft": false, "prerelease": false,
        "assets": [ { "name": "StudioPhoto-1.5.19.zip", "size": 253000000,
                      "browser_download_url": "https://exemple/StudioPhoto-1.5.19.zip" } ] },
      { "tag_name": "identite-v1.5.18", "name": "Identité 1.5.18", "body": "l'avant-derniere",
        "draft": false, "prerelease": true,
        "assets": [ { "name": "StudioIdentite-1.5.18.zip", "size": 257000000,
                      "browser_download_url": "https://exemple/StudioIdentite-1.5.18.zip" } ] },
      { "tag_name": "identite-v1.5.19", "name": "Identité 1.5.19", "body": "la bonne",
        "draft": false, "prerelease": true,
        "assets": [ { "name": "StudioIdentite-1.5.19.zip", "size": 258000000,
                      "browser_download_url": "https://exemple/StudioIdentite-1.5.19.zip" } ] }
    ]
    """;

    [Fact]
    public void La_liste_rend_la_plus_recente_du_prefixe_demande()
    {
        var version = MiseAJour.LireLaListe(ListeDeDeuxLogiciels, "identite-v");

        Assert.NotNull(version);
        Assert.Equal(new Version(1, 5, 19), version.Version);
        Assert.Contains("StudioIdentite-1.5.19.zip", version.Url);
    }

    /// <summary>
    /// Le point qui compte : la publication du STUDIO ne doit jamais être proposée au poste
    /// identité, ni l'inverse. Elles vivent dans le même dépôt.
    /// </summary>
    [Fact]
    public void La_liste_ignore_les_publications_de_l_autre_logiciel()
    {
        var identite = MiseAJour.LireLaListe(ListeDeDeuxLogiciels, "identite-v");
        Assert.DoesNotContain("StudioPhoto", identite!.Url);
    }

    /// <summary>
    /// Les PRÉVERSIONS sont acceptées ici, à l'inverse de la dernière publication : c'est
    /// sous cette forme qu'Identité est publié, exprès.
    /// </summary>
    [Fact]
    public void Une_preversion_est_acceptee_dans_la_liste()
    {
        Assert.NotNull(MiseAJour.LireLaListe(ListeDeDeuxLogiciels, "identite-v"));
    }

    [Fact]
    public void Un_brouillon_est_ecarte()
    {
        const string liste = """
        [ { "tag_name": "identite-v9.9.9", "draft": true, "prerelease": true,
            "assets": [ { "name": "StudioIdentite-9.9.9.zip", "size": 1,
                          "browser_download_url": "https://exemple/x.zip" } ] } ]
        """;

        Assert.Null(MiseAJour.LireLaListe(liste, "identite-v"));
    }

    /// <summary>Une publication sans archive n'est pas installable : on ne la propose pas.</summary>
    [Fact]
    public void Une_publication_sans_archive_est_ecartee()
    {
        const string liste = """
        [ { "tag_name": "identite-v9.9.9", "draft": false, "prerelease": true, "assets": [] } ]
        """;

        Assert.Null(MiseAJour.LireLaListe(liste, "identite-v"));
    }

    [Fact]
    public void Une_liste_illisible_ne_leve_pas()
    {
        Assert.Null(MiseAJour.LireLaListe("{ pas du json", "identite-v"));
        Assert.Null(MiseAJour.LireLaListe("", "identite-v"));
    }
}
